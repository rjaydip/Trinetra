"""License-plate detection via a hosted AI API. Same contract shape as the vehicle remote
backend — see `backends/vehicle/remote_api.py` for the rationale."""

from __future__ import annotations

import logging

import httpx
import numpy as np

from backends.base import BoundingBox, PlateDetection, PlateDetector

logger = logging.getLogger(__name__)


class RemotePlateDetector(PlateDetector):
    def __init__(
        self, api_url: str, api_key: str | None, confidence_threshold: float, timeout: float
    ) -> None:
        self._api_url = api_url
        self._confidence_threshold = confidence_threshold
        headers = {"Authorization": f"Bearer {api_key}"} if api_key else {}
        self._client = httpx.Client(headers=headers, timeout=timeout)

    def detect(self, vehicle_crop: np.ndarray) -> list[PlateDetection]:
        if vehicle_crop.size == 0:
            return []

        import cv2

        ok, encoded = cv2.imencode(".jpg", vehicle_crop)
        if not ok:
            logger.warning("RemotePlateDetector: failed to encode crop for upload")
            return []

        response = self._client.post(
            self._api_url,
            files={"image": ("crop.jpg", encoded.tobytes(), "image/jpeg")},
        )
        response.raise_for_status()
        payload = response.json()

        detections: list[PlateDetection] = []
        for item in payload.get("detections", []):
            confidence = float(item.get("confidence", 0.0))
            if confidence < self._confidence_threshold:
                continue

            box = item["bbox"]
            detections.append(
                PlateDetection(
                    bbox=BoundingBox(int(box["x1"]), int(box["y1"]), int(box["x2"]), int(box["y2"])),
                    confidence=confidence,
                )
            )

        return detections
