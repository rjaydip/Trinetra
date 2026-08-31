"""Resolves the configured backend for each pipeline stage.

This is the one place that reads `config.py`'s backend-selection fields and turns them into
concrete objects. `pipeline.py` calls `build_vehicle_detector()` etc. once at startup and never
looks at config again — adding a new implementation means adding a branch here (or, for a
genuinely new stage, extending this module), not touching the pipeline.
"""

from __future__ import annotations

from backends.base import AnprProcessor, PlateDetector, VehicleDetector
from config import Settings


def build_vehicle_detector(settings: Settings) -> VehicleDetector:
    if settings.vehicle_backend == "remote":
        from backends.vehicle.remote_api import RemoteVehicleDetector

        if not settings.vehicle_api_url:
            raise ValueError("VEHICLE_BACKEND=remote requires VEHICLE_API_URL to be set.")

        return RemoteVehicleDetector(
            api_url=settings.vehicle_api_url,
            api_key=settings.vehicle_api_key,
            confidence_threshold=settings.vehicle_confidence_threshold,
            timeout=settings.remote_api_timeout_seconds,
        )

    from backends.vehicle.yolo_local import YoloVehicleDetector

    return YoloVehicleDetector(
        model_path=settings.vehicle_model_path,
        device=settings.device,
        confidence_threshold=settings.vehicle_confidence_threshold,
    )


def build_plate_detector(settings: Settings) -> PlateDetector:
    if settings.plate_backend == "remote":
        from backends.plate.remote_api import RemotePlateDetector

        if not settings.plate_api_url:
            raise ValueError("PLATE_BACKEND=remote requires PLATE_API_URL to be set.")

        return RemotePlateDetector(
            api_url=settings.plate_api_url,
            api_key=settings.plate_api_key,
            confidence_threshold=settings.plate_confidence_threshold,
            timeout=settings.remote_api_timeout_seconds,
        )

    if settings.plate_backend == "local":
        from backends.plate.yolo_local import YoloPlateDetector

        if not settings.plate_model_path:
            raise ValueError("PLATE_BACKEND=local requires PLATE_MODEL_PATH to be set.")

        return YoloPlateDetector(
            model_path=settings.plate_model_path,
            device=settings.device,
            confidence_threshold=settings.plate_confidence_threshold,
        )

    from backends.plate.heuristic_cv import HeuristicPlateDetector

    return HeuristicPlateDetector(confidence_threshold=settings.plate_confidence_threshold)


def build_ocr_processor(settings: Settings) -> AnprProcessor:
    if settings.ocr_backend == "remote":
        from backends.ocr.remote_api import RemoteOcrProcessor

        if not settings.ocr_api_url:
            raise ValueError("OCR_BACKEND=remote requires OCR_API_URL to be set.")

        return RemoteOcrProcessor(
            api_url=settings.ocr_api_url,
            api_key=settings.ocr_api_key,
            confidence_threshold=settings.ocr_confidence_threshold,
            timeout=settings.remote_api_timeout_seconds,
        )

    from backends.ocr.local_ocr import LocalOcrProcessor

    return LocalOcrProcessor(
        confidence_threshold=settings.ocr_confidence_threshold,
        gpu=settings.device in ("cuda", "mps"),
    )
