"""Metrics the operator actually needs to decide "add a GPU" vs "add a worker".

Exposed in Prometheus text format (`monitoring/app.py`'s `/metrics`) because that's a standard
any consumer — a real Prometheus server, a one-off `curl`, or the eventual .NET receiver in
Phase 2 — can scrape without this worker needing to know who's asking.

Reading the signal:
  - `ai_worker_inference_latency_seconds` climbing on one worker while its camera count is
    fixed -> that worker (or its device) is under-powered -> scale vertically (GPU) or move
    cameras off it.
  - `ai_worker_frames_dropped_total` climbing -> the worker cannot keep up with
    `PROCESSING_FPS` for its assigned cameras -> scale horizontally (add a worker, rebalance
    `WORKER_COUNT`).
"""

from __future__ import annotations

from prometheus_client import Counter, Gauge, Histogram

frames_captured_total = Counter(
    "ai_worker_frames_captured_total", "Frames read from a camera source.", ["camera_id"]
)
frames_processed_total = Counter(
    "ai_worker_frames_processed_total", "Frames run through the full detection pipeline.",
    ["camera_id"],
)
frames_dropped_total = Counter(
    "ai_worker_frames_dropped_total", "Frames captured but not processed.",
    ["camera_id", "reason"],
)

inference_latency_seconds = Histogram(
    "ai_worker_inference_latency_seconds", "Per-stage inference latency.", ["stage"],
)

detections_total = Counter(
    "ai_worker_detections_total", "Normalized detections produced.", ["camera_id", "event_type"],
)
matches_total = Counter(
    "ai_worker_watchlist_candidate_total",
    "Detections whose plate text was non-empty (a Phase-2 watchlist match candidate).",
    ["camera_id"],
)

errors_total = Counter(
    "ai_worker_errors_total", "Errors raised by a pipeline stage.", ["stage"],
)

active_cameras = Gauge(
    "ai_worker_active_cameras", "Cameras this worker instance currently owns.",
)
worker_info = Gauge(
    "ai_worker_info", "Static worker identity, as labels; value is always 1.",
    ["worker_index", "worker_count", "device"],
)

# One row per currently-claimed camera, so an operator (or a Prometheus/Grafana alert) can see
# exactly which cameras this worker instance is watching, not just how many — the same
# information the backend's own worker-health view surfaces (leasedCameraNames), duplicated here
# for a consumer that only ever talks to this worker's own /metrics, not the backend API. Cleared
# and fully rebuilt on every claim-loop tick (worker.py) rather than incrementally updated: a
# camera no longer claimed must disappear from this gauge, not linger at whatever value it last
# had — `.clear()` before re-setting is what makes that correct instead of accumulating stale
# label combinations for cameras this worker gave up minutes or hours ago.
claimed_camera = Gauge(
    "ai_worker_claimed_camera",
    "1 for each camera this worker instance currently holds a lease on.",
    ["camera_id", "camera_name"],
)
