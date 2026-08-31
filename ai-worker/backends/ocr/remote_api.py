"""Plate OCR via a hosted AI/vision API. Same contract shape as the other remote backends."""

from __future__ import annotations

import logging

import httpx
import numpy as np

from backends.base import AnprResult, AnprProcessor

logger = logging.getLogger(__name__)


class RemoteOcrProcessor(AnprProcessor):
    def __init__(
        self, api_url: str, api_key: str | None, confidence_threshold: float, timeout: float
    ) -> None:
        self._api_url = api_url
        self._confidence_threshold = confidence_threshold
        headers = {"Authorization": f"Bearer {api_key}"} if api_key else {}
        self._client = httpx.Client(headers=headers, timeout=timeout)

    def read(self, plate_crop: np.ndarray) -> AnprResult | None:
        if plate_crop.size == 0:
            return None

        import cv2

        ok, encoded = cv2.imencode(".jpg", plate_crop)
        if not ok:
            logger.warning("RemoteOcrProcessor: failed to encode crop for upload")
            return None

        response = self._client.post(
            self._api_url,
            files={"image": ("plate.jpg", encoded.tobytes(), "image/jpeg")},
        )
        response.raise_for_status()
        payload = response.json()

        text = payload.get("text")
        confidence = float(payload.get("confidence", 0.0))

        if not text or confidence < self._confidence_threshold:
            return None

        return AnprResult(text=text, confidence=confidence)
