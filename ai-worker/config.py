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

    # --- Worker identity / horizontal scaling ---------------------------------------------------
    # worker_index/worker_count still name this instance (heartbeat's workerId, metrics labels)
    # and still drive scaling.py's static hash-partition when CAMERA_SOURCE=static (no backend
    # to claim cameras from in that mode). Against CAMERA_SOURCE=trinetra with an API key set,
    # camera ownership is decided dynamically instead — see worker_capacity below and
    # lease_client.py — so these two no longer bound how many cameras this instance can run.
    worker_index: int = 0
    worker_count: int = 1

    # --- Dynamic camera claiming (v1.27/v1.28; lease_client.py) -------------------------------
    # How many cameras one worker instance claims from POST /cameras/claim. The backend returns
    # VMS-discovered candidates before standalone registry ones, up to this count.
    worker_capacity: int = 50
    # Must stay comfortably below the backend's 90s lease TTL — this is the renewal interval, and
    # missing several ticks in a row (not just one slow poll) is what should cost a worker its
    # cameras.
    camera_claim_poll_seconds: float = 30.0
    # When true, once this worker's first non-empty claim succeeds it never asks for more
    # cameras than it holds at that moment — it keeps renewing/holding what it already has (the
    # backend's own claim/trim logic already favors a worker's longest-held leases), but stops
    # auto-growing into any newly-freed capacity on its own. Off by default: this is a real
    # behavior change from "always claim up to worker_capacity," opt in deliberately. Resets on
    # process restart — a per-process cap, not a persisted assignment. See _run_claim_loop's own
    # docstring in worker.py for the full trade-off.
    camera_claim_sticky: bool = False

    # --- Detection batching (detection_batch.py) ------------------------------------------------
    # Routine (VEHICLE_DETECTED, no plate) detections batch and flush together; a plate read
    # (ANPR_DETECTED) always submits immediately (backend_client.py), never through this queue —
    # this worker can't know locally whether a plate is a watchlist match, so every plate read is
    # treated as potentially time-sensitive. Flush fires on whichever of these two comes first.
    detection_flush_max: int = 50
    detection_flush_interval_seconds: float = 2.0
    # Where a batch that failed to POST (backend down, network partition) is spooled for retry —
    # one JSON array file per failed batch, replayed oldest-first on the next start/reconnect.
    detection_spool_dir: str = str(AI_WORKER_ROOT / "detection_spool")
    # Bounds local retention during an extended outage: past this many pending files, the oldest
    # is dropped (logged) rather than growing disk usage without limit.
    detection_spool_max_files: int = 500

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
