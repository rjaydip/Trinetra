"""Entry point for a running worker instance: capture + inference + monitoring.

One process per instance. With `CAMERA_SOURCE=trinetra` and an API key configured, camera
ownership is dynamic (`lease_client.py`, v1.27/v1.28): a background thread polls
`POST /cameras/claim` on an interval well inside the backend's 90s lease TTL, and
`CameraWorkerPool` starts/stops per-camera capture threads to match whatever that poll
returns — VMS-discovered cameras first, then standalone registry ones, up to
`WORKER_CAPACITY`. If this instance stops polling (crash, network partition), its leases go
stale and the next worker that polls picks up the freed cameras; a clean shutdown calls
`POST /cameras/release` so that handoff doesn't have to wait out the TTL.

Without an API key (`CAMERA_SOURCE=static`, or `trinetra` with no key), camera ownership falls
back to `scaling.py`'s static hash-partition, decided once at startup — there is no backend to
claim cameras from in that mode.

The main thread serves `/health` and `/metrics` either way, so monitoring stays up even if a
camera's loop is stuck.
"""

from __future__ import annotations

import logging
import os
import platform
import sys
import threading

import httpx
import uvicorn

from backend_client import BackendClient
from backends.registry import build_ocr_processor, build_plate_detector, build_vehicle_detector
from camera_source import CameraConfig, TrinetraApiCameraSource, build_camera_source
from capture.registry import build_frame_source
from config import settings
from detection_batch import DetectionBatcher
from lease_client import CameraLeaseClient
from monitoring.app import app as metrics_app
from monitoring.heartbeat import HeartbeatSender
from monitoring.metrics import active_cameras, claimed_camera, frames_captured_total, worker_info
from pipeline import ANPR_DETECTED, DetectionEvent, Pipeline
from scaling import assigned_cameras

from capture.rtsp_url import redact


class _RedactRtspCredentials(logging.Filter):
    """Strip `user:pass@` userinfo from any RTSP URL before a record is emitted.

    The capture backends log the stream URL in several places (connect, reconnect, PTS
    discontinuity); once credentials are injected that URL carries the secret. Redacting at
    the logging layer covers every call site without threading a redacted copy through each
    backend.
    """

    def filter(self, record: logging.LogRecord) -> bool:
        try:
            record.msg = redact(record.getMessage())
            record.args = ()
        except Exception:  # noqa: BLE001 - logging must never raise
            pass
        return True


# `capture.rtsp_source` redirects the OS-level stderr file descriptor (fd 2) for the whole
# duration of a camera's RTSP session, to silence FFmpeg's own decoder-internal chatter
# ("Could not find ref with POC #", "The cu_qp_delta # is outside the valid range", etc.) — that
# C-level library writes straight to fd 2 with no Python-reachable log-level knob. Since fd
# redirection is process-wide, not per-thread, it would just as easily swallow this worker's own
# `logger.*` calls from any other thread while a capture session has fd 2 redirected — nearly all
# the time, once any camera is running. Duplicating the real terminal's fd here, before anything
# redirects fd 2, and binding logging to that duplicate instead of the live fd 2 makes this
# worker's own log output immune to that redirection, whatever else is happening to fd 2.
_real_stderr = os.fdopen(os.dup(sys.stderr.fileno()), "w", buffering=1)

logging.basicConfig(
    level=getattr(logging, settings.log_level.upper(), logging.INFO),
    format="%(asctime)s %(levelname)s %(name)s: %(message)s",
    stream=_real_stderr,
)
# On the handler, not the logger: a logger's own filters are skipped for records that reach
# it by propagation from a child logger, but a handler's filters run for every record it emits.
for _handler in logging.getLogger().handlers:
    _handler.addFilter(_RedactRtspCredentials())
logger = logging.getLogger("worker")


class _NullBatcher:
    """Stand-in for `DetectionBatcher` when no backend is configured (standalone/offline run) —
    routes straight to `BackendClient`, which already no-ops and logs in that case. Keeps
    `_submit_event`/`CameraWorkerPool` from needing an `Optional[DetectionBatcher]` check at
    every call site."""

    def __init__(self, backend_client: BackendClient) -> None:
        self._backend_client = backend_client

    def submit(self, event: DetectionEvent) -> None:
        self._backend_client.submit(event)

    def stop(self, drain_timeout: float = 10.0) -> None:
        pass


_AnyBatcher = DetectionBatcher | _NullBatcher


def _submit_event(
    event: DetectionEvent, backend_client: BackendClient, batcher: _AnyBatcher
) -> None:
    """Routes one detection to the immediate single-POST path or the batching queue.

    A plate read (`ANPR_DETECTED`) always goes out immediately — this worker has no local
    watchlist copy to check a match against itself, so every plate read is treated as
    potentially time-sensitive (confirmed design decision, `detection_batch.py`'s own docstring).
    A routine `VEHICLE_DETECTED` (no plate) is eligible for batching."""
    if event.event_type == ANPR_DETECTED:
        backend_client.submit(event)
    else:
        batcher.submit(event)


class _CameraWorker:
    """One camera's capture + inference loop, running on its own thread so it can be started
    and stopped independently as the claimed camera set changes."""

    def __init__(
        self, pipeline: Pipeline, backend_client: BackendClient, batcher: _AnyBatcher,
        camera: CameraConfig,
    ) -> None:
        self._pipeline = pipeline
        self._backend_client = backend_client
        self._batcher = batcher
        self._camera = camera
        self._source = None
        self._thread = threading.Thread(
            target=self._run, daemon=True, name=f"camera-{camera.camera_id}"
        )

    def start(self) -> None:
        self._thread.start()

    def stop(self, timeout: float = 5.0) -> None:
        # Closing the source unblocks a frames() call that is blocked on a network read; the
        # thread's own `with` block then exits cleanly via FrameSource.__exit__.
        if self._source is not None:
            self._source.close()
        self._thread.join(timeout=timeout)
        if self._thread.is_alive():
            logger.warning(
                "Capture thread for camera %s did not stop within %.0fs", self._camera.camera_id, timeout
            )

    def _run(self) -> None:
        fps = self._camera.processing_fps or settings.processing_fps
        logger.info(
            "Starting capture loop for %s (%s) at %.1f fps",
            self._camera.camera_id, self._camera.name, fps,
        )
        try:
            with build_frame_source(settings, self._camera.rtsp_url, processing_fps=fps) as source:
                self._source = source
                for frame in source.frames():
                    frames_captured_total.labels(camera_id=self._camera.camera_id).inc()

                    try:
                        events = self._pipeline.process_frame(
                            self._camera.camera_id, frame, self._camera.enabled_models
                        )
                    except Exception:
                        logger.exception(
                            "Unhandled pipeline error for camera %s", self._camera.camera_id
                        )
                        continue

                    for event in events:
                        _submit_event(event, self._backend_client, self._batcher)
        except Exception:
            logger.exception("Capture loop for camera %s exited with an error", self._camera.camera_id)
        finally:
            # A vehicle still mid-track when this camera's capture loop stops (claim released,
            # pool resync, worker shutdown) would otherwise lose whatever consensus it had built
            # up so far — flush_camera finalizes and returns it instead of silently dropping it.
            for event in self._pipeline.flush_camera(self._camera.camera_id, self._camera.enabled_models):
                _submit_event(event, self._backend_client, self._batcher)


class CameraWorkerPool:
    """Starts/stops `_CameraWorker` threads to match whatever camera set is currently wanted —
    the static assignment (decided once) or the dynamic claim loop (re-synced on every poll)
    both just call `sync()`."""

    def __init__(
        self, pipeline: Pipeline, backend_client: BackendClient, batcher: _AnyBatcher
    ) -> None:
        self._pipeline = pipeline
        self._backend_client = backend_client
        self._batcher = batcher
        self._lock = threading.Lock()
        self._workers: dict[str, _CameraWorker] = {}

    def sync(self, cameras: list[CameraConfig]) -> None:
        wanted = {c.camera_id: c for c in cameras}
        with self._lock:
            for camera_id in list(self._workers):
                if camera_id not in wanted:
                    worker = self._workers.pop(camera_id)
                    logger.info("Stopping capture loop for %s (no longer claimed)", camera_id)
                    worker.stop()

            for camera_id, camera in wanted.items():
                if camera_id not in self._workers:
                    worker = _CameraWorker(self._pipeline, self._backend_client, self._batcher, camera)
                    worker.start()
                    self._workers[camera_id] = worker

            active_cameras.set(len(self._workers))

    def stop_all(self) -> None:
        with self._lock:
            for camera_id in list(self._workers):
                self._workers.pop(camera_id).stop()
            active_cameras.set(0)


def _run_claim_loop(
    lease_client: CameraLeaseClient,
    camera_source: TrinetraApiCameraSource,
    pool: CameraWorkerPool,
    capacity: int,
    poll_seconds: float,
    stop_event: threading.Event,
    sticky: bool = False,
) -> None:
    """Polls `POST /cameras/claim` every `poll_seconds`, syncing the pool to whatever comes back.

    `sticky` (settings.camera_claim_sticky): once this worker's first non-empty claim succeeds,
    every later poll asks for exactly `len(currently held)` instead of the full `capacity` —
    the backend's own trim step already keeps a worker's longest-held leases over anything newly
    claimed (`CameraLeaseRepository.ClaimAsync`'s `ORDER BY leased_at ASC` trim), so requesting no
    more than the current count is what stops this worker from ever picking up a newly-freed
    camera on its own. It still renews/keeps everything it already holds, and a camera it holds
    that goes stale (this worker crashes, or the camera itself is deleted/deactivated) is still
    released via the normal lease-TTL failover — sticky only turns off *auto-growing into spare
    capacity*, not the existing safety net. Resets to `capacity` on process restart (a fresh
    `held_capacity` here starts unset again) — a worker that's been restarted re-evaluates its
    full capacity once, by design; this is a per-process behavior, not a persisted assignment.
    """
    held_capacity: int | None = None

    while not stop_event.is_set():
        try:
            request_capacity = held_capacity if sticky and held_capacity is not None else capacity
            claimed = lease_client.claim(request_capacity)
            cameras = [
                camera
                for c in claimed
                if (camera := camera_source.resolve_leased_camera(c)) is not None
            ]
            pool.sync(cameras)
            logger.info(
                "Claimed %d camera(s), %d resolved to a usable stream",
                len(claimed), len(cameras),
            )

            claimed_camera.clear()
            for c in claimed:
                claimed_camera.labels(camera_id=c.camera_ref, camera_name=c.name).set(1)

            if sticky and held_capacity is None and claimed:
                held_capacity = len(claimed)
                logger.info(
                    "Camera claim is sticky — capped at %d camera(s) held from this first claim "
                    "onward, until this process restarts", held_capacity,
                )
        except httpx.HTTPError as exc:
            logger.warning("Camera claim failed (will retry next poll): %s", exc)

        stop_event.wait(poll_seconds)


def main() -> None:
    worker_info.labels(
        worker_index=str(settings.worker_index),
        worker_count=str(settings.worker_count),
        device=settings.device,
    ).set(1)

    pipeline = Pipeline(
        vehicle_detector=build_vehicle_detector(settings),
        plate_detector=build_plate_detector(settings),
        ocr_processor=build_ocr_processor(settings),
        evidence_dir=settings.evidence_dir,
    )
    backend_client = BackendClient(settings.trinetra_api_base_url, settings.trinetra_api_key)

    batcher: _AnyBatcher | None = None
    if settings.trinetra_api_base_url and settings.trinetra_api_key:
        batcher = DetectionBatcher(
            base_url=settings.trinetra_api_base_url,
            api_key=settings.trinetra_api_key,
            flush_max_detections=settings.detection_flush_max,
            flush_interval_seconds=settings.detection_flush_interval_seconds,
            spool_dir=settings.detection_spool_dir,
            spool_max_files=settings.detection_spool_max_files,
        )
        batcher.start()
    else:
        # No backend configured (standalone/offline run) — route everything through
        # BackendClient.submit(), which already no-ops and logs when there's nothing to submit
        # to, same as before this change.
        batcher = _NullBatcher(backend_client)

    pool = CameraWorkerPool(pipeline, backend_client, batcher)

    heartbeat = HeartbeatSender(
        url=settings.backend_heartbeat_url,
        worker_index=settings.worker_index,
        worker_count=settings.worker_count,
        interval_seconds=settings.backend_heartbeat_interval_seconds,
        api_key=settings.trinetra_api_key,
    )
    heartbeat.start()

    dynamic = settings.camera_source == "trinetra" and bool(settings.trinetra_api_key)
    lease_client: CameraLeaseClient | None = None
    stop_event = threading.Event()

    if dynamic:
        camera_source = build_camera_source(settings)
        assert isinstance(camera_source, TrinetraApiCameraSource)  # guaranteed by `dynamic`

        worker_id = f"ai-worker-{settings.worker_index}-of-{settings.worker_count}"
        hostname = platform.node() or "unknown"
        lease_client = CameraLeaseClient(
            base_url=settings.trinetra_api_base_url,
            api_key=settings.trinetra_api_key,
            worker_id=worker_id,
            hostname=hostname,
        )

        logger.info(
            "Dynamic camera claiming enabled (worker_id=%s, capacity=%d, poll=%.0fs, sticky=%s)",
            worker_id, settings.worker_capacity, settings.camera_claim_poll_seconds,
            settings.camera_claim_sticky,
        )

        threading.Thread(
            target=_run_claim_loop,
            args=(
                lease_client, camera_source, pool,
                settings.worker_capacity, settings.camera_claim_poll_seconds, stop_event,
                settings.camera_claim_sticky,
            ),
            daemon=True,
            name="camera-claim",
        ).start()
    else:
        all_cameras = build_camera_source(settings).list_cameras()
        my_cameras = assigned_cameras(all_cameras, settings.worker_index, settings.worker_count)
        pool.sync(my_cameras)

        logger.info(
            "Worker %d/%d owns %d of %d camera(s) (source: %s, static partition)",
            settings.worker_index, settings.worker_count, len(my_cameras), len(all_cameras),
            settings.camera_source,
        )
        if not my_cameras:
            logger.warning(
                "No cameras assigned (none returned by the %s camera source, or none hashed to "
                "this worker index). Only the /health and /metrics server will run.",
                settings.camera_source,
            )

    def _on_shutdown() -> None:
        # Registered on the metrics app's ASGI lifespan rather than run after uvicorn.run()
        # returns: uvicorn's own signal handling restores the default SIGTERM/SIGINT action and
        # re-raises the signal once its own shutdown completes (uvicorn/server.py
        # capture_signals), which kills the process immediately — code placed after
        # uvicorn.run() in this function would never run. The lifespan "shutdown" event fires
        # from inside that same shutdown sequence, before the signal is re-raised, so this is
        # the one hook that reliably runs on a graceful SIGTERM/SIGINT.
        stop_event.set()
        pool.stop_all()
        # After stop_all() returns, every capture thread has joined — no further batcher.submit()
        # calls can occur, so this is a safe final drain-and-flush.
        batcher.stop()
        heartbeat.stop()
        if lease_client is not None:
            lease_client.release()
            lease_client.close()

    metrics_app.router.on_shutdown.append(_on_shutdown)
    uvicorn.run(metrics_app, host=settings.metrics_host, port=settings.metrics_port, log_level="info")


if __name__ == "__main__":
    main()
