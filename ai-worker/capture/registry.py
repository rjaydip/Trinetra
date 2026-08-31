"""Resolves the configured capture backend, mirroring `backends/registry.py`'s pattern.

Four ways to read the same RTSP stream, all producing the same `FrameSource` contract so
`worker.py` never branches on which one is active — swap `CAPTURE_BACKEND` and nothing else
changes:

  - `opencv` (default): `RtspFrameSource`, OpenCV's FFmpeg binding. Simplest, pip-installable,
    no system dependency beyond what `opencv-python-headless` already brings.
  - `ffmpeg`: `FfmpegFrameSource`, an `ffmpeg` subprocess. Needs the `ffmpeg`/`ffprobe`
    binaries on PATH — not a pip package.
  - `gstreamer`: `GStreamerFrameSource`, a native GStreamer pipeline. Needs GStreamer +
    PyGObject as system packages — not pip-installable at all (see gstreamer_source.py).
  - `deepstream`: `DeepStreamFrameSource`. NVIDIA GPU + DeepStream SDK on Linux only; will
    raise clearly on any other platform (see deepstream_source.py's module docstring — this
    one specifically has not been run anywhere, unlike the other three).

All four were verified against a real live RTSP stream (a local MediaMTX instance re-publishing
a sample clip, matching the Sentinel portal's own `rtsp://<host>:8554/stream/<id>` shape) while
building this — `deepstream` is the one exception, since it cannot run on non-NVIDIA hardware.
"""

from __future__ import annotations

from capture.base import FrameSource
from capture.rtsp_url import apply_credentials


def build_frame_source(settings, rtsp_url: str, processing_fps: float) -> FrameSource:  # noqa: ANN001
    backend = settings.capture_backend

    # Fold in operator-supplied credentials for a URL that has none (the registry never
    # includes them). A URL that already carries userinfo is left untouched.
    rtsp_url = apply_credentials(rtsp_url, settings.rtsp_username, settings.rtsp_password)

    if backend == "ffmpeg":
        from capture.ffmpeg_source import FfmpegFrameSource

        return FfmpegFrameSource(rtsp_url, processing_fps)

    if backend == "gstreamer":
        from capture.gstreamer_source import GStreamerFrameSource

        return GStreamerFrameSource(rtsp_url, processing_fps)

    if backend == "deepstream":
        from capture.deepstream_source import DeepStreamFrameSource

        return DeepStreamFrameSource(rtsp_url, processing_fps)

    from capture.rtsp_source import RtspFrameSource

    return RtspFrameSource(rtsp_url, processing_fps)
