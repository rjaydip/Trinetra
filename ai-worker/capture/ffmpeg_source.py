"""RTSP capture via an `ffmpeg` subprocess piping raw frames to stdout.

The CLI-based alternative to `RtspFrameSource` (OpenCV's FFmpeg binding) — matches the
portal's own documented `ffplay -rtsp_transport tcp ...` / `ffprobe` usage
(sentinel.gujarat.gov.in/resource) for teams that want ffmpeg directly, more control over its
flags, or a decoder OpenCV wasn't built with. Requires the `ffmpeg`/`ffprobe` binaries on PATH.

Same PTS-driven-timestamp requirement as `rtsp_source.py`: rawvideo frames on stdout carry no
timing on their own, so ffmpeg is run with `-vf showinfo`, whose stderr output includes each
frame's `pts_time`. A background thread parses that stream continuously (stdout and stderr
must both be drained concurrently or one pipe's OS buffer fills and deadlocks the process) and
the main loop reads the latest parsed value when a frame comes off stdout.
"""

from __future__ import annotations

import json
import logging
import re
import subprocess
import threading
import time
from collections.abc import Iterator
from datetime import datetime, timedelta, timezone

import numpy as np

from capture.base import Frame, FrameSource

logger = logging.getLogger(__name__)

_PTS_TIME_RE = re.compile(rb"pts_time:(-?[0-9.]+)")
_MAX_PLAUSIBLE_PTS_JUMP_SECONDS = 30.0


def _rtsp_transport_flags(url: str) -> list[str]:
    """`-rtsp_transport` is a private option of the RTSP demuxer specifically — ffmpeg's CLI
    parser rejects it outright ("Option not found") for any other input (a local file used to
    validate this without a live camera, an HLS URL, ...), so it's only included for a genuine
    `rtsp://` URL rather than passed unconditionally."""
    return ["-rtsp_transport", "tcp"] if url.startswith("rtsp://") else []


def probe_resolution(rtsp_url: str, timeout: float = 10.0) -> tuple[int, int]:
    """One-shot `ffprobe` call to learn the frame size before opening the raw pipe — rawvideo
    has no header, so the reader must already know how many bytes make up one frame."""
    result = subprocess.run(
        [
            "ffprobe", "-v", "error", *_rtsp_transport_flags(rtsp_url),
            "-select_streams", "v:0", "-show_entries", "stream=width,height",
            "-of", "json", rtsp_url,
        ],
        capture_output=True, text=True, timeout=timeout, check=True,
    )
    stream = json.loads(result.stdout)["streams"][0]
    return int(stream["width"]), int(stream["height"])


class FfmpegFrameSource(FrameSource):
    def __init__(
        self,
        rtsp_url: str,
        processing_fps: float,
        initial_reconnect_seconds: float = 2.0,
        max_reconnect_seconds: float = 60.0,
    ) -> None:
        self._rtsp_url = rtsp_url
        self._sample_interval = 1.0 / processing_fps if processing_fps > 0 else 0.0
        self._initial_reconnect_seconds = initial_reconnect_seconds
        self._max_reconnect_seconds = max_reconnect_seconds

        self._process: subprocess.Popen | None = None
        self._stderr_thread: threading.Thread | None = None
        self._width = 0
        self._height = 0
        self._frame_bytes = 0
        self._stopped = False

        self._pts_lock = threading.Lock()
        self._latest_pts_seconds: float | None = None
        self._pts_anchor_seconds: float | None = None
        self._pts_anchor_wallclock: datetime | None = None

    def _start(self) -> None:
        self._width, self._height = probe_resolution(self._rtsp_url)
        self._frame_bytes = self._width * self._height * 3
        logger.info("Starting ffmpeg for %s at %dx%d", self._rtsp_url, self._width, self._height)

        self._process = subprocess.Popen(  # noqa: S603 - rtsp_url is operator-configured, not user input
            [
                "ffmpeg", "-loglevel", "info", "-nostats",
                *_rtsp_transport_flags(self._rtsp_url),
                "-i", self._rtsp_url,
                "-an", "-vf", "showinfo",
                "-f", "rawvideo", "-pix_fmt", "bgr24", "-",
            ],
            stdout=subprocess.PIPE, stderr=subprocess.PIPE,
        )

        with self._pts_lock:
            self._latest_pts_seconds = None
            self._pts_anchor_seconds = None
            self._pts_anchor_wallclock = None

        self._stderr_thread = threading.Thread(
            target=self._drain_stderr, daemon=True, name="ffmpeg-stderr"
        )
        self._stderr_thread.start()

    def _drain_stderr(self) -> None:
        """Must run concurrently with the stdout reads in `frames()` — ffmpeg blocks writing to
        either pipe once its OS buffer fills, so leaving stderr unread stalls stdout too."""
        assert self._process is not None and self._process.stderr is not None
        for line in self._process.stderr:
            match = _PTS_TIME_RE.search(line)
            if match:
                with self._pts_lock:
                    self._latest_pts_seconds = float(match.group(1))

    def _read_exact(self, size: int) -> bytes:
        """Accumulates across multiple reads rather than trusting one `.read(size)` call to
        return the full frame — a partial read with the process still alive is not the same as
        EOF, and treating it as EOF (as a single bare `.read(size)` call effectively did) was
        the cause of spurious reconnects observed while validating this against a local file."""
        assert self._process is not None and self._process.stdout is not None
        chunks: list[bytes] = []
        remaining = size

        while remaining > 0:
            chunk = self._process.stdout.read(remaining)
            if not chunk:  # a genuinely empty read is the real EOF signal
                break
            chunks.append(chunk)
            remaining -= len(chunk)

        return b"".join(chunks)

    def frames(self) -> Iterator[Frame]:
        next_sample_at = 0.0
        backoff = self._initial_reconnect_seconds

        while not self._stopped:
            if self._process is None:
                try:
                    self._start()
                    backoff = self._initial_reconnect_seconds
                except (subprocess.SubprocessError, OSError, KeyError, IndexError, ValueError) as exc:
                    logger.warning(
                        "ffmpeg start failed for %s, retrying in %.1fs: %s",
                        self._rtsp_url, backoff, exc,
                    )
                    time.sleep(backoff)
                    backoff = min(backoff * 2, self._max_reconnect_seconds)
                    continue

            raw = self._read_exact(self._frame_bytes)

            if len(raw) < self._frame_bytes:
                logger.warning("ffmpeg stream for %s ended; reconnecting", self._rtsp_url)
                self._terminate()
                time.sleep(backoff)
                backoff = min(backoff * 2, self._max_reconnect_seconds)
                continue

            backoff = self._initial_reconnect_seconds
            now = time.monotonic()
            if now < next_sample_at:
                continue
            next_sample_at = now + self._sample_interval

            image = np.frombuffer(raw, dtype=np.uint8).reshape((self._height, self._width, 3))
            yield Frame(captured_at=self._current_timestamp(), image=image)

    def _current_timestamp(self) -> datetime:
        now = datetime.now(timezone.utc)

        with self._pts_lock:
            pts_seconds = self._latest_pts_seconds

            if pts_seconds is None:
                return now

            if (
                self._pts_anchor_seconds is None
                or abs(pts_seconds - self._pts_anchor_seconds) > _MAX_PLAUSIBLE_PTS_JUMP_SECONDS
            ):
                if self._pts_anchor_seconds is not None:
                    logger.info(
                        "ffmpeg source %s: PTS discontinuity (%.3fs -> %.3fs), re-anchoring — "
                        "likely a looping sample feed restarting.",
                        self._rtsp_url, self._pts_anchor_seconds, pts_seconds,
                    )
                self._pts_anchor_seconds = pts_seconds
                self._pts_anchor_wallclock = now

            assert self._pts_anchor_wallclock is not None
            return self._pts_anchor_wallclock + timedelta(
                seconds=pts_seconds - self._pts_anchor_seconds
            )

    def _terminate(self) -> None:
        if self._process is not None:
            self._process.terminate()
            try:
                self._process.wait(timeout=5)
            except subprocess.TimeoutExpired:
                self._process.kill()
            self._process = None
        if self._stderr_thread is not None:
            self._stderr_thread.join(timeout=2)
            self._stderr_thread = None

    def close(self) -> None:
        self._stopped = True
        self._terminate()
