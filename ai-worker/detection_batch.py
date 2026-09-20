"""Batches routine (non-plate) detections into bulk submissions, off the capture threads.

Confirmed design (BA + system-design + Python + camera-management review, 2026-09): submitting
one HTTP request per detection doesn't scale request volume toward the platform's 80k-camera
target, but a wanted-plate match cannot wait behind a batch window — a real ANPR deployment for
policing needs that alert close to immediate. So only `VEHICLE_DETECTED` events (no plate) are
eligible for batching here; any `ANPR_DETECTED` event (this worker read *some* plate, and can't
know locally whether it's a watchlist match — the platform doesn't sync its watchlist to
workers) always goes out immediately through `BackendClient.submit()`, unbatched, exactly as
before this change. See `worker.py`'s wiring of the two paths.

Shape: capture threads `put()` eligible events onto a thread-safe `queue.Queue`; one dedicated
flush thread drains it, flushing on `flush_max_detections` OR `flush_interval_seconds` — whichever
comes first — via `POST /api/v1/detections/bulk`. A flush that fails outright (backend down,
network partition) is never dropped: the whole batch is spooled to its own file under
`spool_dir` (one JSON array per file, written atomically via temp-file-then-rename so a crash
mid-write can't leave a half-written file behind) and replayed — oldest file first — before any
further live flush, so replay and live traffic never race for network access. Spool growth is
bounded by `spool_max_files`; past that, the oldest file is dropped (logged loudly, never
silently) rather than filling disk during an extended outage.

Deliberately thread-based, not asyncio, matching every other background thread in this worker
(`_run_claim_loop`, `HeartbeatSender`) — see the design review for why introducing a second
concurrency model here wasn't justified.
"""

from __future__ import annotations

import json
import logging
import queue
import tempfile
import threading
import time
import uuid
from pathlib import Path

import httpx

from pipeline import DetectionEvent

logger = logging.getLogger(__name__)

# detection_event's own outcomes worth keeping in a retry spool. "inserted"/"duplicate" are done;
# "conflict" reused an id and must never be resubmitted verbatim (a fresh id would be a different
# detection, so there is nothing safe to retry); "error" (unknown camera, bad payload) is worth
# one more attempt in case the cause was transient (e.g. a claim not yet visible to this API
# replica), but see _MAX_ITEM_RETRIES below for the ceiling on that.
_RETRYABLE_STATUSES = {"error"}


class DetectionBatcher:
    def __init__(
        self,
        base_url: str,
        api_key: str,
        flush_max_detections: int,
        flush_interval_seconds: float,
        spool_dir: str | Path,
        spool_max_files: int,
        timeout: float = 30.0,
    ) -> None:
        self._client = httpx.Client(
            headers={"X-Api-Key": api_key}, timeout=timeout, base_url=base_url.rstrip("/")
        )
        self._flush_max_detections = flush_max_detections
        self._flush_interval_seconds = flush_interval_seconds
        self._spool_dir = Path(spool_dir)
        self._spool_dir.mkdir(parents=True, exist_ok=True)
        self._spool_max_files = spool_max_files

        self._queue: queue.Queue[DetectionEvent] = queue.Queue()
        self._stop = threading.Event()
        self._thread = threading.Thread(target=self._run, daemon=True, name="detection-flush")

    def start(self) -> None:
        self._thread.start()

    def submit(self, event: DetectionEvent) -> None:
        """Enqueues a routine detection for batched submission. Never blocks the calling
        (capture) thread on network I/O — `queue.Queue.put()` is O(1) and unbounded here on
        purpose: backpressure belongs on the spool (bounded, see `spool_max_files`), not on the
        capture loop, which must keep reading frames regardless of backend health."""
        self._queue.put(event)

    def stop(self, drain_timeout: float = 10.0) -> None:
        """Signals the flush thread to do one final flush of whatever's queued, then joins it.
        Called from `worker.py`'s shutdown hook, after the capture threads (and therefore all
        `submit()` callers) have already stopped — no new items can arrive after this point."""
        self._stop.set()
        self._thread.join(timeout=drain_timeout)
        if self._thread.is_alive():
            logger.warning(
                "Detection flush thread did not stop within %.0fs; remaining queued items are lost "
                "unless they were already spooled", drain_timeout,
            )
        self._client.close()

    def _run(self) -> None:
        # Replay anything left over from a prior crash/outage before taking on new live traffic —
        # a stale spool file always predates whatever's arriving now.
        self._replay_spool()

        last_flush = time.monotonic()
        pending: list[DetectionEvent] = []

        # Caps how long a single queue.get() blocks, regardless of flush_interval_seconds — a
        # long interval (e.g. 30s) must not make stop() wait that long for the thread to notice
        # self._stop; this bounds the worst case to one poll tick.
        poll_cap_seconds = 1.0

        while True:
            remaining = self._flush_interval_seconds - (time.monotonic() - last_flush)
            wait = max(0.0, min(remaining, poll_cap_seconds))
            try:
                item = self._queue.get(timeout=wait)
                pending.append(item)
            except queue.Empty:
                pass

            should_flush = (
                len(pending) >= self._flush_max_detections
                or (pending and time.monotonic() - last_flush >= self._flush_interval_seconds)
            )

            if should_flush:
                self._flush(pending)
                pending = []
                last_flush = time.monotonic()
            elif not pending:
                # Nothing queued and the interval already elapsed: rebase the window so the next
                # loop iteration blocks a full interval again instead of spinning on
                # queue.get(timeout=0).
                last_flush = time.monotonic()

            if self._stop.is_set() and self._queue.empty():
                if pending:
                    self._flush(pending)
                break

    def _flush(self, batch: list[DetectionEvent]) -> None:
        if not self._post_batch(batch):
            self._spool(batch)
            return

        # A successful bulk POST doesn't guarantee every item is retryable-clean (some 400s can
        # be permanent — an unknown camera id that will never resolve), but that's the backend's
        # per-item report, already logged inside _post_batch; nothing further to spool here.

    def _post_batch(self, batch: list[DetectionEvent]) -> bool:
        """Returns True if the bulk request was accepted at all (HTTP-level success) — a
        request-level failure (backend unreachable, 5xx, timeout) is what triggers spooling.
        Per-item outcomes inside a 200 response are logged but never spooled: an accepted request
        already durably recorded the platform's decision on each item (inserted, duplicate,
        conflict, or a permanent validation error), and retrying those verbatim would just repeat
        the same outcome."""
        try:
            resp = self._client.post(
                "/api/v1/detections/bulk", json={"items": [e.to_json() for e in batch]}
            )
            resp.raise_for_status()
        except httpx.HTTPError as exc:
            logger.warning(
                "Bulk detection flush failed (%d item(s)); spooling for retry: %s", len(batch), exc
            )
            return False

        body = resp.json()
        failed = body.get("failed", 0)
        if failed:
            for item in body.get("results", []):
                if item.get("status") == "error":
                    logger.warning(
                        "Detection %s rejected by bulk ingest: %s", item.get("id"), item.get("error")
                    )
                elif item.get("status") == "conflict":
                    logger.error(
                        "Detection id %s collided with different content already stored: %s",
                        item.get("id"), item.get("error"),
                    )
        logger.info(
            "Flushed %d detection(s): %d inserted, %d duplicate, %d failed",
            len(batch), body.get("inserted", 0), body.get("duplicate", 0), failed,
        )
        return True

    def _spool(self, batch: list[DetectionEvent]) -> None:
        self._enforce_spool_cap()

        payload = [e.to_json() for e in batch]
        filename = f"{time.time():.6f}-{uuid.uuid4().hex}.json"
        target = self._spool_dir / filename

        # Atomic write: a crash mid-write leaves only a stray .tmp file, never a half-written
        # target that _replay_spool could pick up and fail to parse.
        fd, tmp_path = tempfile.mkstemp(dir=self._spool_dir, suffix=".tmp")
        try:
            with open(fd, "w") as f:
                json.dump(payload, f)
            Path(tmp_path).rename(target)
        except Exception:
            Path(tmp_path).unlink(missing_ok=True)
            raise

        logger.info("Spooled %d detection(s) to %s for later retry", len(batch), target.name)

    def _enforce_spool_cap(self) -> None:
        files = sorted(self._spool_dir.glob("*.json"))
        if len(files) < self._spool_max_files:
            return

        # Drop the oldest first — a detection is an operational/security signal with declining
        # value the longer it sits unsent; bounded retention is the honest degrade under a
        # backend outage that outlasts spool_max_files, not unbounded disk growth.
        excess = len(files) - self._spool_max_files + 1
        for stale in files[:excess]:
            logger.error(
                "Spool directory at capacity (%d files); dropping oldest unsent batch %s",
                self._spool_max_files, stale.name,
            )
            stale.unlink(missing_ok=True)

    def _replay_spool(self) -> None:
        files = sorted(self._spool_dir.glob("*.json"))
        if not files:
            return

        logger.info("Replaying %d spooled detection batch(es) from a prior run", len(files))
        for path in files:
            if self._stop.is_set():
                # Shutdown raced with startup replay (unlikely, but possible under a very fast
                # start/stop in a test) — leave remaining files for the next run rather than
                # racing a flush against process exit.
                break

            try:
                items = json.loads(path.read_text())
            except (json.JSONDecodeError, OSError) as exc:
                logger.error(
                    "Spooled file %s is unreadable/corrupt, discarding: %s", path.name, exc
                )
                path.unlink(missing_ok=True)
                continue

            try:
                resp = self._client.post("/api/v1/detections/bulk", json={"items": items})
                resp.raise_for_status()
            except httpx.HTTPError as exc:
                logger.warning(
                    "Replay of spooled batch %s failed again (%s); will retry on next run",
                    path.name, exc,
                )
                # Stop replaying — the backend is still unreachable, so trying the rest of the
                # spool now would just fail the same way; leave them for the next attempt.
                break

            body = resp.json()
            logger.info(
                "Replayed spooled batch %s: %d inserted, %d duplicate, %d failed",
                path.name, body.get("inserted", 0), body.get("duplicate", 0), body.get("failed", 0),
            )
            path.unlink(missing_ok=True)
