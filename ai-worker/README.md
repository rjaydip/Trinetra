# Trinetra AI Worker

Standalone Python service: captures a camera's video feed, runs real vehicle/plate/OCR
inference on it, and (once its ingest endpoint exists — Phase 2) posts normalized detection
events to `Federation.Api` for watchlist matching and alerting. It never runs inference inside
an HTTP request — it is the "AI Worker" box in the platform's own architecture diagram, kept
fully separate from `Federation.Api`/`.Storage`.

This worker was built in two stages against `Federation.Api`, which already exists and is
live (it's Model 3, not new work from this component): camera discovery
(`GET /api/v1/vms`, `GET /api/v1/vms/{id}/cameras`) is wired up and working now — the platform's
own camera registry is the source of truth for what cameras exist, this worker never calls an
upstream VMS/portal directly to discover them. Phase 2 has since been built on the `.NET` side:
`POST /api/v1/detections` (ingest, with watchlist matching in the same transaction) and
`POST /api/v1/worker-health/heartbeat` now exist — `backend_client.py` and
`monitoring/heartbeat.py` are written against their exact contracts, but neither has been
integration-tested end-to-end yet (needs a real `TRINETRA_API_KEY` provisioned via the new
`POST /api/v1/api-keys` endpoint). Sending the actual snapshot image (not just a local path) is
still a follow-up on the worker side — the ingest endpoint already accepts an optional
`evidence.snapshotBase64` field for it.

## Quickstart

```bash
cd ai-worker
python3 -m venv .venv && source .venv/bin/activate
pip install -r requirements.txt

cp .env.example .env
# set TRINETRA_API_BASE_URL and TRINETRA_API_KEY (see "Camera source" below)

python worker.py            # long-running: capture + inference + /health + /metrics
python smoke_test.py samples/bus.jpg    # one-shot: run the real pipeline against an image
```

Runs directly against a venv — no Docker required for development. A `Dockerfile` exists for
the eventual deployment shape but isn't the thing this is built or validated against day to day.

## Camera source

`CAMERA_SOURCE=trinetra` (default) calls the Trinetra `Federation.Api` to discover cameras —
`GET /api/v1/vms` for the caller's VMS targets, then `GET /api/v1/vms/{id}/cameras` per target
for the actual camera rows (`nativeCameraId`, `name`, `isEnabled`, `streamReferences`). There is
no camera list checked into this repo, and the worker never calls an upstream VMS or portal
directly — schema confirmed against the live `GET /openapi/v1.json` document, not guessed.

`TRINETRA_API_BASE_URL` has deliberately no default in `config.py` — which host serves the API
is deployment configuration, not something to bake into source. Set it in `.env`.
`TRINETRA_API_KEY` is required too: the discovery endpoints are permission-gated (`vms.read`) — a
service integration like this worker authenticates with an `X-Api-Key` header (a person would
use a Bearer login token instead, per the API's own docs). Without a valid key, camera
discovery fails clearly with `401 Unauthorized` rather than silently returning nothing.

The key must act through the **`DETECTION_WORKER`** role, whose access group is `ACTIVE` and
scoped to the target's organization unit. That role carries `vms.read`, `observation.write`,
`worker.heartbeat` and — after `db/versions/v1.5.sql` — `credential.resolve`. Use
`scripts/create-detection-api-key.sh` to provision one, or `scripts/check-detection-api-key.sql`
to audit and repair a key that already exists (e.g. one issued before v1.5).

`monitoring/heartbeat.py` posts through the same key. From `db/versions/v1.13.sql` a heartbeat
is bound to that key: the worker is `(key, WORKER_INDEX-of-COUNT, hostname)`, `worker.heartbeat`
is submit-only, and the server — not the worker's clock — stamps the last-seen time (the worker's
reported time is kept only to show clock drift). Nothing to change here; the push stays a no-op
until `BACKEND_HEARTBEAT_URL` is set.

### RTSP stream credentials

`streamReferences` from the registry never include a username/password (credentials are
references, not fields). When a camera's RTSP endpoint needs authentication, the worker gets
the login one of two ways, in order:

1. `RTSP_USERNAME` / `RTSP_PASSWORD` in `.env` (or per-camera `rtsp_username`/`rtsp_password`
   in `cameras.yaml`) — a straight override, useful when every camera shares one login.
2. `GET /api/v1/vms/{id}/credential/resolve` — resolves the credential stored for that
   connector target (`credential.resolve` permission). `TrinetraApiCameraSource` calls it once
   per target, caches the result in memory, and folds the login into the RTSP URL. A missing
   credential, an out-of-scope target, or a 403/503 is logged and the stream URL is used
   as-is — never fatal. The audit trail is on the `.NET` side (`credential_access_log`).

Either way the credentials are percent-encoded into the `rtsp://` URL at connect time and
redacted from every log line (`capture/rtsp_url.py`, and the filter in `worker.py`).

`CAMERA_SOURCE=static` reads `cameras.yaml` (see `cameras.example.yaml`) instead, for
offline dev/testing without hitting the network.

Either way, cameras come back as a `CameraConfig` list (`camera_source.py`) — the rest of the
worker (`worker.py`, `scaling.py`, `pipeline.py`) doesn't know or care which source produced it.

## Capture backends — which one, and how to switch

Four ways to actually read a camera's RTSP stream, all behind the same `FrameSource`
interface (`capture/base.py`). Switch with one setting; nothing else in the worker changes:

```bash
CAPTURE_BACKEND=opencv        # default
```

| Backend | Best when | Cost | Verified |
|---|---|---|---|
| **opencv** (default) | Zero setup friction — pure `pip install`, identical locally and in Docker. Good default for getting cameras flowing fast. | Least control over the decode pipeline; PTS reporting is occasionally unreliable, so there's a wall-clock fallback for it. | Yes — real live RTSP stream |
| **ffmpeg** | Want ffmpeg's own hwaccel flags (`videotoolbox` on Mac, `vaapi`/`cuda` on Linux), or need to match the portal's own documented `ffplay`/`ffprobe` commands directly. | One extra OS process per camera; needs the `ffmpeg`/`ffprobe` binaries on PATH — not pip-installable. | Yes — real live RTSP stream |
| **gstreamer** | Deploying on Linux with more cameras and want the most production-grade, purpose-built media framework. Native per-buffer PTS — the cleanest timing of the four. Also the direct stepping stone to `deepstream`, since a DeepStream pipeline *is* a GStreamer pipeline with NVIDIA elements swapped in (`DeepStreamFrameSource` subclasses `GStreamerFrameSource` and only overrides the pipeline string). | Not pip-installable at all — needs system packages (`gstreamer` + `gst-plugins-*` + PyGObject: Homebrew locally, `apt-get gstreamer1.0-* python3-gi` in Docker). Heaviest footprint, harder to debug. | Yes — real live RTSP stream |
| **deepstream** | You have actual NVIDIA GPU hardware (Jetson or a cloud GPU box) and need batched hardware decode+inference across many cameras — the real answer to the platform's horizontal/vertical GPU scaling requirement at scale. | Linux + NVIDIA DeepStream SDK only. | **No** — cannot run on this dev machine (Apple Silicon, no NVIDIA GPU). Written to the portal's documented pipeline shape and self-diagnoses (`deepstream_available()`) rather than failing obscurely, but is unverified until run on real hardware. |

**Recommendation for this hackathon:** stay on `opencv` (the default) unless a concrete problem
shows up — it has the least deployment friction and the most validation behind it. Move to
`gstreamer` only if deploying on Linux and robustness at higher camera counts becomes the
limiting factor. Reach for `deepstream` only once real GPU hardware is actually available.

All three of `opencv`/`ffmpeg`/`gstreamer` were validated against a real live RTSP stream (a
local MediaMTX instance re-publishing a sample clip) during development — not just against
static images. `CAPTURE_BACKEND` is a per-worker-process setting today: every camera a given
worker instance owns uses the same backend. A per-camera override would be straightforward to
add on top of `CameraConfig` if a real need for mixing backends on one instance comes up.

## AI model backends — same idea, different axis

Independently of *how* frames are captured, *what* reads them is also swappable per stage —
`VEHICLE_BACKEND` / `PLATE_BACKEND` / `OCR_BACKEND`, each `local` (in-process model) or
`remote` (a hosted AI API by URL + key), with `heuristic` (classical, weight-free OpenCV) as an
extra option for the plate stage. See `backends/registry.py` and `.env.example`.

## Scaling and monitoring

- **Horizontal:** `WORKER_INDEX` / `WORKER_COUNT` deterministically partition the camera list
  across worker instances (`scaling.py`) — add a worker, bump `WORKER_COUNT`, restart the set.
- **Vertical / when to add GPU vs. add a worker:** watch `GET /metrics` (Prometheus format).
  Sustained high `ai_worker_inference_latency_seconds` or `ai_worker_frames_dropped_total` on
  one worker is the signal to give it a GPU or shrink its camera list.
- `GET /health` for liveness.

## Detection contract — the closed sets

`POST /api/v1/detections` validates two fields against fixed sets:

- **`eventType`** must be `ANPR_DETECTED` or `VEHICLE_DETECTED` — the only values
  `pipeline.py` emits today. The API returns `400` for anything else, and the `detection` table
  has a matching `CHECK` constraint. This is deliberate: search and dashboards aggregate on
  `event_type`, so a free-text value nothing can reason about is worse than a rejection.
- **`confidence`** must be a number in `0..1` (`NaN` rejected).

**Adding a detection type is an API-first, three-place change, deployed in this order:**
1. `db/versions/vN.sql` — widen the `detection.event_type` `CHECK`.
2. `src/Trinetra.Federation.Api/Endpoints/DetectionEndpoints.cs` — add it to `DetectionEventTypes`.
3. this worker — start emitting it.

Deploy the API + migration before the new worker version, or the new type's detections are
rejected (a clean `400` the worker logs and drops — not a poison message, but still lost).
`vendorEventType` (free text, already on the contract) stays the escape hatch for
device-specific labels.

## What's deliberately not here yet

- End-to-end integration testing of `backend_client.py`/`heartbeat.py` against the now-built
  `.NET` ingest/heartbeat endpoints — needs a real `TRINETRA_API_KEY`.
- Sending the actual snapshot image bytes (`evidence.snapshotBase64`) rather than a local path
  — the `.NET` endpoint already accepts it, the worker doesn't send it yet.
- A Kafka metadata producer/consumer, and SignalR/real-time alert push (`GET
  /api/v1/watchlist/alerts` is poll-only for now).
- Dynamic, DB-backed lease-based camera assignment across workers (mirroring the existing
  `.NET` side's `LeaseStore.cs`/`worker_node` pattern) — `WORKER_INDEX`/`WORKER_COUNT` static
  partitioning is the v1.
- Docker as the day-to-day dev/validation path (it's written, just not what's exercised).
