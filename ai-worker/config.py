"""Runtime configuration, read from environment variables / a `.env` file.

Every AI-relevant knob the brief calls out (per-stage backend choice, thresholds, sampling
rate, device, worker partitioning) lives here as one typed object rather than scattered
`os.environ` reads, so `pipeline.py` and the backend registry never hard-code a value.
"""

from __future__ import annotations

from pathlib import Path

from pydantic import Field
from pydantic_settings import BaseSettings, SettingsConfigDict

AI_WORKER_ROOT = Path(__file__).resolve().parent


class Settings(BaseSettings):
    model_config = SettingsConfigDict(env_file=".env", env_file_encoding="utf-8", extra="ignore")

    # --- Per-stage backend selection --------------------------------------------------------
    # "local" runs the model in-process; "remote" calls a hosted AI API; "heuristic" (plate
    # stage only) uses a classical, weight-free OpenCV detector. Changing a model or switching
    # to a hosted API is a config change only — pipeline.py never branches on this.
    vehicle_backend: str = Field(default="local", pattern="^(local|remote)$")
    plate_backend: str = Field(default="heuristic", pattern="^(local|remote|heuristic)$")
    ocr_backend: str = Field(default="local", pattern="^(local|remote)$")

    # --- Local model weights ------------------------------------------------------------------
    vehicle_model_path: str = "yolov8n.pt"
    plate_model_path: str | None = None  # only needed when plate_backend == "local"
    device: str = Field(default="cpu", pattern="^(cpu|cuda|mps)$")

    # --- RTSP credentials --------------------------------------------------------------------
    # The registry never returns credential values, so a camera discovered via
    # CAMERA_SOURCE=trinetra arrives as a credential-free rtsp:// URL. When the camera requires
    # authentication, set these; they are injected into the URL at connect time (capture/
    # rtsp_url.py), held in memory only, and redacted from logs. Ignored for a URL that already
    # carries its own userinfo (e.g. one embedded in cameras.yaml).
    rtsp_username: str | None = None
    rtsp_password: str | None = None

    # --- Remote AI API configuration (per stage; only used when that stage's backend is "remote")
    vehicle_api_url: str | None = None
    vehicle_api_key: str | None = None
    plate_api_url: str | None = None
    plate_api_key: str | None = None
    ocr_api_url: str | None = None
    ocr_api_key: str | None = None
    remote_api_timeout_seconds: float = 10.0

    # --- Confidence thresholds (brief §37) -----------------------------------------------------
    vehicle_confidence_threshold: float = 0.5
    plate_confidence_threshold: float = 0.4
    ocr_confidence_threshold: float = 0.5

    # --- Frame sampling (brief §9) --------------------------------------------------------------
    processing_fps: float = 2.0

    # --- Capture backend: how a stream's frames actually get read ------------------------------
    # opencv (pip-only, default) | ffmpeg (needs ffmpeg/ffprobe on PATH) | gstreamer (needs
    # GStreamer + PyGObject as system packages) | deepstream (NVIDIA GPU + SDK, Linux only).
    capture_backend: str = Field(default="opencv", pattern="^(opencv|ffmpeg|gstreamer|deepstream)$")

    # --- Worker horizontal partitioning (static v1 — see scaling.py) ---------------------------
    worker_index: int = 0
    worker_count: int = 1

    # --- Camera source: "trinetra" (Federation.Api, default) or "static" (local YAML, dev/offline)
    camera_source: str = Field(default="trinetra", pattern="^(trinetra|static)$")
    cameras_config_path: str = str(AI_WORKER_ROOT / "cameras.yaml")  # only used when "static"

    # --- Trinetra Federation.Api ------------------------------------------------------------
    # Used for camera discovery (GET /api/v1/vms, GET /api/v1/vms/{id}/cameras — the platform's
    # own camera registry, not any upstream VMS/portal called directly) and, once it exists,
    # detection ingest (POST /api/v1/detections — Phase 2). No hardcoded default: which host
    # serves the API is deployment configuration, not something to bake into source.
    trinetra_api_base_url: str | None = None
    trinetra_api_key: str | None = None  # X-Api-Key; see camera_source.py's docstring
    backend_heartbeat_url: str | None = None
    backend_heartbeat_interval_seconds: float = 15.0

    # --- Monitoring HTTP surface -----------------------------------------------------------------
    metrics_host: str = "0.0.0.0"
    metrics_port: int = 8000

    # --- Evidence -----------------------------------------------------------------------------
    evidence_dir: str = str(AI_WORKER_ROOT / "evidence")

    # --- Logging -----------------------------------------------------------------------------
    # DEBUG adds the per-stage pipeline trace (vehicle regions -> plate candidates -> OCR text
    # -> normalized), which is how you see which stage drops a plate.
    log_level: str = "INFO"


settings = Settings()
