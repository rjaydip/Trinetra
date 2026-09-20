"""Dynamic camera claiming against the Trinetra backend (v1.27/v1.28).

Replaces `scaling.py`'s static hash-partition: instead of every worker being told the same
full camera list and keeping only what hashes to its own `WORKER_INDEX`, each worker instance
now calls `POST /api/v1/worker-health/cameras/claim` on an interval, telling the backend how
many cameras it can handle (`WORKER_CAPACITY`). The backend hands back up to that many cameras
this worker doesn't already hold — VMS-discovered ones first, then standalone registry
cameras — and keeps renewing what it already holds. If a worker stops calling (crash, network
partition), its leases go stale after the backend's TTL (90s) and the freed cameras are picked
up by the next worker that polls. `POST /cameras/release` on clean shutdown skips that wait.

This is the Phase-2 upgrade `scaling.py`'s own docstring already called out: the lease store
and worker registry pattern on the .NET side (`Federation.Storage/LeaseStore.cs`) applied to AI
workers and cameras.
"""

from __future__ import annotations

import logging
from dataclasses import dataclass

import httpx

logger = logging.getLogger(__name__)


@dataclass(frozen=True)
class ClaimedCamera:
    """One entry of `POST /cameras/claim`'s response — see `ClaimedCameraResponse` on the
    .NET side. `camera_ref` is either a bare registry camera UUID (`source == "REGISTRY"`) or
    `vms:{targetId}:{nativeCameraId}` (`source == "VMS"`); submit it back verbatim as a
    detection's `cameraId` (`DetectionRepository.ResolveCameraAsync` understands both shapes,
    including the "vms:" tag)."""

    camera_ref: str
    source: str
    name: str
    stream_reference: str | None
    stream_preference: str
    native_hls_url: str | None
    native_webrtc_url: str | None
    credential_reference: str | None


class CameraLeaseClient:
    def __init__(
        self,
        base_url: str,
        api_key: str,
        worker_id: str,
        hostname: str,
        timeout: float = 15.0,
    ) -> None:
        self._base_url = base_url.rstrip("/")
        self._worker_id = worker_id
        self._hostname = hostname
        self._client = httpx.Client(headers={"X-Api-Key": api_key}, timeout=timeout)

    def claim(self, capacity: int) -> list[ClaimedCamera]:
        """Claims/renews up to `capacity` cameras. Call this well inside the backend's 90s
        lease TTL (`CAMERA_CLAIM_POLL_SECONDS`, default 30s) — a slow poll is what the TTL is
        for, but a poll interval close to the TTL risks losing cameras to a faster worker on
        one missed tick."""
        resp = self._client.post(
            f"{self._base_url}/api/v1/worker-health/cameras/claim",
            json={
                "workerId": self._worker_id,
                "hostname": self._hostname,
                "capacity": capacity,
            },
        )
        resp.raise_for_status()
        return [
            ClaimedCamera(
                camera_ref=c["cameraRef"],
                source=c["source"],
                name=c["name"],
                stream_reference=c.get("streamReference"),
                stream_preference=c.get("streamPreference", "RTSP"),
                native_hls_url=c.get("nativeHlsUrl"),
                native_webrtc_url=c.get("nativeWebrtcUrl"),
                credential_reference=c.get("credentialReference"),
            )
            for c in resp.json()
        ]

    def release(self) -> None:
        """Best-effort — called once on clean shutdown so this worker's cameras are picked up
        immediately rather than waiting out the lease TTL. Never raises: a release that fails
        during shutdown just means the TTL handles it instead."""
        try:
            resp = self._client.post(
                f"{self._base_url}/api/v1/worker-health/cameras/release",
                json={"workerId": self._worker_id, "hostname": self._hostname},
            )
            resp.raise_for_status()
        except httpx.HTTPError as exc:
            logger.warning("Camera release failed (leases will expire via TTL instead): %s", exc)

    def close(self) -> None:
        self._client.close()
