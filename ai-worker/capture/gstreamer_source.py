"""RTSP capture via a native GStreamer pipeline (`appsink`).

Matches the portal's documented pipeline shape (sentinel.gujarat.gov.in/resource):

    rtspsrc location=<url> protocols=tcp latency=200 ! rtph264depay ! h264parse ! avdec_h264
    ! videoconvert ! appsink

— their example ends in `fakesink`, just to verify connectivity; swapped for `appsink` here so
frames actually reach Python. `decodebin` replaces the manual `rtph264depay ! h264parse !
avdec_h264` chain so the pipeline auto-negotiates whichever codec the server actually presents
(the portal's guide explicitly calls out "mixed H.264/H.265 codecs" — a fixed H.264 chain would
break on an H.265 camera). Requires GStreamer + gst-plugins-base/good and PyGObject (`gi`) —
`pip install`-only environments don't have these; they're a system package (Homebrew:
`gstreamer gst-plugins-base gst-plugins-good gst-plugins-bad gst-python pygobject3`).

GStreamer buffers carry their own PTS (nanoseconds since the pipeline clock started) natively,
so — unlike the OpenCV and ffmpeg-subprocess sources, which have to reconstruct or parse for a
timing signal — this is the most direct way to satisfy the portal's "drive all timing from PTS"
requirement.
"""

from __future__ import annotations

import logging
import time
from collections.abc import Iterator
from datetime import datetime, timedelta, timezone

import numpy as np

from capture.base import Frame, FrameSource

logger = logging.getLogger(__name__)

_MAX_PLAUSIBLE_PTS_JUMP_SECONDS = 30.0


def gstreamer_available() -> bool:
    try:
        import gi

        gi.require_version("Gst", "1.0")
        from gi.repository import Gst  # noqa: F401

        return True
    except (ImportError, ValueError):
        return False


class GStreamerFrameSource(FrameSource):
    def __init__(
        self,
        rtsp_url: str,
        processing_fps: float,
        latency_ms: int = 200,
        initial_reconnect_seconds: float = 2.0,
        max_reconnect_seconds: float = 60.0,
    ) -> None:
        if not gstreamer_available():
            raise RuntimeError(
                "GStreamer/PyGObject not available. Install the system packages "
                "(Homebrew: gstreamer gst-plugins-base gst-plugins-good gst-plugins-bad "
                "gst-python pygobject3; Debian/Ubuntu: python3-gi, gstreamer1.0-*) — this is "
                "not a pip package."
            )

        self._rtsp_url = rtsp_url
        self._sample_interval = 1.0 / processing_fps if processing_fps > 0 else 0.0
        self._latency_ms = latency_ms
        self._initial_reconnect_seconds = initial_reconnect_seconds
        self._max_reconnect_seconds = max_reconnect_seconds

        self._pipeline = None
        self._appsink = None
        self._pts_anchor_seconds: float | None = None
        self._pts_anchor_wallclock: datetime | None = None
        self._stopped = False

    def _pipeline_string(self) -> str:
        """Overridden by `DeepStreamFrameSource` to swap in NVIDIA's hardware-decode elements
        while reusing everything else here (appsink pulling, PTS anchoring, reconnect)."""
        return (
            f"rtspsrc location={self._rtsp_url} protocols=tcp latency={self._latency_ms} "
            "! decodebin ! videoconvert ! video/x-raw,format=BGR "
            "! appsink name=sink emit-signals=false sync=false max-buffers=1 drop=true"
        )

    def _start(self) -> None:
        import gi

        gi.require_version("Gst", "1.0")
        from gi.repository import Gst

        if not Gst.is_initialized():
            Gst.init(None)

        pipeline_str = self._pipeline_string()
        logger.info("Starting GStreamer pipeline for %s (TCP transport)", self._rtsp_url)
        self._pipeline = Gst.parse_launch(pipeline_str)
        self._appsink = self._pipeline.get_by_name("sink")

        ret = self._pipeline.set_state(Gst.State.PLAYING)
        if ret == Gst.StateChangeReturn.FAILURE:
            self._pipeline.set_state(Gst.State.NULL)
            raise ConnectionError(f"GStreamer pipeline failed to start for {self._rtsp_url}")

        self._pts_anchor_seconds = None
        self._pts_anchor_wallclock = None

    def frames(self) -> Iterator[Frame]:
        import gi

        gi.require_version("Gst", "1.0")
        from gi.repository import GLib, Gst

        backoff = self._initial_reconnect_seconds
        next_sample_at = 0.0

        while not self._stopped:
            if self._pipeline is None:
                try:
                    self._start()
                    backoff = self._initial_reconnect_seconds
                except (ConnectionError, GLib.Error) as exc:
                    logger.warning(
                        "GStreamer pipeline start failed for %s, retrying in %.1fs: %s",
                        self._rtsp_url, backoff, exc,
                    )
                    time.sleep(backoff)
                    backoff = min(backoff * 2, self._max_reconnect_seconds)
                    continue

            assert self._appsink is not None
            sample = self._appsink.emit("try-pull-sample", 1_000_000_000)  # 1s timeout, ns

            if sample is None:
                bus = self._pipeline.get_bus()
                msg = bus.pop_filtered(Gst.MessageType.ERROR | Gst.MessageType.EOS)
                if msg is not None:
                    logger.warning(
                        "GStreamer pipeline for %s ended (%s); reconnecting",
                        self._rtsp_url, msg.type.get_name(),
                    )
                    self._terminate()
                    time.sleep(backoff)
                    backoff = min(backoff * 2, self._max_reconnect_seconds)
                continue

            backoff = self._initial_reconnect_seconds
            now = time.monotonic()
            if now < next_sample_at:
                continue
            next_sample_at = now + self._sample_interval

            image, pts_ns = self._decode_sample(sample, Gst)
            yield Frame(captured_at=self._timestamp_for(pts_ns), image=image)

    def _decode_sample(self, sample, Gst) -> tuple[np.ndarray, int | None]:  # noqa: ANN001
        caps = sample.get_caps()
        structure = caps.get_structure(0)
        width = structure.get_value("width")
        height = structure.get_value("height")

        buffer = sample.get_buffer()
        ok, map_info = buffer.map(Gst.MapFlags.READ)
        try:
            image = np.frombuffer(map_info.data, dtype=np.uint8).reshape((height, width, 3)).copy()
        finally:
            buffer.unmap(map_info)

        pts_ns = buffer.pts if buffer.pts != Gst.CLOCK_TIME_NONE else None
        return image, pts_ns

    def _timestamp_for(self, pts_ns: int | None) -> datetime:
        now = datetime.now(timezone.utc)
        if pts_ns is None:
            return now

        pts_seconds = pts_ns / 1e9

        if (
            self._pts_anchor_seconds is None
            or abs(pts_seconds - self._pts_anchor_seconds) > _MAX_PLAUSIBLE_PTS_JUMP_SECONDS
        ):
            if self._pts_anchor_seconds is not None:
                logger.info(
                    "GStreamer source %s: PTS discontinuity (%.3fs -> %.3fs), re-anchoring — "
                    "likely a looping sample feed restarting.",
                    self._rtsp_url, self._pts_anchor_seconds, pts_seconds,
                )
            self._pts_anchor_seconds = pts_seconds
            self._pts_anchor_wallclock = now

        assert self._pts_anchor_wallclock is not None
        return self._pts_anchor_wallclock + timedelta(seconds=pts_seconds - self._pts_anchor_seconds)

    def _terminate(self) -> None:
        if self._pipeline is not None:
            from gi.repository import Gst

            self._pipeline.set_state(Gst.State.NULL)
            self._pipeline = None
            self._appsink = None

    def close(self) -> None:
        self._stopped = True
        self._terminate()
