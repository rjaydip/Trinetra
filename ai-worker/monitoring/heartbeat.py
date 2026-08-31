"""Optional heartbeat push to the Trinetra backend.

A no-op until `BACKEND_HEARTBEAT_URL` is set (Phase 2, once the .NET side has a receiver for
it) — `/metrics` above is the monitoring surface that works today. Kept here now so that
turning this on later is a config change, not new code.
"""

from __future__ import annotations

import logging
import platform
from datetime import datetime, timezone
from threading import Event, Thread

import httpx

logger = logging.getLogger(__name__)


class HeartbeatSender:
    def __init__(
        self,
        url: str | None,
        worker_index: int,
        worker_count: int,
        interval_seconds: float,
        api_key: str | None = None,
    ) -> None:
        self._url = url
        self._worker_id = f"ai-worker-{worker_index}-of-{worker_count}"
        self._interval = interval_seconds
        self._headers = {"X-Api-Key": api_key} if api_key else {}
        self._stop = Event()
        self._thread: Thread | None = None

    def start(self) -> None:
        if not self._url:
            logger.info("BACKEND_HEARTBEAT_URL not set; heartbeat disabled.")
            return

        self._thread = Thread(target=self._run, daemon=True, name="heartbeat")
        self._thread.start()

    def stop(self) -> None:
        self._stop.set()
        if self._thread is not None:
            self._thread.join(timeout=self._interval + 1)

    def _run(self) -> None:
        assert self._url is not None
        with httpx.Client(headers=self._headers, timeout=5.0) as client:
            while not self._stop.is_set():
                try:
                    client.post(
                        self._url,
                        json={
                            "workerId": self._worker_id,
                            "hostname": platform.node(),
                            "reportedAt": datetime.now(timezone.utc).isoformat(),
                        },
                    )
                except httpx.HTTPError as exc:
                    logger.warning("Heartbeat POST failed: %s", exc)

                self._stop.wait(self._interval)
