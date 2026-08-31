"""License-plate detection via a local YOLO model trained specifically for plates.

Higher accuracy than `heuristic_cv.HeuristicPlateDetector` but needs `PLATE_MODEL_PATH` set to
real plate-detector weights (a single-class YOLO checkpoint). Same ultralytics runtime as the
vehicle stage, just a different checkpoint and no class filtering — a plate model has one class.
"""

from __future__ import annotations

import logging

import numpy as np

from backends.base import BoundingBox, PlateDetection, PlateDetector

logger = logging.getLogger(__name__)


class YoloPlateDetector(PlateDetector):
    def __init__(self, model_path: str, device: str, confidence_threshold: float) -> None:
        from ultralytics import YOLO

        self._model = YOLO(model_path)
        self._device = device
        self._confidence_threshold = confidence_threshold
        logger.info("YoloPlateDetector loaded %s on %s", model_path, device)

    def detect(self, vehicle_crop: np.ndarray) -> list[PlateDetection]:
        if vehicle_crop.size == 0:
            return []

        results = self._model.predict(
            source=vehicle_crop,
            device=self._device,
            conf=self._confidence_threshold,
            verbose=False,
        )

        detections: list[PlateDetection] = []
        for result in results:
            boxes = result.boxes
            if boxes is None:
                continue

            for box, conf in zip(boxes.xyxy, boxes.conf, strict=True):
                x1, y1, x2, y2 = (int(v) for v in box.tolist())
                detections.append(
                    PlateDetection(bbox=BoundingBox(x1, y1, x2, y2), confidence=float(conf.item()))
                )

        return detections
