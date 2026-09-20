"""IoU-based multi-object tracking for ANPR temporal consensus.

Associates vehicle detections across frames into short-lived tracks (one physical vehicle
sighting), so plate reads from several frames of the SAME vehicle can be combined into one
consensus detection instead of the pipeline emitting one independent — and independently noisy —
event per frame. A single frame's OCR read is inherently noisy (motion blur, glare, partial
occlusion, a corrupted decode block); several reads of the same vehicle voting together are far
more reliable than any one of them alone. This also collapses what used to be ~5-15 duplicate
events per vehicle (one per sampled frame it was in view) into one.

Deliberately a plain greedy IoU tracker, not a Kalman-filter tracker like SORT/DeepSORT: at 2 fps
sampling and this camera's field of view, consecutive frames' boxes for the same vehicle overlap
heavily and a velocity model buys little. IoU matching needs no extra dependency and is easy to
reason about — swap it out later if a faster/denser deployment needs the real thing.
"""

from __future__ import annotations

import itertools
from dataclasses import dataclass, field
from datetime import datetime

from backends.base import BoundingBox, VehicleDetection


def _iou(a: BoundingBox, b: BoundingBox) -> float:
    x1, y1 = max(a.x1, b.x1), max(a.y1, b.y1)
    x2, y2 = min(a.x2, b.x2), min(a.y2, b.y2)
    inter = max(0, x2 - x1) * max(0, y2 - y1)
    if inter == 0:
        return 0.0
    area_a = (a.x2 - a.x1) * (a.y2 - a.y1)
    area_b = (b.x2 - b.x1) * (b.y2 - b.y1)
    union = area_a + area_b - inter
    return inter / union if union > 0 else 0.0


@dataclass(frozen=True)
class PlateReading:
    text: str
    confidence: float
    # A base64-encoded JPEG (v1.30's evidence.snapshotBase64), not a file path — an ANPR
    # detection's evidence is stored in the backend's database, not this worker's own disk.
    snapshot_base64: str | None
    occurred_at: datetime


# Bounds memory for a vehicle that dwells in frame a long time (idling, stopped in traffic) —
# far more readings than this adds nothing to the consensus vote, since a handful of agreeing
# reads already dominates the vote long before this cap is reached.
_MAX_READINGS_PER_TRACK = 50


@dataclass
class Track:
    track_id: int
    bbox: BoundingBox
    vehicle_type: str
    vehicle_confidence: float
    first_seen: datetime
    last_seen: datetime
    missed_frames: int = 0
    plate_readings: list[PlateReading] = field(default_factory=list)
    plateless_snapshot: str | None = None
    plateless_confidence: float = 0.0

    def add_plate_reading(self, reading: PlateReading) -> None:
        if len(self.plate_readings) >= _MAX_READINGS_PER_TRACK:
            self.plate_readings.pop(0)
        self.plate_readings.append(reading)

    def consensus(self) -> PlateReading | None:
        """Majority-vote plate text across every frame this track was seen, weighted by OCR
        confidence — a text read on several frames (even each imperfectly) beats one read
        confidently on a single frame but never repeated, since a transient misread (glare,
        motion blur, a corrupted decode block) rarely repeats identically across frames. Ties
        broken by which candidate's readings carry the single highest individual confidence.
        Returns the highest-confidence individual reading for the winning text, so its own
        snapshot (not a synthetic composite) is what gets attached as evidence."""
        if not self.plate_readings:
            return None

        by_text: dict[str, list[PlateReading]] = {}
        for r in self.plate_readings:
            by_text.setdefault(r.text, []).append(r)

        def total_weight(readings: list[PlateReading]) -> float:
            return sum(r.confidence for r in readings)

        best_text = max(by_text, key=lambda t: (total_weight(by_text[t]), max(r.confidence for r in by_text[t])))
        best_readings = by_text[best_text]
        best_single = max(best_readings, key=lambda r: r.confidence)
        avg_confidence = total_weight(best_readings) / len(best_readings)

        return PlateReading(
            text=best_text, confidence=avg_confidence,
            snapshot_base64=best_single.snapshot_base64, occurred_at=best_single.occurred_at,
        )


class VehicleTracker:
    """One instance per camera — tracks are meaningless across different cameras' frames, so
    `Pipeline` keeps a separate tracker per `camera_id` (see its own docstring)."""

    _IOU_MATCH_THRESHOLD = 0.3
    # ~2.5s of dwell tolerance at 2fps before a track is finalized and its consensus event
    # emitted — long enough to survive a couple of missed/corrupted frames, short enough that a
    # vehicle's event isn't held back for long after it actually leaves frame.
    _MAX_MISSED_FRAMES = 5

    def __init__(self) -> None:
        self._tracks: dict[int, Track] = {}
        self._next_id = itertools.count(1)

    def update(self, vehicles: list[VehicleDetection], now: datetime) -> dict[int, VehicleDetection]:
        """Matches this frame's vehicle detections against existing tracks (greedy best-IoU
        first), creating a new track for anything unmatched. Returns {track_id: matched
        VehicleDetection} for the caller to run plate detection/OCR against and feed back via
        `record_plate`/`record_plateless`."""
        unmatched_tracks = set(self._tracks.keys())
        assignments: dict[int, VehicleDetection] = {}

        # Greedy highest-IoU-first matching across every (track, detection) pair — simple and
        # sufficient for the handful of vehicles typically in frame at once; a real Hungarian
        # assignment would be solving a much bigger problem than this one has.
        pairs = sorted(
            (
                (_iou(self._tracks[tid].bbox, v.bbox), tid, vi)
                for tid in unmatched_tracks
                for vi, v in enumerate(vehicles)
            ),
            reverse=True,
        )

        matched_detections: set[int] = set()
        for iou, tid, vi in pairs:
            if iou < self._IOU_MATCH_THRESHOLD:
                break
            if tid not in unmatched_tracks or vi in matched_detections:
                continue
            track = self._tracks[tid]
            v = vehicles[vi]
            track.bbox = v.bbox
            track.vehicle_confidence = max(track.vehicle_confidence, v.confidence)
            track.last_seen = now
            track.missed_frames = 0
            assignments[tid] = v
            unmatched_tracks.discard(tid)
            matched_detections.add(vi)

        for vi, v in enumerate(vehicles):
            if vi in matched_detections:
                continue
            tid = next(self._next_id)
            self._tracks[tid] = Track(
                track_id=tid, bbox=v.bbox, vehicle_type=v.vehicle_type,
                vehicle_confidence=v.confidence, first_seen=now, last_seen=now,
            )
            assignments[tid] = v

        for tid in unmatched_tracks:
            self._tracks[tid].missed_frames += 1

        return assignments

    def record_plate(self, track_id: int, reading: PlateReading) -> None:
        track = self._tracks.get(track_id)
        if track is not None:
            track.add_plate_reading(reading)

    def record_plateless(self, track_id: int, snapshot_path: str | None, confidence: float) -> None:
        """Remembers the best (highest-confidence) plateless sighting of this track, for the
        VEHICLE_DETECTED fallback event if it never gets a plate reading."""
        track = self._tracks.get(track_id)
        if track is not None and confidence >= track.plateless_confidence:
            track.plateless_snapshot = snapshot_path
            track.plateless_confidence = confidence

    def pop_finalized(self) -> list[Track]:
        """Removes and returns every track that has gone stale (missed too many consecutive
        frames) — the caller turns each into one consensus detection event."""
        finalized = [t for t in self._tracks.values() if t.missed_frames > self._MAX_MISSED_FRAMES]
        for t in finalized:
            del self._tracks[t.track_id]
        return finalized

    def pop_all(self) -> list[Track]:
        """Every remaining track, regardless of staleness — call when a camera's capture loop
        stops (claim released, worker shutting down) so nothing in flight is silently dropped."""
        remaining = list(self._tracks.values())
        self._tracks.clear()
        return remaining
