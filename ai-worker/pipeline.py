"""Orchestrates one frame through vehicle detection -> plate detection -> OCR -> normalization.

Depends only on the three ABCs in `backends/base.py` — which concrete model or remote API is
behind each one is decided once at startup by `backends/registry.py` from `config.py`. This
module never imports a concrete backend and never branches on which one is active.
"""

from __future__ import annotations

import base64
import gzip
import logging
import threading
import uuid
from dataclasses import dataclass, field
from datetime import datetime, timezone
from pathlib import Path

import cv2
import numpy as np

from backends.base import (
    AnprProcessor,
    BoundingBox,
    PlateDetector,
    VehicleDetection,
    VehicleDetector,
)
from capture.base import Frame
from monitoring.metrics import (
    detections_total,
    errors_total,
    frames_processed_total,
    inference_latency_seconds,
    matches_total,
)
from plate_normalizer import correct_plate_format, is_plausible_plate, normalize_plate
from tracker import PlateReading, Track, VehicleTracker

logger = logging.getLogger(__name__)

ANPR_DETECTED = "ANPR_DETECTED"
VEHICLE_DETECTED = "VEHICLE_DETECTED"

# The vehicle box is often tight enough to clip a plate that sits at the very bottom edge;
# widen it slightly before looking for the plate inside.
_VEHICLE_BBOX_PAD_FRACTION = 0.08

# The plate detector's own bbox is cropped with zero margin before OCR — a real gap identified
# this session (expert review pointed at partial/garbled OCR reads, e.g. real detections logged
# as "4", "CQ4", "HHE", "Q4", as more likely a clipped-crop problem than an image-quality one): a
# tight detector box very commonly clips 1-2 pixels of the leftmost/rightmost character strokes,
# which is enough for OCR to drop or mangle an edge character. A plate box is small in absolute
# pixels, so this fraction translates to only a handful of pixels of margin in practice — enough
# breathing room for edge characters without pulling in enough surrounding background clutter to
# itself get read as spurious characters.
_PLATE_BBOX_PAD_FRACTION = 0.12

# ANPR evidence is stored in the backend's database (detection_snapshot, v1.30), not on this
# worker's own disk. Full resolution, high JPEG quality — deliberately not resized or
# quality-reduced (confirmed decision: evidence must stay pixel-faithful to the original capture,
# not just "good enough to read"). Size is instead bounded by lossless gzip on the encoded bytes
# (_gzip_encode below) — real savings there are modest (JPEG's own entropy coding already removes
# most redundancy a second lossless pass could find, typically ~2-10%), which is the accepted
# trade-off for keeping every pixel exactly as captured.
_EVIDENCE_JPEG_QUALITY = 95

# EasyOCR needs a reasonable pixel height to read; a plate crop straight off a distant vehicle
# is often only tens of pixels wide. Upscale small crops before OCR.
_OCR_MIN_CROP_WIDTH = 240

# Most Indian CCTV/DVR streams burn a timestamp + location text banner across the very top of
# the frame — a wide, flat, high-contrast rectangle that HeuristicPlateDetector's pure aspect-
# ratio/area geometry heuristic (backends/plate/heuristic_cv.py) cannot distinguish from an
# actual plate, since it has no semantic understanding of the pixels, only their contour shape.
# Applied unconditionally to every plate-search region, not only a vehicle-detector miss: a real
# vehicle detected close to the top of frame (a camera angle where traffic passes directly behind
# the overlay) still produces a padded search box that reaches into the banner otherwise, and a
# geometry-only detector reads the banner text just as readily as it would a genuine plate.
_OSD_EXCLUSION_TOP_FRACTION = 0.12


def _pad(bbox: BoundingBox, image_shape: tuple, fraction: float) -> BoundingBox:
    """Widens `bbox` by `fraction` of its own size on every side, clamped to the image bounds.
    No OSD/frame-level logic here — that's `_pad_bbox`'s own addition, for the vehicle-region
    case specifically; this is the plain geometric operation both padding needs share."""
    h, w = image_shape[:2]
    pad_x = int((bbox.x2 - bbox.x1) * fraction)
    pad_y = int((bbox.y2 - bbox.y1) * fraction)
    return BoundingBox(
        max(0, bbox.x1 - pad_x), max(0, bbox.y1 - pad_y),
        min(w, bbox.x2 + pad_x), min(h, bbox.y2 + pad_y),
    )


def _pad_bbox(bbox: BoundingBox, image_shape: tuple, fraction: float) -> BoundingBox:
    padded = _pad(bbox, image_shape, fraction)
    osd_band = round(image_shape[0] * _OSD_EXCLUSION_TOP_FRACTION)
    return BoundingBox(padded.x1, max(osd_band, padded.y1), padded.x2, padded.y2)


def _upscale_for_ocr(crop: np.ndarray) -> np.ndarray:
    if crop.size == 0:
        return crop
    ch, cw = crop.shape[:2]
    if cw == 0 or cw >= _OCR_MIN_CROP_WIDTH:
        return crop
    scale = _OCR_MIN_CROP_WIDTH / cw
    return cv2.resize(crop, (int(cw * scale), int(ch * scale)), interpolation=cv2.INTER_CUBIC)


@dataclass(frozen=True)
class DetectionEvent:
    """The normalized shape the (future) Trinetra ingest endpoint consumes — brief §7."""

    camera_id: str
    event_type: str
    timestamp: datetime
    confidence: float
    attributes: dict[str, str] = field(default_factory=dict)
    evidence: dict[str, str] = field(default_factory=dict)
    id: str = field(default_factory=lambda: f"evt-{uuid.uuid4().hex}")

    def to_json(self) -> dict:
        return {
            "id": self.id,
            "cameraId": self.camera_id,
            "eventType": self.event_type,
            "timestamp": self.timestamp.astimezone(timezone.utc).isoformat().replace("+00:00", "Z"),
            "confidence": round(self.confidence, 4),
            "attributes": self.attributes,
            "evidence": self.evidence,
        }


class Pipeline:
    def __init__(
        self,
        vehicle_detector: VehicleDetector,
        plate_detector: PlateDetector,
        ocr_processor: AnprProcessor,
        evidence_dir: str | Path,
    ) -> None:
        self._vehicle_detector = vehicle_detector
        self._plate_detector = plate_detector
        self._ocr_processor = ocr_processor
        self._evidence_dir = Path(evidence_dir)
        self._evidence_dir.mkdir(parents=True, exist_ok=True)

        # One VehicleTracker per camera (tracker.py) — a track id is only meaningful within the
        # frames of the one camera it came from. Only ever touched by that camera's own capture
        # thread once created, but the dict itself is shared across all cameras' threads (one
        # Pipeline instance per worker process, per worker.py), so creation is lock-guarded.
        self._trackers: dict[str, VehicleTracker] = {}
        self._trackers_lock = threading.Lock()

    def _tracker_for(self, camera_id: str) -> VehicleTracker:
        tracker = self._trackers.get(camera_id)
        if tracker is None:
            with self._trackers_lock:
                tracker = self._trackers.setdefault(camera_id, VehicleTracker())
        return tracker

    def process_frame(
        self, camera_id: str, frame: Frame, enabled_models: list[str]
    ) -> list[DetectionEvent]:
        """Runs detection/OCR for this frame's vehicles and feeds the results into this
        camera's `VehicleTracker` — but a `DetectionEvent` is only actually produced when a
        track finalizes (the vehicle has been out of frame for a few consecutive samples), not
        on every frame. See `tracker.py`'s own docstring for why: voting several frames' reads
        of the same vehicle together is far more reliable than trusting any single frame, and it
        collapses what used to be one duplicate event per sampled frame into one per vehicle
        sighting."""
        tracker = self._tracker_for(camera_id)

        try:
            with inference_latency_seconds.labels(stage="vehicle").time():
                vehicles = self._vehicle_detector.detect(frame.image)
        except Exception:
            errors_total.labels(stage="vehicle").inc()
            logger.exception("Vehicle detection failed for camera %s", camera_id)
            vehicles = []

        # A frame with no detected vehicle produces no plate search at all — no full-frame
        # fallback. That fallback used to scan the entire frame when no vehicle was found, on the
        # theory that a fixed ANPR lane camera or a bench-test plate held to the lens has no real
        # vehicle to detect. In practice it was the root cause of two distinct false-positive
        # classes against a real stream: a geometry-only plate detector has no semantic
        # understanding, so it matched the camera's own on-screen timestamp/location text overlay
        # (a wide, flat, high-contrast rectangle) just as readily as a real plate, and — worse —
        # a visibly corrupted decode frame's blocky concealment artifacts just as readily as
        # either. A plate search anchored to an actually-detected vehicle's own bounding box
        # never reaches far enough into either of those regions to be fooled by them.
        logger.debug("camera %s: %d vehicle region(s)", camera_id, len(vehicles))

        assignments = tracker.update(vehicles, frame.captured_at)

        for track_id, vehicle in assignments.items():
            region = _pad_bbox(vehicle.bbox, frame.image.shape, _VEHICLE_BBOX_PAD_FRACTION)
            vehicle_crop = region.crop(frame.image)

            if "plate" in enabled_models and vehicle_crop.size > 0:
                self._read_plate(camera_id, track_id, tracker, frame, region, vehicle_crop)
            elif "vehicle" in enabled_models:
                snapshot_path = self._save_evidence(camera_id, vehicle_crop)
                tracker.record_plateless(track_id, snapshot_path, vehicle.confidence)
            # else: ANPR-only camera (enabled_models has "plate" but no "vehicle") with plate
            # detection skipped this frame (empty crop) — nothing to record either way; the next
            # frame gets another chance before this track's miss counter finalizes it.

        events = [
            e for t in tracker.pop_finalized()
            if (e := self._finalize_track(camera_id, t, enabled_models)) is not None
        ]

        frames_processed_total.labels(camera_id=camera_id).inc()
        return events

    def flush_camera(self, camera_id: str, enabled_models: list[str]) -> list[DetectionEvent]:
        """Finalizes every track still open for this camera, regardless of staleness — call
        when its capture loop stops (claim released, worker shutting down) so a vehicle that was
        still in frame doesn't silently lose whatever consensus it had built up so far.
        `enabled_models` must be that camera's own config — it decides whether a plateless
        leftover track is worth a VEHICLE_DETECTED event, same as during normal processing."""
        tracker = self._trackers.pop(camera_id, None)
        if tracker is None:
            return []

        return [
            e for t in tracker.pop_all()
            if (e := self._finalize_track(camera_id, t, enabled_models)) is not None
        ]

    def _read_plate(
        self, camera_id: str, track_id: int, tracker: VehicleTracker, frame: Frame,
        region: BoundingBox, vehicle_crop: np.ndarray,
    ) -> None:
        try:
            with inference_latency_seconds.labels(stage="plate").time():
                plates = self._plate_detector.detect(vehicle_crop)
        except Exception:
            errors_total.labels(stage="plate").inc()
            logger.exception("Plate detection failed for camera %s", camera_id)
            return

        logger.debug("camera %s: %d plate candidate(s) in region", camera_id, len(plates))
        if not plates:
            return

        best_plate = max(plates, key=lambda p: p.confidence)
        plate_region = _pad(best_plate.bbox, vehicle_crop.shape, _PLATE_BBOX_PAD_FRACTION)
        plate_crop = _upscale_for_ocr(plate_region.crop(vehicle_crop))
        try:
            with inference_latency_seconds.labels(stage="ocr").time():
                anpr = self._ocr_processor.read(plate_crop)
        except Exception:
            errors_total.labels(stage="ocr").inc()
            logger.exception("OCR failed for camera %s", camera_id)
            return

        if anpr is None:
            return

        normalized = normalize_plate(anpr.text)
        if not normalized:
            return

        corrected = correct_plate_format(normalized)
        logger.debug(
            "camera %s: OCR '%s' (conf %.2f) -> '%s' -> '%s'",
            camera_id, anpr.text, anpr.confidence, normalized, corrected,
        )

        if not is_plausible_plate(corrected):
            # Confirmed real misreads this session ("4", "CQ4", "HHE", "Q4") are all 2-4
            # characters — nowhere near a real plate's shape. Reject rather than store/act on a
            # fragment, regardless of the OCR confidence score attached to it (a short garbled
            # read can still score moderately "confident").
            logger.debug(
                "camera %s: '%s' rejected — not plate-shaped", camera_id, corrected)
            return

        normalized = corrected

        # Full frame with the plate boxed and labelled, not just the plate crop — an operator
        # reviewing an alert needs the vehicle/scene context, not an isolated close-up.
        # best_plate.bbox is in vehicle_crop's own pixel space; offset by region's own top-left
        # to place the box correctly on the full frame.
        plate_bbox_on_frame = BoundingBox(
            region.x1 + best_plate.bbox.x1, region.y1 + best_plate.bbox.y1,
            region.x1 + best_plate.bbox.x2, region.y1 + best_plate.bbox.y2,
        )
        snapshot_base64 = self._save_annotated_evidence(
            camera_id, frame.image, plate_bbox_on_frame, normalized)

        tracker.record_plate(track_id, PlateReading(
            text=normalized, confidence=anpr.confidence,
            snapshot_base64=snapshot_base64, occurred_at=frame.captured_at,
        ))

    def _finalize_track(
        self, camera_id: str, track: Track, enabled_models: list[str]
    ) -> DetectionEvent | None:
        winner = track.consensus()

        if winner is not None:
            confidence = min(track.vehicle_confidence, winner.confidence)
            event = DetectionEvent(
                camera_id=camera_id,
                event_type=ANPR_DETECTED,
                timestamp=winner.occurred_at,
                confidence=confidence,
                attributes={"vehicleType": track.vehicle_type, "plateNumber": winner.text},
                evidence={"snapshotBase64": winner.snapshot_base64, "snapshotEncoding": "gzip"}
                if winner.snapshot_base64 else {},
            )
            matches_total.labels(camera_id=camera_id).inc()
        elif "vehicle" not in enabled_models or track.plateless_snapshot is None:
            # A camera configured for ANPR only (enabled_models without "vehicle") skips the
            # plateless fallback entirely — a tracked vehicle with no readable plate across its
            # whole sighting simply produces no event, rather than a VEHICLE_DETECTED the
            # operator didn't ask for. Also skipped if nothing was ever actually recorded for
            # this track (e.g. every frame's crop came back empty).
            return None
        else:
            event = DetectionEvent(
                camera_id=camera_id,
                event_type=VEHICLE_DETECTED,
                timestamp=track.last_seen,
                confidence=track.vehicle_confidence,
                attributes={"vehicleType": track.vehicle_type},
                evidence={"snapshotPath": track.plateless_snapshot},
            )

        detections_total.labels(camera_id=camera_id, event_type=event.event_type).inc()
        return event

    def _save_evidence(self, camera_id: str, image: np.ndarray | None) -> str | None:
        if image is None or image.size == 0:
            return None

        filename = f"{camera_id}-{uuid.uuid4().hex}.jpg"
        path = self._evidence_dir / filename
        if not cv2.imwrite(str(path), image):
            logger.warning("Failed to write evidence snapshot %s", path)
            return None

        return str(path)

    def _save_annotated_evidence(
        self, camera_id: str, full_frame: np.ndarray, plate_bbox: BoundingBox, plate_text: str,
    ) -> str | None:
        """The whole frame (not a crop), full resolution, with a box drawn around the plate and
        the plate text labelled above it — an ANPR alert needs the vehicle/scene context, not
        just an isolated plate close-up, and evidence stays pixel-faithful to the original
        capture rather than resized or quality-reduced. Returns the gzip-compressed JPEG,
        base64-encoded (never a file path — a plate detection's evidence goes straight into
        `detection_snapshot` via the ingest endpoint's `evidence.snapshotBase64` +
        `evidence.snapshotEncoding=gzip`, not the worker's local disk, since a plate alert is
        exactly the evidence that must survive independently of whatever machine this worker
        happens to run on). Never raises: drawing on a copy of the frame so the caller's own array
        is untouched, and any failure just falls back to no snapshot."""
        if full_frame is None or full_frame.size == 0:
            return None

        annotated = full_frame.copy()
        x1, y1, x2, y2 = plate_bbox.x1, plate_bbox.y1, plate_bbox.x2, plate_bbox.y2

        box_color = (0, 220, 0)  # BGR — green, readable against most road-scene backgrounds
        box_thickness = max(2, round(annotated.shape[0] / 360))  # scales with frame resolution
        cv2.rectangle(annotated, (x1, y1), (x2, y2), box_color, box_thickness)

        font = cv2.FONT_HERSHEY_SIMPLEX
        font_scale = max(0.6, annotated.shape[0] / 720)
        text_thickness = max(1, box_thickness - 1)
        (text_w, text_h), baseline = cv2.getTextSize(plate_text, font, font_scale, text_thickness)

        # Label sits just above the box; if that would go off the top edge (a plate near the top
        # of frame), place it just below the box instead so it's never clipped.
        pad = 6
        label_y2 = y1 - pad
        label_y1 = label_y2 - text_h - baseline - pad
        if label_y1 < 0:
            label_y1 = y2 + pad
            label_y2 = label_y1 + text_h + baseline + pad

        label_x1 = max(0, x1)
        label_x2 = min(annotated.shape[1], label_x1 + text_w + 2 * pad)

        # A filled background behind the text keeps it legible over a busy/bright frame —
        # plain text drawn straight onto the image can disappear against light road surfaces.
        cv2.rectangle(annotated, (label_x1, label_y1), (label_x2, label_y2), box_color, -1)
        cv2.putText(
            annotated, plate_text, (label_x1 + pad, label_y2 - pad - baseline // 2),
            font, font_scale, (0, 0, 0), text_thickness, cv2.LINE_AA,
        )

        ok, buffer = cv2.imencode(".jpg", annotated, [cv2.IMWRITE_JPEG_QUALITY, _EVIDENCE_JPEG_QUALITY])
        if not ok:
            logger.warning("Failed to encode annotated evidence snapshot for camera %s", camera_id)
            return None

        return base64.b64encode(gzip.compress(buffer.tobytes())).decode("ascii")
