"""License-plate localization via classical OpenCV, no trained weights required.

This is the default plate backend (`PLATE_BACKEND=heuristic`) specifically because it needs
nothing downloaded — it works the moment OpenCV is installed, which matters for getting the
pipeline running today without hunting for plate-detector weights. It is genuine computer
vision (edge detection + contour geometry), not a lookup: a real plate-shaped region must
actually be present in the pixels for this to return a box. Swap to `PLATE_BACKEND=local` with
a trained detector, or `remote`, when higher accuracy is needed — `pipeline.py` doesn't change.
"""

from __future__ import annotations

import cv2
import numpy as np

from backends.base import BoundingBox, PlateDetection, PlateDetector

# A license plate is a wide, flat rectangle. These bounds are deliberately loose — they only
# need to reject obviously-wrong shapes (windows, wheels, whole-vehicle contours), not to be a
# precise plate model.
_MIN_ASPECT_RATIO = 2.0
_MAX_ASPECT_RATIO = 6.0
_MIN_AREA_FRACTION = 0.005
_MAX_AREA_FRACTION = 0.35


class HeuristicPlateDetector(PlateDetector):
    def __init__(self, confidence_threshold: float) -> None:
        self._confidence_threshold = confidence_threshold

    def detect(self, vehicle_crop: np.ndarray) -> list[PlateDetection]:
        if vehicle_crop.size == 0:
            return []

        h, w = vehicle_crop.shape[:2]
        crop_area = float(h * w)

        gray = cv2.cvtColor(vehicle_crop, cv2.COLOR_BGR2GRAY)
        gray = cv2.bilateralFilter(gray, 11, 17, 17)
        edges = cv2.Canny(gray, 30, 200)
        edges = cv2.dilate(edges, np.ones((3, 3), np.uint8), iterations=1)

        contours, _ = cv2.findContours(edges, cv2.RETR_LIST, cv2.CHAIN_APPROX_SIMPLE)

        candidates: list[PlateDetection] = []
        for contour in contours:
            x, y, cw, ch = cv2.boundingRect(contour)
            if cw == 0 or ch == 0:
                continue

            aspect_ratio = cw / ch
            area_fraction = (cw * ch) / crop_area

            if not (_MIN_ASPECT_RATIO <= aspect_ratio <= _MAX_ASPECT_RATIO):
                continue
            if not (_MIN_AREA_FRACTION <= area_fraction <= _MAX_AREA_FRACTION):
                continue

            # No learned confidence to report — score how well the candidate matches the
            # expected plate aspect ratio (~3.1:1 for an Indian plate) as a proxy. Clamped into
            # the same [0, 1] range every backend reports, so the pipeline treats it uniformly.
            target_ratio = 3.1
            ratio_error = abs(aspect_ratio - target_ratio) / target_ratio
            score = max(0.0, 1.0 - ratio_error)

            if score < self._confidence_threshold:
                continue

            candidates.append(
                PlateDetection(bbox=BoundingBox(x, y, x + cw, y + ch), confidence=score)
            )

        candidates.sort(key=lambda c: c.confidence, reverse=True)
        return candidates[:3]
