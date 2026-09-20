"""Where the worker learns which cameras exist and how to process them.

`TrinetraApiCameraSource` calls the Trinetra `Federation.Api` — the platform's own camera
registry — and is the default. This is deliberate, not incidental: Model 3 already discovers
cameras through vendor VMS adapters and exposes them with stream references
(`GET /api/v1/vms` -> `GET /api/v1/vms/{id}/cameras`); this worker (Model 2) reads that
inventory rather than calling any upstream VMS/portal directly, so the AI layer never bypasses
the registry the rest of the platform already treats as authoritative. `StaticConfigCameraSource`
(a local YAML file) still exists behind the same `CameraSource` ABC for offline dev/testing
without hitting the network; select either with `CAMERA_SOURCE=trinetra|static`.

The schema below is not guessed — it's taken directly from the live OpenAPI document
(`GET /openapi/v1.json`): `GET /api/v1/vms` returns `VmsResponse[]` (each with an `id` used as
the path parameter below), `GET /api/v1/vms/{id}/cameras` returns `FederatedCameraResponse[]`
with `nativeCameraId`, `name`, `isEnabled`, and `streamReferences` (a list of stream addresses
— Model 3 never touches video itself, so these are references to hand to a player or to us).

`streamReferences` never carry a username/password (credentials are references, not fields).
When a camera's RTSP endpoint needs authentication, `TrinetraApiCameraSource` calls
`GET /api/v1/vms/{id}/credential/resolve` once per target and folds the login into the URL.
An explicit `RTSP_USERNAME` / `RTSP_PASSWORD` override takes precedence and skips that call.
"""

from __future__ import annotations

import logging
from abc import ABC, abstractmethod
from dataclasses import dataclass, field
from pathlib import Path

import httpx
import yaml

from capture.rtsp_url import apply_credentials
from lease_client import ClaimedCamera

logger = logging.getLogger(__name__)


@dataclass(frozen=True)
class CameraConfig:
    camera_id: str
    name: str
    rtsp_url: str
    processing_fps: float | None = None  # falls back to the worker-wide default when unset
    enabled_models: list[str] = field(default_factory=lambda: ["vehicle", "plate", "ocr"])
    webrtc_url: str | None = None
    hls_url: str | None = None


class CameraSource(ABC):
    @abstractmethod
    def list_cameras(self) -> list[CameraConfig]:
        ...


class StaticConfigCameraSource(CameraSource):
    """Reads camera definitions from a local YAML file (see `cameras.example.yaml`). Dev/offline
    use only — `TrinetraApiCameraSource` is what the worker uses by default."""

    def __init__(self, config_path: str | Path) -> None:
        self._config_path = Path(config_path)

    def list_cameras(self) -> list[CameraConfig]:
        if not self._config_path.exists():
            return []

        raw = yaml.safe_load(self._config_path.read_text()) or {}
        cameras = []
        for entry in raw.get("cameras", []):
            cameras.append(
                CameraConfig(
                    camera_id=entry["camera_id"],
                    name=entry.get("name", entry["camera_id"]),
                    rtsp_url=apply_credentials(
                        entry["rtsp_url"],
                        entry.get("rtsp_username"),
                        entry.get("rtsp_password"),
                    ),
                    processing_fps=entry.get("processing_fps"),
                    enabled_models=entry.get("enabled_models", ["vehicle", "plate", "ocr"]),
                )
            )
        return cameras


# --- Trinetra Federation.Api source -------------------------------------------------------


class TrinetraApiCameraSource(CameraSource):
    def __init__(
        self,
        base_url: str,
        api_key: str | None = None,
        timeout: float = 10.0,
        rtsp_username: str | None = None,
        rtsp_password: str | None = None,
    ) -> None:
        self._base_url = base_url.rstrip("/")
        # X-Api-Key is the documented scheme for a service integration like this worker; a
        # person authenticates with a Bearer token instead (POST /api/v1/auth/login) but a
        # long-lived machine credential is the right shape for an unattended process.
        headers = {"X-Api-Key": api_key} if api_key else {}
        self._client = httpx.Client(headers=headers, timeout=timeout)

        # An explicit RTSP_USERNAME / RTSP_PASSWORD override wins over anything the API would
        # resolve; when it is set we skip the resolve call entirely and let capture/registry.py
        # inject it. Only when it is absent do we ask the platform for the stored credential.
        self._rtsp_username = rtsp_username
        self._rtsp_password = rtsp_password
        self._cred_cache: dict[str, tuple[str | None, str | None]] = {}
        self._camera_cred_cache: dict[str, tuple[str | None, str | None]] = {}

    def list_cameras(self) -> list[CameraConfig]:
        targets_response = self._client.get(f"{self._base_url}/api/v1/vms")
        targets_response.raise_for_status()
        targets = targets_response.json()

        cameras: list[CameraConfig] = []
        for target in targets:
            target_id = target["id"]
            cameras_response = self._client.get(f"{self._base_url}/api/v1/vms/{target_id}/cameras")
            cameras_response.raise_for_status()

            # One resolve call per target (cached), never per camera — the registry returns a
            # target's whole inventory and every camera behind it shares the connector login.
            username, password = self._resolve_credentials(target_id)

            for raw in cameras_response.json():
                camera = self._parse_camera(target_id, raw, username, password)
                if camera is not None:
                    cameras.append(camera)

        logger.info(
            "Trinetra Federation.Api returned %d camera(s) across %d VMS target(s)",
            len(cameras), len(targets),
        )
        return cameras

    def _resolve_credentials(self, target_id: str) -> tuple[str | None, str | None]:
        """Fetch the stored RTSP login for a VMS target via
        `GET /api/v1/vms/{id}/credential/resolve` (the one route that returns secret material;
        needs the `credential.resolve` permission, held by the `DETECTION_WORKER` role).

        Returns `(None, None)` — meaning "use the stream URL as-is" — when an explicit override
        is configured, when the target has no stored credential, when this key is not scoped to
        the target, or when the call fails. Never raises: a missing credential is not fatal
        (the stream may be unauthenticated, or an operator may fix scope later).
        """
        if self._rtsp_username and self._rtsp_password:
            return (None, None)

        if target_id in self._cred_cache:
            return self._cred_cache[target_id]

        creds: tuple[str | None, str | None] = (None, None)
        try:
            resp = self._client.get(
                f"{self._base_url}/api/v1/vms/{target_id}/credential/resolve"
            )
            if resp.status_code == 200:
                body = resp.json()
                creds = (body.get("username"), body.get("password"))
                logger.info("Resolved a stored RTSP credential for VMS target %s", target_id)
            elif resp.status_code == 404:
                logger.info(
                    "VMS target %s has no stored credential, or this key is not scoped to it — "
                    "using the stream URL without credentials.", target_id,
                )
            elif resp.status_code == 403:
                logger.warning(
                    "API key lacks 'credential.resolve' for VMS target %s. Apply "
                    "db/versions/v1.5.sql and run scripts/check-detection-api-key.sql. "
                    "Falling back to an unauthenticated stream URL.", target_id,
                )
            elif resp.status_code == 503:
                logger.warning(
                    "credential/resolve for VMS target %s returned 503 (the platform could not "
                    "audit the access); using the stream URL without credentials.", target_id,
                )
            else:
                logger.warning(
                    "credential/resolve for VMS target %s returned HTTP %d; using the stream "
                    "URL without credentials.", target_id, resp.status_code,
                )
        except httpx.HTTPError as exc:
            logger.warning(
                "credential/resolve for VMS target %s failed (%s); using the stream URL "
                "without credentials.", target_id, exc,
            )

        self._cred_cache[target_id] = creds
        return creds

    def resolve_leased_camera(self, claimed: ClaimedCamera) -> CameraConfig | None:
        """Turns one entry of `CameraLeaseClient.claim()`'s result into a `CameraConfig` the
        capture loop can open — the dynamic-claim counterpart to `list_cameras()`/
        `_parse_camera()`. Resolves the camera's own stored credential the same way
        `list_cameras()` resolves a VMS target's, just against the matching endpoint for each
        source: `GET /vms/{targetId}/credential/resolve` for a VMS-discovered camera,
        `GET /cameras/{id}/credential/resolve` for a standalone registry one.

        Returns `None` (logged, not raised) when the claimed camera has no usable `rtsp://`
        stream reference — the claim endpoint's job is scope/ownership, not stream validity."""
        if claimed.source == "VMS":
            # "vms:{targetId}:{nativeCameraId}" -> the plain "{targetId}:{nativeCameraId}"
            # DetectionRepository.ResolveCameraAsync (and this worker's existing VMS discovery
            # path) already uses as a camera id.
            _, target_id, native_camera_id = claimed.camera_ref.split(":", 2)
            camera_id = f"{target_id}:{native_camera_id}"
            username, password = self._resolve_credentials(target_id)
        else:
            camera_id = claimed.camera_ref
            username, password = self._resolve_camera_credential(claimed.camera_ref)

        rtsp_url = claimed.stream_reference
        if not rtsp_url or not rtsp_url.startswith("rtsp://"):
            logger.warning(
                "Skipping claimed camera %s (%s): no rtsp:// stream reference (got: %r)",
                camera_id, claimed.name, rtsp_url,
            )
            return None

        return CameraConfig(
            camera_id=camera_id,
            name=claimed.name or camera_id,
            rtsp_url=apply_credentials(rtsp_url, username, password),
            processing_fps=None,
            webrtc_url=claimed.native_webrtc_url,
            hls_url=claimed.native_hls_url,
        )

    def _resolve_camera_credential(self, camera_id: str) -> tuple[str | None, str | None]:
        """The registry-camera counterpart to `_resolve_credentials` — same caching, override
        and never-raises posture, against `GET /cameras/{id}/credential/resolve`
        (`camera.credential.resolve`, granted to `DETECTION_WORKER` by v1.29)."""
        if self._rtsp_username and self._rtsp_password:
            return (None, None)

        if camera_id in self._camera_cred_cache:
            return self._camera_cred_cache[camera_id]

        creds: tuple[str | None, str | None] = (None, None)
        try:
            resp = self._client.get(
                f"{self._base_url}/api/v1/cameras/{camera_id}/credential/resolve"
            )
            if resp.status_code == 200:
                body = resp.json()
                creds = (body.get("username"), body.get("password"))
                logger.info("Resolved a stored credential for registry camera %s", camera_id)
            elif resp.status_code == 404:
                logger.info(
                    "Registry camera %s has no stored credential, or this key is not scoped "
                    "to it — using the stream URL without credentials.", camera_id,
                )
            elif resp.status_code == 403:
                logger.warning(
                    "API key lacks 'camera.credential.resolve' for camera %s. Apply "
                    "db/versions/v1.29.sql. Falling back to an unauthenticated stream URL.",
                    camera_id,
                )
            elif resp.status_code == 503:
                logger.warning(
                    "credential/resolve for camera %s returned 503 (the platform could not "
                    "audit the access); using the stream URL without credentials.", camera_id,
                )
            else:
                logger.warning(
                    "credential/resolve for camera %s returned HTTP %d; using the stream "
                    "URL without credentials.", camera_id, resp.status_code,
                )
        except httpx.HTTPError as exc:
            logger.warning(
                "credential/resolve for camera %s failed (%s); using the stream URL "
                "without credentials.", camera_id, exc,
            )

        self._camera_cred_cache[camera_id] = creds
        return creds

    def _parse_camera(
        self,
        target_id: str,
        raw: dict,
        username: str | None = None,
        password: str | None = None,
    ) -> CameraConfig | None:
        if not raw.get("isEnabled", True):
            return None

        # nativeCameraId is only unique within one VMS target (federated_camera's actual
        # primary key is (target_id, native_camera_id)); prefixing with the target keeps camera
        # ids globally unique when a worker owns cameras from more than one VMS.
        camera_id = f"{target_id}:{raw['nativeCameraId']}"

        stream_references = raw.get("streamReferences") or []
        rtsp_url = next((s for s in stream_references if s.startswith("rtsp://")), None)
        if rtsp_url is None:
            logger.warning(
                "Skipping camera %s: no rtsp:// entry in streamReferences (has: %s)",
                camera_id, stream_references,
            )
            return None

        return CameraConfig(
            camera_id=camera_id,
            name=raw.get("name") or camera_id,
            # A no-op when username/password are None or when the reference already carries its
            # own userinfo; otherwise the login is percent-encoded into the URL.
            rtsp_url=apply_credentials(rtsp_url, username, password),
            processing_fps=None,
            webrtc_url=next((s for s in stream_references if "whep" in s), None),
            hls_url=next((s for s in stream_references if s.endswith(".m3u8")), None),
        )


def build_camera_source(settings) -> CameraSource:  # noqa: ANN001 - avoids a config.py import cycle
    """Resolves the configured `CameraSource`, mirroring `backends/registry.py`'s pattern."""
    if settings.camera_source == "static":
        return StaticConfigCameraSource(settings.cameras_config_path)

    if not settings.trinetra_api_base_url:
        raise ValueError("CAMERA_SOURCE=trinetra requires TRINETRA_API_BASE_URL to be set.")

    return TrinetraApiCameraSource(
        base_url=settings.trinetra_api_base_url,
        api_key=settings.trinetra_api_key,
        rtsp_username=settings.rtsp_username,
        rtsp_password=settings.rtsp_password,
    )
