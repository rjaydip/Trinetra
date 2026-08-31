"""Deterministic camera partitioning across worker instances (horizontal scaling, v1).

Every worker instance is given the same full camera list (via `CameraSource`) and the same
`WORKER_COUNT`, but a distinct `WORKER_INDEX`. Each keeps only the cameras that hash to its
index, so no coordination between workers is needed. To add capacity: bump `WORKER_COUNT` and
restart the set with each worker's `WORKER_INDEX` set 0..WORKER_COUNT-1.

This is deliberately static rather than a DB-backed lease: the lease store and a `worker_node`
registry already exist on the .NET side (`Federation.Storage/LeaseStore.cs`,
`federation.worker_node`) for exactly this kind of claim-and-heartbeat problem, and mirroring
that pattern for AI workers is the natural Phase-2 upgrade once the .NET integration exists —
building a second, divergent coordination mechanism now would just be thrown away then.
"""

from __future__ import annotations

import hashlib

from camera_source import CameraConfig


def assigned_cameras(
    cameras: list[CameraConfig], worker_index: int, worker_count: int
) -> list[CameraConfig]:
    if worker_count <= 0:
        raise ValueError("worker_count must be >= 1")
    if not (0 <= worker_index < worker_count):
        raise ValueError(f"worker_index must be in [0, {worker_count}), got {worker_index}")

    return [c for c in cameras if _partition(c.camera_id, worker_count) == worker_index]


def _partition(camera_id: str, worker_count: int) -> int:
    digest = hashlib.sha256(camera_id.encode("utf-8")).digest()
    return int.from_bytes(digest[:8], "big") % worker_count
