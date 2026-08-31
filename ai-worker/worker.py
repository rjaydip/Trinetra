"""Entry point for a running worker instance: capture + inference + monitoring.

One process per instance. Camera ownership is decided once at startup (`scaling.py`); each
owned camera runs its own capture/inference loop in a thread, while the main thread serves
`/health` and `/metrics` so monitoring stays up even if a camera's loop is stuck.
"""

from __future__ import annotations

import logging
import threading

import uvicorn

from backend_client import BackendClient
from backends.registry import build_ocr_processor, build_plate_detector, build_vehicle_detector
from camera_source import CameraConfig, build_camera_source
from capture.registry import build_frame_source
from config import settings
from monitoring.app import app as metrics_app
from monitoring.heartbeat import HeartbeatSender
from monitoring.metrics import active_cameras, frames_captured_total, worker_info
from pipeline import Pipeline
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


logging.basicConfig(
    level=getattr(logging, settings.log_level.upper(), logging.INFO),
    format="%(asctime)s %(levelname)s %(name)s: %(message)s",
)
# On the handler, not the logger: a logger's own filters are skipped for records that reach
# it by propagation from a child logger, but a handler's filters run for every record it emits.
for _handler in logging.getLogger().handlers:
    _handler.addFilter(_RedactRtspCredentials())
logger = logging.getLogger("worker")


def run_camera_loop(pipeline: Pipeline, backend_client: BackendClient, camera: CameraConfig) -> None:
    fps = camera.processing_fps or settings.processing_fps
    logger.info("Starting capture loop for %s (%s) at %.1f fps", camera.camera_id, camera.name, fps)

    with build_frame_source(settings, camera.rtsp_url, processing_fps=fps) as source:
        for frame in source.frames():
            frames_captured_total.labels(camera_id=camera.camera_id).inc()

            try:
                events = pipeline.process_frame(camera.camera_id, frame, camera.enabled_models)
            except Exception:
                logger.exception("Unhandled pipeline error for camera %s", camera.camera_id)
                continue

            for event in events:
                backend_client.submit(event)


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

    all_cameras = build_camera_source(settings).list_cameras()
    my_cameras = assigned_cameras(all_cameras, settings.worker_index, settings.worker_count)
    active_cameras.set(len(my_cameras))

    logger.info(
        "Worker %d/%d owns %d of %d camera(s) (source: %s)",
        settings.worker_index, settings.worker_count, len(my_cameras), len(all_cameras),
        settings.camera_source,
    )
    if not my_cameras:
        logger.warning(
            "No cameras assigned (none returned by the %s camera source, or none hashed to "
            "this worker index). Only the /health and /metrics server will run.",
            settings.camera_source,
        )

    heartbeat = HeartbeatSender(
        url=settings.backend_heartbeat_url,
        worker_index=settings.worker_index,
        worker_count=settings.worker_count,
        interval_seconds=settings.backend_heartbeat_interval_seconds,
        api_key=settings.trinetra_api_key,
    )
    heartbeat.start()

    for camera in my_cameras:
        threading.Thread(
            target=run_camera_loop,
            args=(pipeline, backend_client, camera),
            daemon=True,
            name=f"camera-{camera.camera_id}",
        ).start()

    uvicorn.run(metrics_app, host=settings.metrics_host, port=settings.metrics_port, log_level="info")


if __name__ == "__main__":
    main()
