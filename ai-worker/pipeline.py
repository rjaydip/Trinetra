"""Orchestrates one frame through vehicle detection -> plate detection -> OCR -> normalization.

Depends only on the three ABCs in `backends/base.py` — which concrete model or remote API is
behind each one is decided once at startup by `backends/registry.py` from `config.py`. This
module never imports a concrete backend and never branches on which one is active.
"""

from __future__ import annotations

import logging
import uuid
from dataclasses import dataclass, field
from datetime import datetime, timezone
from pathlib import Path

import cv2
import numpy as np

from backends.base import AnprProcessor, PlateDetector, VehicleDetector
from capture.base import Frame
from monitoring.metrics import (
    detections_total,
    errors_total,
    frames_processed_total,
    inference_latency_seconds,
    matches_total,
)
from plate_normalizer import normalize_plate

logger = logging.getLogger(__name__)

ANPR_DETECTED = "ANPR_DETECTED"
VEHICLE_DETECTED = "VEHICLE_DETECTED"


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

    def process_frame(
        self, camera_id: str, frame: Frame, enabled_models: list[str]
    ) -> list[DetectionEvent]:
        events: list[DetectionEvent] = []

        try:
            with inference_latency_seconds.labels(stage="vehicle").time():
                vehicles = self._vehicle_detector.detect(frame.image)
        except Exception:
            errors_total.labels(stage="vehicle").inc()
            logger.exception("Vehicle detection failed for camera %s", camera_id)
            return events

        for vehicle in vehicles:
            vehicle_crop = vehicle.bbox.crop(frame.image)
            plate_text: str | None = None
            plate_confidence = 0.0
            snapshot_path: str | None = None

            if "plate" in enabled_models and vehicle_crop.size > 0:
                try:
                    with inference_latency_seconds.labels(stage="plate").time():
                        plates = self._plate_detector.detect(vehicle_crop)
                except Exception:
                    errors_total.labels(stage="plate").inc()
                    logger.exception("Plate detection failed for camera %s", camera_id)
                    plates = []

                if plates and "ocr" in enabled_models:
                    best_plate = max(plates, key=lambda p: p.confidence)
                    plate_crop = best_plate.bbox.crop(vehicle_crop)
                    try:
                        with inference_latency_seconds.labels(stage="ocr").time():
                            anpr = self._ocr_processor.read(plate_crop)
                    except Exception:
                        errors_total.labels(stage="ocr").inc()
                        logger.exception("OCR failed for camera %s", camera_id)
                        anpr = None

                    if anpr is not None:
                        normalized = normalize_plate(anpr.text)
                        if normalized:
                            plate_text = normalized
                            plate_confidence = anpr.confidence
                            snapshot_path = self._save_evidence(camera_id, plate_crop)

            if plate_text:
                event = DetectionEvent(
                    camera_id=camera_id,
                    event_type=ANPR_DETECTED,
                    timestamp=frame.captured_at,
                    confidence=min(vehicle.confidence, plate_confidence),
                    attributes={"vehicleType": vehicle.vehicle_type, "plateNumber": plate_text},
                    evidence={"snapshotPath": snapshot_path} if snapshot_path else {},
                )
                matches_total.labels(camera_id=camera_id).inc()
            else:
                snapshot_path = self._save_evidence(camera_id, vehicle_crop)
                event = DetectionEvent(
                    camera_id=camera_id,
                    event_type=VEHICLE_DETECTED,
                    timestamp=frame.captured_at,
                    confidence=vehicle.confidence,
                    attributes={"vehicleType": vehicle.vehicle_type},
                    evidence={"snapshotPath": snapshot_path} if snapshot_path else {},
                )

            detections_total.labels(camera_id=camera_id, event_type=event.event_type).inc()
            events.append(event)

        frames_processed_total.labels(camera_id=camera_id).inc()
        return events

    def _save_evidence(self, camera_id: str, image: np.ndarray | None) -> str | None:
        if image is None or image.size == 0:
            return None

        filename = f"{camera_id}-{uuid.uuid4().hex}.jpg"
        path = self._evidence_dir / filename
        if not cv2.imwrite(str(path), image):
            logger.warning("Failed to write evidence snapshot %s", path)
            return None

        return str(path)
