"""Live RTSP capture with frame-rate sampling and reconnect/retry.

Written fully per the brief's reliability requirements (§9 frame sampling, §32 reconnect), but
**not live-tested in this pass** — no RTSP source/GPU is available in this environment yet
(confirmed with the user). Validate against `StaticFrameSource`/`SampleVideoFrameSource` now;
point this at a real `rtsp://` URL once a feed exists — nothing else in the pipeline changes.

Built specifically against the Sentinel hackathon portal's integrator guidance
(sentinel.gujarat.gov.in/resource), since that's the actual feed source this will eventually
point at:

  - endpoints are shaped `rtsp://<host>:8554/stream/<id>` (also `.../whep` for WebRTC and
    `.../index.m3u8` for HLS — not implemented here; OpenCV's FFmpeg backend can also open an
    `.m3u8` URL through this same class if HLS is preferred over RTSP for a given deployment)
  - "force RTSP over TCP, as UDP fails across network boundaries" -> `rtsp_transport;tcp`
  - "drive all timing from PTS, never from arrival time, to avoid incorrect velocity
    calculations" -> `_next_frame_timestamp` below
  - "mixed H.264/H.265 codecs and varying resolutions" -> left to FFmpeg's own decoder
    selection; nothing here assumes a fixed codec or resolution
  - "implement reconnection logic with exponential backoff" -> `_backoff_seconds`
  - "support scene discontinuities from looping feeds" -> a backward or implausible PTS jump
    re-anchors the PTS-to-wallclock mapping rather than producing a corrupted timestamp
"""

from __future__ import annotations

import logging
import os
import time
from collections.abc import Iterator
from datetime import datetime, timedelta, timezone

import cv2

from capture.base import Frame, FrameSource

logger = logging.getLogger(__name__)

# Read by OpenCV's FFmpeg backend when it opens the capture. Must be set before
# cv2.VideoCapture() is constructed. Harmless for non-RTSP URLs (HLS/file) — FFmpeg ignores an
# option a given demuxer doesn't recognize.
os.environ.setdefault("OPENCV_FFMPEG_CAPTURE_OPTIONS", "rtsp_transport;tcp")

# A PTS jump larger than this (in either direction) is treated as a discontinuity — a looping
# sample feed restarting, or a stream re-establishing after a gap — rather than genuine elapsed
# time, and re-anchors the PTS-to-wallclock mapping instead of producing a bogus timestamp.
_MAX_PLAUSIBLE_PTS_JUMP = timedelta(seconds=30)


class RtspFrameSource(FrameSource):
    def __init__(
        self,
        rtsp_url: str,
        processing_fps: float,
        initial_reconnect_seconds: float = 2.0,
        max_reconnect_seconds: float = 60.0,
        max_consecutive_failures: int = 20,
    ) -> None:
        self._rtsp_url = rtsp_url
        self._sample_interval = 1.0 / processing_fps if processing_fps > 0 else 0.0
        self._initial_reconnect_seconds = initial_reconnect_seconds
        self._max_reconnect_seconds = max_reconnect_seconds
        self._max_consecutive_failures = max_consecutive_failures
        self._capture: cv2.VideoCapture | None = None
        self._stopped = False

        # PTS-to-wallclock anchor: (stream position at anchor time, wallclock at anchor time).
        self._pts_anchor_ms: float | None = None
        self._pts_anchor_wallclock: datetime | None = None
        self._last_pts_ms: float | None = None

    def _connect(self) -> cv2.VideoCapture:
        logger.info("Connecting to RTSP source %s (TCP transport)", self._rtsp_url)
        capture = cv2.VideoCapture(self._rtsp_url, cv2.CAP_FFMPEG)
        if not capture.isOpened():
            capture.release()
            raise ConnectionError(f"Could not open RTSP stream: {self._rtsp_url}")

        # Reset the PTS anchor on every (re)connect: a fresh session starts its own PTS clock,
        # and the old anchor would otherwise be read as a huge discontinuity.
        self._pts_anchor_ms = None
        self._pts_anchor_wallclock = None
        self._last_pts_ms = None
        return capture

    def frames(self) -> Iterator[Frame]:
        consecutive_failures = 0
        backoff = self._initial_reconnect_seconds
        next_sample_at = 0.0

        while not self._stopped:
            if self._capture is None:
                try:
                    self._capture = self._connect()
                    consecutive_failures = 0
                    backoff = self._initial_reconnect_seconds
                except ConnectionError as exc:
                    consecutive_failures += 1
                    logger.warning(
                        "RTSP connect failed (%d/%d), retrying in %.1fs: %s",
                        consecutive_failures, self._max_consecutive_failures, backoff, exc,
                    )
                    if consecutive_failures >= self._max_consecutive_failures:
                        raise
                    time.sleep(backoff)
                    backoff = min(backoff * 2, self._max_reconnect_seconds)
                    continue

            # A decode error on one frame (a corrupt packet, a mid-GOP splice at a loop point)
            # logs via OpenCV/FFmpeg's own stderr output and surfaces here only as ok=False —
            # handled the same as a dropped connection, never a raised exception.
            ok, frame = self._capture.read()
            now = time.monotonic()

            if not ok:
                logger.warning("RTSP read failed for %s; reconnecting", self._rtsp_url)
                self._capture.release()
                self._capture = None
                time.sleep(backoff)
                backoff = min(backoff * 2, self._max_reconnect_seconds)
                continue

            consecutive_failures = 0
            backoff = self._initial_reconnect_seconds

            # Sample down to processing_fps: cameras run at 15-30fps, inference does not need
            # to (brief §9). Frames between samples are still read (to keep the decoder's
            # internal buffer from growing unbounded) and immediately discarded.
            if now < next_sample_at:
                continue

            next_sample_at = now + self._sample_interval
            yield Frame(captured_at=self._next_frame_timestamp(), image=frame)

    def _next_frame_timestamp(self) -> datetime:
        """PTS-derived timestamp, per the portal's "never from arrival time" requirement.

        OpenCV doesn't expose an absolute wall-clock PTS for a live RTSP session — only
        `CAP_PROP_POS_MSEC`, the stream's own relative position. So this anchors that relative
        position to the wallclock time of the first frame after each (re)connect, then reports
        every later frame as an offset from that anchor — which is what "driven by PTS" means
        in practice here: frame spacing tracks the stream's own clock, not variable arrival
        jitter. A backward or implausibly large jump (a loop restart) re-anchors rather than
        producing a corrupted timestamp.
        """
        assert self._capture is not None
        pos_ms = self._capture.get(cv2.CAP_PROP_POS_MSEC)
        now = datetime.now(timezone.utc)

        if pos_ms <= 0:
            # Some FFmpeg/RTSP combinations never populate POS_MSEC for a live session. Fall
            # back to arrival time rather than fabricate a position — logged once, not per
            # frame, since this is expected for some sources and not itself an error.
            if self._pts_anchor_wallclock is None:
                logger.info(
                    "RTSP source %s does not report stream position; falling back to arrival "
                    "time for frame timestamps.", self._rtsp_url,
                )
                self._pts_anchor_wallclock = now  # sentinel: "we've logged this already"
            return now

        max_jump_ms = _MAX_PLAUSIBLE_PTS_JUMP.total_seconds() * 1000
        if self._pts_anchor_ms is None or self._last_pts_ms is None or (
            abs(pos_ms - self._last_pts_ms) > max_jump_ms
        ):
            if self._pts_anchor_ms is not None:
                logger.info(
                    "RTSP source %s: PTS discontinuity (%.0fms -> %.0fms), re-anchoring — "
                    "likely a looping sample feed restarting.",
                    self._rtsp_url, self._last_pts_ms, pos_ms,
                )
            self._pts_anchor_ms = pos_ms
            self._pts_anchor_wallclock = now

        self._last_pts_ms = pos_ms
        elapsed_ms = pos_ms - self._pts_anchor_ms
        return self._pts_anchor_wallclock + timedelta(milliseconds=elapsed_ms)

    def close(self) -> None:
        self._stopped = True
        if self._capture is not None:
            self._capture.release()
            self._capture = None
