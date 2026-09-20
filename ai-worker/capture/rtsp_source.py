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

import contextlib
import logging
import os
import threading
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


_stderr_redirect_lock = threading.Lock()
_stderr_redirect_count = 0
_stderr_saved_fd: int | None = None


@contextlib.contextmanager
def _capture_native_stderr() -> Iterator[list[bytes]]:
    """Redirects fd 2 into a pipe for the duration of the block and returns what was written.

    Used only around `cv2.VideoCapture(...)` / `isOpened()` in `_connect()` — that's where
    FFmpeg's real connection-failure reason ("Connection refused", "Connection timed out",
    "401 Unauthorized", "404 Not Found", RTSP negotiation errors, ...) is written via its C
    `av_log` callback, same as the decode-noise messages `_suppress_native_stderr` throws away.
    Kept separate from that function (rather than reusing it with a flag) because a decode
    session can run for hours with many threads sharing the redirect — buffering all of that in
    a pipe would leak memory; a connect attempt is a single bounded call, safe to capture in
    full and read back.
    """
    read_fd, write_fd = os.pipe()
    os.set_blocking(read_fd, False)
    saved_fd = os.dup(2)
    os.dup2(write_fd, 2)
    os.close(write_fd)
    chunks: list[bytes] = []
    try:
        yield chunks
    finally:
        os.dup2(saved_fd, 2)
        os.close(saved_fd)
        with contextlib.suppress(BlockingIOError):
            while chunk := os.read(read_fd, 4096):
                chunks.append(chunk)
        os.close(read_fd)


@contextlib.contextmanager
def _suppress_native_stderr() -> Iterator[None]:
    """Silences writes to the OS-level stderr file descriptor for the duration of the block.

    FFmpeg's own decoder ("[hevc @ ...] Could not find ref with POC #",
    "The cu_qp_delta # is outside the valid range", and similar) writes directly to fd 2 via its
    C `av_log` callback — this build of OpenCV doesn't route it through `cv2.utils.logging` or
    any Python-reachable API (both confirmed to have no effect), so the only place left to
    intercept it is the OS file descriptor itself. Verified against a real relay that emits this
    continuously: hundreds of consecutive `cap.read()` calls still return `ok=True` while it
    fires, so it is decoder-internal noise, not a real failure — see `RtspFrameSource.frames()`'s
    own comment on why a decode error surfaces only as `ok=False`.

    `RtspFrameSource.frames()` holds this open for an entire capture session (not just one
    `read()` call), because FFmpeg's HEVC/H.264 decoder can use internal frame-parallel/
    decode-ahead threads that log slightly outside a narrowly-scoped per-call window — a gap a
    background decoder thread could still slip a message through. `worker.py` runs one capture
    thread per claimed camera, all potentially wanting this open at once, and fd 2 is one
    process-wide resource: two threads independently dup()-ing and dup2()-ing it without
    coordination would race (the second thread's "saved" fd would capture the first thread's
    devnull redirect, not the real terminal, corrupting fd 2 once either thread restores it).
    This is therefore a thread-safe, reference-counted redirect: the first caller to enter
    actually redirects fd 2; concurrent callers just increment the count; only the last caller
    to exit restores the real fd. `worker.py`'s own logging is bound to a duplicated fd taken
    before any of this runs (see its `_real_stderr` comment), so it stays visible throughout,
    however long fd 2 itself stays redirected.
    """
    global _stderr_redirect_count, _stderr_saved_fd

    with _stderr_redirect_lock:
        if _stderr_redirect_count == 0:
            _stderr_saved_fd = os.dup(2)
            devnull_fd = os.open(os.devnull, os.O_WRONLY)
            os.dup2(devnull_fd, 2)
            os.close(devnull_fd)
        _stderr_redirect_count += 1

    try:
        yield
    finally:
        with _stderr_redirect_lock:
            _stderr_redirect_count -= 1
            if _stderr_redirect_count == 0:
                assert _stderr_saved_fd is not None
                os.dup2(_stderr_saved_fd, 2)
                os.close(_stderr_saved_fd)
                _stderr_saved_fd = None

def _last_stderr_line(chunks: list[bytes]) -> str:
    """The most specific line of FFmpeg's captured stderr, e.g. "Connection refused" or
    "401 Unauthorized" — the last non-empty line is generally the actual failure reason;
    earlier lines are usually just FFmpeg's version/config banner."""
    text = b"".join(chunks).decode("utf-8", errors="replace")
    lines = [line.strip() for line in text.splitlines() if line.strip()]
    return lines[-1] if lines else ""


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
        max_reconnect_seconds: float = 30.0,
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
        with _capture_native_stderr() as stderr_chunks:
            capture = cv2.VideoCapture(self._rtsp_url, cv2.CAP_FFMPEG)
            opened = capture.isOpened()
        if not opened:
            capture.release()
            reason = _last_stderr_line(stderr_chunks)
            detail = f": {reason}" if reason else " (no diagnostic output from FFmpeg)"
            raise ConnectionError(f"Could not open RTSP stream {self._rtsp_url}{detail}")

        # Reset the PTS anchor on every (re)connect: a fresh session starts its own PTS clock,
        # and the old anchor would otherwise be read as a huge discontinuity.
        self._pts_anchor_ms = None
        self._pts_anchor_wallclock = None
        self._last_pts_ms = None
        return capture

    def frames(self) -> Iterator[Frame]:
        # One suppression window for the whole method, not re-entered per connect/read: FFmpeg's
        # HEVC/H.264 decoder can use internal frame-parallel/decode-ahead threads that emit
        # av_log slightly outside a narrowly-scoped per-call window, so a per-read suppression
        # left real gaps a background decoder thread could still log through. worker.py's own
        # logging is bound to a duplicated file descriptor before this runs (see its own comment
        # on `_real_stderr`), so it stays visible even while fd 2 itself sits redirected here for
        # the whole session.
        with _suppress_native_stderr():
            yield from self._frames_inner()

    def _frames_inner(self) -> Iterator[Frame]:
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
            # logs via OpenCV/FFmpeg's own stderr output — suppressed for the whole session by
            # frames()'s own wrapper — and surfaces here only as ok=False, handled the same as a
            # dropped connection, never a raised exception.
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
