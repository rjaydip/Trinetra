"""Vehicle detection via a hosted AI/vision API instead of a local model.

Lets an operator point at any HTTP detection service (a cloud vision API, a colleague's model
server) without touching `pipeline.py` — set `VEHICLE_BACKEND=remote`, `VEHICLE_API_URL`,
`VEHICLE_API_KEY` in config. The request/response shape below is a reasonable default contract;
adapt the two `_encode`/`_parse` methods if a specific provider's API differs, without touching
anything else in the pipeline.
"""

from __future__ import annotations

import logging

import httpx
import numpy as np

from backends.base import BoundingBox, VehicleDetection, VehicleDetector

logger = logging.getLogger(__name__)


class RemoteVehicleDetector(VehicleDetector):
    def __init__(
        self, api_url: str, api_key: str | None, confidence_threshold: float, timeout: float
    ) -> None:
        self._api_url = api_url
        self._confidence_threshold = confidence_threshold
        headers = {"Authorization": f"Bearer {api_key}"} if api_key else {}
        self._client = httpx.Client(headers=headers, timeout=timeout)

    def detect(self, frame: np.ndarray) -> list[VehicleDetection]:
        import cv2

        ok, encoded = cv2.imencode(".jpg", frame)
        if not ok:
            logger.warning("RemoteVehicleDetector: failed to encode frame for upload")
            return []

        response = self._client.post(
            self._api_url,
            files={"image": ("frame.jpg", encoded.tobytes(), "image/jpeg")},
        )
        response.raise_for_status()
        payload = response.json()

        detections: list[VehicleDetection] = []
        for item in payload.get("detections", []):
            confidence = float(item.get("confidence", 0.0))
            if confidence < self._confidence_threshold:
                continue

            box = item["bbox"]
            detections.append(
                VehicleDetection(
                    bbox=BoundingBox(int(box["x1"]), int(box["y1"]), int(box["x2"]), int(box["y2"])),
                    vehicle_type=str(item.get("label", "vehicle")),
                    confidence=confidence,
                )
            )

        return detections
