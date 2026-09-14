#!/usr/bin/env python3
"""Keeps MediaMTX's runtime path list in sync with the camera registry.

docs/STREAMING-GATEWAY-PLAN.md, G3. Runs as its own systemd service
(trinetra-mediamtx-provisioner.service), polling on an interval rather than reacting to
events -- there is no camera-added/removed event stream this process subscribes to, and a
periodic reconciliation is simpler to reason about than a partial live-update path that can
drift from the registry's actual state.

For each camera in scope:
  1. Resolve its RTSP credential. Tries GET /api/v1/cameras/{id}/credential/resolve first
     (G1); a VMS-managed camera can legitimately carry no credential of its own and rely
     entirely on its connector target's, so a 404 there falls back to
     GET /api/v1/vms/{vmsId}/credential/resolve when the camera has a vmsId -- confirmed
     during G1's implementation that there is no automatic fallback on the API side, so this
     script is the one place that tries both. STREAMING_GATEWAY holds both permissions
     (camera.credential.resolve, vms.read's credential.resolve) for exactly this reason. Every
     resolution is audited to credential_access_log on the API side -- nothing here logs the
     resolved credential itself.
  2. Build the RTSP source URL with the credential embedded (the same way ai-worker's own
     capture/rtsp_url.py does for AI inference -- see that file for the percent-encoding
     rationale; this script intentionally does not import ai-worker's package, to keep the
     provisioner a standalone deployable with no dependency on the AI pipeline).
  3. PUT that source into MediaMTX's own runtime config API (POST .../add on first sight,
     PATCH .../patch thereafter) as an on-demand path -- MediaMTX only opens the RTSP
     connection when a viewer actually requests the path (mediamtx.yml's pathDefaults).
  4. Remove any MediaMTX path whose camera no longer appears in this run's camera list
     (retired, deleted, or moved out of this gateway's scope).

Credentials live in MediaMTX's in-memory runtime config (bound to 127.0.0.1 only -- see
mediamtx.yml) between provisioning runs, not on disk: this script never writes the resolved
RTSP URL to a file, and MediaMTX itself is configured with no persistent config write-back
(--confFile is read-only from this script's point of view; MediaMTX's own /v3/config/paths/*
calls mutate its in-memory state, not mediamtx.yml on disk).
"""

from __future__ import annotations

import logging
import os
import sys
import time
from urllib.parse import quote

import requests

logger = logging.getLogger("provision_paths")

API_BASE_URL = os.environ["TRINETRA_API_BASE_URL"]
API_KEY = os.environ["TRINETRA_API_KEY"]
MEDIAMTX_API_URL = os.environ.get("MEDIAMTX_API_URL", "http://127.0.0.1:9997")
POLL_INTERVAL_SECONDS = int(os.environ.get("PROVISION_INTERVAL_SECONDS", "60"))
HTTP_TIMEOUT_SECONDS = int(os.environ.get("PROVISION_HTTP_TIMEOUT_SECONDS", "15"))

_session = requests.Session()
_session.headers["X-Api-Key"] = API_KEY


def _api_get(path: str, params: dict | None = None) -> dict:
    response = _session.get(f"{API_BASE_URL}{path}", params=params, timeout=HTTP_TIMEOUT_SECONDS)
    response.raise_for_status()
    return response.json()


def list_cameras() -> list[dict]:
    """Every camera this gateway's scope can see. GET /api/v1/cameras is cursor-paginated
    (items + nextCursor), not page/pageSize/totalPages -- a null nextCursor is the last page."""
    cameras: list[dict] = []
    cursor: str | None = None
    while True:
        params = {"limit": 200} | ({"cursor": cursor} if cursor else {})
        result = _api_get("/api/v1/cameras", params=params)
        cameras.extend(result["items"])
        cursor = result.get("nextCursor")
        if not cursor:
            return cameras


def _get_or_404(path: str) -> dict | None:
    try:
        return _api_get(path)
    except requests.HTTPError as exc:
        if exc.response is not None and exc.response.status_code == 404:
            return None
        raise


def resolve_credential(camera: dict) -> dict | None:
    """A camera's own credential_reference is independent of, and never automatically falls
    back to, its owning VMS target's -- G1 confirmed there is no such fallback anywhere in
    Federation.Core/Storage. A VMS-managed camera can legitimately have no credential of its
    own and rely entirely on its connector target's, so this tries the camera-level route
    first and only then, if the camera has a vmsId, tries the VMS-level route -- never assumes
    one or the other always works. None means neither resolved anything usable."""
    resolved = _get_or_404(f"/api/v1/cameras/{camera['id']}/credential/resolve")
    if resolved is not None:
        return resolved

    vms_id = camera.get("vmsId")
    if not vms_id:
        return None

    return _get_or_404(f"/api/v1/vms/{vms_id}/credential/resolve")


def build_rtsp_source(stream_reference: str, credential: dict) -> str:
    """Same percent-encoding requirement as ai-worker/capture/rtsp_url.py -- a username or
    password containing '@', ':' or '/' must round-trip through the URL correctly."""
    scheme_sep = stream_reference.find("://")
    if scheme_sep == -1:
        raise ValueError(f"streamReference is not a URL: {stream_reference!r}")
    scheme, rest = stream_reference[: scheme_sep + 3], stream_reference[scheme_sep + 3 :]
    host_and_path = rest.split("@", 1)[-1]  # drop any credential already embedded in the reference.
    user = quote(credential["username"], safe="")
    password = quote(credential["password"], safe="")
    return f"{scheme}{user}:{password}@{host_and_path}"


def mediamtx_path_name(camera_id: str) -> str:
    return f"cam-{camera_id}"


def upsert_mediamtx_path(path_name: str, source: str, *, exists: bool) -> None:
    # MediaMTX's v3 config API is verb-specific, not just path-specific: /add is created with
    # POST, but /patch requires the HTTP PATCH method and /delete requires HTTP DELETE -- sending
    # all three as POST (an earlier version of this script did) 404s on patch/delete with a bare
    # "404 page not found", which looks identical to "path doesn't exist" unless you notice it's
    # plain text, not MediaMTX's own {"status":"error",...} JSON shape.
    request = _session.patch if exists else _session.post
    verb = "patch" if exists else "add"
    response = request(
        f"{MEDIAMTX_API_URL}/v3/config/paths/{verb}/{path_name}",
        json={"source": source, "sourceOnDemand": True},
        timeout=HTTP_TIMEOUT_SECONDS,
    )
    response.raise_for_status()


def delete_mediamtx_path(path_name: str) -> None:
    response = _session.delete(
        f"{MEDIAMTX_API_URL}/v3/config/paths/delete/{path_name}", timeout=HTTP_TIMEOUT_SECONDS
    )
    if response.status_code not in (200, 404):
        response.raise_for_status()


def list_mediamtx_paths() -> set[str]:
    response = _session.get(f"{MEDIAMTX_API_URL}/v3/config/paths/list", timeout=HTTP_TIMEOUT_SECONDS)
    response.raise_for_status()
    return {item["name"] for item in response.json().get("items", [])}


def run_once() -> None:
    cameras = list_cameras()
    existing_paths = list_mediamtx_paths()
    seen_paths: set[str] = set()

    for camera in cameras:
        stream_reference = camera.get("streamReference")
        if not stream_reference:
            continue  # nothing to pull -- no configured stream for this camera.

        credential = resolve_credential(camera)
        if credential is None:
            logger.info("camera %s has no resolvable credential; skipping", camera["id"])
            continue

        try:
            source = build_rtsp_source(stream_reference, credential)
        except ValueError as exc:
            logger.warning("camera %s: %s", camera["id"], exc)
            continue

        path_name = mediamtx_path_name(camera["id"])
        seen_paths.add(path_name)
        upsert_mediamtx_path(path_name, source, exists=path_name in existing_paths)

    for stale_path in existing_paths - seen_paths:
        logger.info("removing stale MediaMTX path %s (camera no longer in scope)", stale_path)
        delete_mediamtx_path(stale_path)

    logger.info("provisioned %d camera path(s)", len(seen_paths))


def main() -> None:
    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s")
    while True:
        try:
            run_once()
        except Exception:  # noqa: BLE001 -- one bad cycle must not kill the service; log and retry next interval.
            logger.exception("provisioning cycle failed")
        time.sleep(POLL_INTERVAL_SECONDS)


if __name__ == "__main__":
    sys.exit(main())
