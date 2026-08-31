"""Vehicle detection via a local YOLOv8 model (ultralytics), run in-process.

Real inference: this loads actual pretrained weights and runs the actual forward pass. Nothing
here reads a database or a filename to decide what's "detected" — that would violate the
platform's core anti-cheat rule (CLAUDE.md / hackathon brief §4-5).
"""

from __future__ import annotations

import logging

import numpy as np

from backends.base import BoundingBox, VehicleDetection, VehicleDetector

logger = logging.getLogger(__name__)

# COCO class ids YOLOv8's stock weights were trained on, restricted to vehicle classes. Using
# the pretrained COCO classes (per brief §5: "prefer pretrained models... do not train from
# scratch unless required") rather than a custom vehicle taxonomy.
_COCO_VEHICLE_CLASSES: dict[int, str] = {
    2: "car",
    3: "motorcycle",
    5: "bus",
    7: "truck",
}


class YoloVehicleDetector(VehicleDetector):
    def __init__(self, model_path: str, device: str, confidence_threshold: float) -> None:
        from ultralytics import YOLO  # imported lazily: heavy, and not needed by remote/tests

        self._model = YOLO(model_path)
        self._device = device
        self._confidence_threshold = confidence_threshold
        logger.info("YoloVehicleDetector loaded %s on %s", model_path, device)

    def detect(self, frame: np.ndarray) -> list[VehicleDetection]:
        results = self._model.predict(
            source=frame,
            device=self._device,
            conf=self._confidence_threshold,
            classes=list(_COCO_VEHICLE_CLASSES.keys()),
            verbose=False,
        )

        detections: list[VehicleDetection] = []
        for result in results:
            boxes = result.boxes
            if boxes is None:
                continue

            for box, cls, conf in zip(boxes.xyxy, boxes.cls, boxes.conf, strict=True):
                x1, y1, x2, y2 = (int(v) for v in box.tolist())
                vehicle_type = _COCO_VEHICLE_CLASSES.get(int(cls.item()), "vehicle")
                detections.append(
                    VehicleDetection(
                        bbox=BoundingBox(x1, y1, x2, y2),
                        vehicle_type=vehicle_type,
                        confidence=float(conf.item()),
                    )
                )

        return detections
