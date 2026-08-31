"""Frame sources that don't need a live camera — used to validate the pipeline right now.

`StaticImageFrameSource` loops a single image (or a directory of images) as if it were a
one-frame-per-call stream. `SampleVideoFrameSource` reads a local video file with the same
sampling behaviour `RtspFrameSource` uses. Neither depends on RTSP or a GPU.
"""

from __future__ import annotations

from collections.abc import Iterator
from datetime import datetime, timezone
from pathlib import Path

import cv2

from capture.base import Frame, FrameSource


class StaticImageFrameSource(FrameSource):
    """Replays a fixed set of images, one `Frame` per call to `frames()`, then stops."""

    def __init__(self, image_paths: list[str | Path], repeat: int = 1) -> None:
        self._image_paths = [Path(p) for p in image_paths]
        self._repeat = repeat

    def frames(self) -> Iterator[Frame]:
        for _ in range(self._repeat):
            for path in self._image_paths:
                image = cv2.imread(str(path))
                if image is None:
                    raise FileNotFoundError(f"Could not read image: {path}")
                yield Frame(captured_at=datetime.now(timezone.utc), image=image)

    def close(self) -> None:
        pass


class SampleVideoFrameSource(FrameSource):
    """Reads a local video file at the configured processing FPS, like `RtspFrameSource` would
    for a live stream — useful for validating sampling/pipeline behaviour without a camera."""

    def __init__(self, video_path: str | Path, processing_fps: float) -> None:
        self._video_path = Path(video_path)
        self._processing_fps = processing_fps
        self._capture: cv2.VideoCapture | None = None

    def frames(self) -> Iterator[Frame]:
        self._capture = cv2.VideoCapture(str(self._video_path))
        if not self._capture.isOpened():
            raise FileNotFoundError(f"Could not open video: {self._video_path}")

        source_fps = self._capture.get(cv2.CAP_PROP_FPS) or 25.0
        sample_every_n = max(1, round(source_fps / self._processing_fps))

        frame_index = 0
        while True:
            ok, frame = self._capture.read()
            if not ok:
                break

            if frame_index % sample_every_n == 0:
                yield Frame(captured_at=datetime.now(timezone.utc), image=frame)

            frame_index += 1

    def close(self) -> None:
        if self._capture is not None:
            self._capture.release()
            self._capture = None
