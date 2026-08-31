"""Manual verification: run the real pipeline against local image(s), no RTSP/GPU required.

    python smoke_test.py sample.jpg [more images...]

Prints each resulting DetectionEvent as JSON. This is the "eyeball the output" check called
for in the plan's verification section (no automated test suite this pass) — real boxes,
labels, and OCR text coming back is the proof that actual model inference ran, not a stub.
"""

from __future__ import annotations

import json
import logging
import sys
from datetime import datetime, timezone

import cv2

from backends.registry import build_ocr_processor, build_plate_detector, build_vehicle_detector
from capture.base import Frame
from config import settings
from pipeline import Pipeline

logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(name)s: %(message)s")


def main() -> None:
    if len(sys.argv) < 2:
        print("Usage: python smoke_test.py <image1> [image2 ...]")
        raise SystemExit(1)

    pipeline = Pipeline(
        vehicle_detector=build_vehicle_detector(settings),
        plate_detector=build_plate_detector(settings),
        ocr_processor=build_ocr_processor(settings),
        evidence_dir=settings.evidence_dir,
    )

    for path in sys.argv[1:]:
        image = cv2.imread(path)
        if image is None:
            print(f"Could not read {path}")
            continue

        frame = Frame(captured_at=datetime.now(timezone.utc), image=image)
        events = pipeline.process_frame(
            "SMOKE-TEST", frame, enabled_models=["vehicle", "plate", "ocr"]
        )

        print(f"\n--- {path}: {len(events)} detection(s) ---")
        for event in events:
            print(json.dumps(event.to_json(), indent=2))


if __name__ == "__main__":
    main()
