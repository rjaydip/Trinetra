"""Reads plate text from a cropped plate image using EasyOCR, in-process.

Real OCR inference on the actual pixels of the crop — this is the stage the brief is most
insistent about not faking (§4-5): the platform must never substitute a database lookup for
this step. EasyOCR is used as shipped, pretrained; no custom training.
"""

from __future__ import annotations

import logging

import numpy as np

from backends.base import AnprResult, AnprProcessor

logger = logging.getLogger(__name__)


class LocalOcrProcessor(AnprProcessor):
    def __init__(self, confidence_threshold: float, gpu: bool = False) -> None:
        import easyocr  # imported lazily: heavy, and not needed by remote/tests that stub it

        self._reader = easyocr.Reader(["en"], gpu=gpu, verbose=False)
        self._confidence_threshold = confidence_threshold
        logger.info("LocalOcrProcessor ready (gpu=%s)", gpu)

    def read(self, plate_crop: np.ndarray) -> AnprResult | None:
        if plate_crop.size == 0:
            return None

        # allowlist restricts recognition to plate-valid characters, which measurably improves
        # accuracy over unconstrained OCR (EasyOCR otherwise tries to read arbitrary text).
        results = self._reader.readtext(
            plate_crop,
            detail=1,
            allowlist="ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789",
        )

        if not results:
            return None

        # A plate often OCRs as multiple fragments (state code, series, number). Reconstruct
        # left-to-right reading order from each fragment's bounding box, rather than trusting
        # EasyOCR's result ordering.
        results.sort(key=lambda item: min(point[0] for point in item[0]))

        text = "".join(fragment for _, fragment, _ in results)
        confidence = min(conf for _, _, conf in results)

        if not text or confidence < self._confidence_threshold:
            return None

        return AnprResult(text=text, confidence=float(confidence))
