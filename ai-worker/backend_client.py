"""Posts a normalized detection to the Trinetra backend's ingest endpoint.

Written against the JSON contract the hackathon brief documents (§7) for
`POST /api/v1/detections`, which doesn't exist yet on the `.NET` side (Phase 2) — so this
client is exercised by `pipeline.py` but its actual HTTP call cannot be integration-tested
until that endpoint lands. It no-ops (logs and returns) when `TRINETRA_API_BASE_URL` is unset,
so the rest of the worker runs standalone today.
"""

from __future__ import annotations

import logging

import httpx

from pipeline import DetectionEvent

logger = logging.getLogger(__name__)


class BackendClient:
    def __init__(self, base_url: str | None, api_key: str | None, timeout: float = 10.0) -> None:
        self._base_url = base_url.rstrip("/") if base_url else None
        headers = {"X-Api-Key": api_key} if api_key else {}
        self._client = httpx.Client(headers=headers, timeout=timeout) if base_url else None

    def submit(self, event: DetectionEvent) -> None:
        if self._client is None or self._base_url is None:
            logger.debug(
                "TRINETRA_API_BASE_URL not set; detection %s not submitted (Phase 2 dependency). "
                "Payload: %s",
                event.id, event.to_json(),
            )
            return

        try:
            response = self._client.post(f"{self._base_url}/api/v1/detections", json=event.to_json())
            response.raise_for_status()
        except httpx.HTTPError as exc:
            logger.error("Failed to submit detection %s: %s", event.id, exc)
