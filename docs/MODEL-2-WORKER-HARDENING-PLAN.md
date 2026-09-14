# Model 2 — Worker Hardening & Deployment Plan (ai-worker + Federation.Worker)

Status: proposal for review. Nothing in this document is implemented yet. Grounded in a
file-by-file read of `ai-worker/` and `src/Trinetra.Federation.Worker/` (plus
`Federation.Runtime`/`Federation.Storage`) as of 2026-09-14, not from the specs alone — every
claim below cites the source it comes from.

This covers two independent worker systems that happen to share the same four concerns —
error handling, multi-instance coordination, performance observability, and Docker/bare-metal
readiness — plus the specific question of `'@'` characters inside credentials. The two systems
are currently in opposite states on several axes; the plan below is ordered so the weaker side
borrows the pattern already proven on the stronger side wherever one exists, instead of
inventing a second design.

---

## 0. The gap that blocks the stated goal

The target behavior is: *ai-worker detects every number plate seen by a camera since it started,
saves it in an easily searchable form, and a user can search a plate and get its track back.*

That path does not exist end-to-end today. `ai-worker/backend_client.py:1-8,27-34` posts each
detection to `POST /api/v1/detections`. That endpoint is not implemented on the `.NET` side —
Model 2 is spec-only there (per `CLAUDE.md`: "the rest of Model 2 is still spec only"). Today:

- If `TRINETRA_API_BASE_URL` is unset, the client no-ops and only logs.
- If it is set and the POST fails, `backend_client.py:36-40` logs the error and **drops** the
  detection — no retry, no local queue, no dead-letter spool.
- Even when the POST would succeed, there is no receiving table or search endpoint on the
  `.NET` side to land it in.

Nothing else in this plan matters to the end user until this exists, so it is Phase 0, not a
footnote.

---

## 1. `'@'` in RTSP / VMS credentials

Already solved, independently, on both sides. No work item here — recorded so it isn't
re-litigated.

- **ai-worker**: `capture/rtsp_url.py:21-39` (`apply_credentials`) percent-encodes both username
  and password with `quote(..., safe='')` before building the URL netloc (`rtsp_url.py:38`), so
  `@`, `:`, `/` in a password round-trip correctly. Log redaction is a separate concern, handled
  globally via a logging filter (`worker.py:29-44`, `rtsp_url.py:42-44`).
- **Federation.Worker**: credentials are never embedded in a URL at all. `RtspSession.cs:113`
  strips `UserName`/`Password` from the request `UriBuilder` before the request line is built,
  and sends them only via a Digest `Authorization` header (`RtspSession.cs:128-133`,
  `DigestAuthHandler.cs`). HTTP/ONVIF adapters follow the same pattern via
  `TargetHttpClientProvider.cs`. This sidesteps the whole class of bug by construction —
  stronger than URL-encoding, since nothing about the credential ever touches URL grammar.

---

## 2. Error handling

### 2.1 ai-worker — current state

Per-stage containment inside the pipeline is solid: a vehicle-detection failure returns empty
events (`pipeline.py:113-119`), a plate-detection failure logs and continues with `plates=[]`
(`pipeline.py:145-151`), an OCR failure logs and continues with `anpr=None`
(`pipeline.py:159-165`). `worker.py:66-70` wraps `pipeline.process_frame()` per frame, so one bad
frame logs and the loop moves on. Evidence-save failure (`pipeline.py:218-220`) degrades to
`snapshot_path=None` rather than dropping the detection.

RTSP reconnect already has exponential backoff and a consecutive-failure cap
(`capture/rtsp_source.py`), though its own docstring (lines 3-5) flags it as **not yet
live-tested against a real RTSP source** — that verification is a prerequisite before relying on
it in production, not a code change.

Two real gaps:

1. **Camera-thread crash is unrecoverable.** `worker.py:62`, `with build_frame_source(...) as
   source:` — if `build_frame_source()`/`_connect()` raises before the frame loop starts, or if
   `source.frames()` itself raises (as opposed to just yielding), that exception is outside the
   per-frame try/except at line 66 and kills the whole per-camera daemon thread. Nothing
   restarts it; that camera is silently dropped until the process is restarted by hand.
2. **No GPU/OOM-specific handling.** A `RuntimeError: CUDA out of memory` from an inference call
   is caught by the same generic `except Exception` as any other stage error
   (`pipeline.py:113-119` etc.) — logged and counted identically to a normal detection miss, with
   no backoff and no metric that distinguishes "the model is failing" from "the GPU is out of
   memory."

### 2.2 Federation.Worker — current state

Resilience is centralized, not ad hoc: `Federation.Runtime/ConnectorResilience.cs` wires Polly
with a circuit breaker (line 52) that explicitly excludes `AuthException`/`CapabilityException`
from tripping it (comment at line 54) — matching `CLAUDE.md` rule 4 (never retry a rejected
credential across the estate) — and a retry policy capped at `MaxRetryAttempts = 2` (line 81)
that honors `RateLimitedException.RetryAfter` (line 99). `Program.cs:37-47` fails fast at startup
on invalid `WorkerOptions`. No changes needed here; this is the reference implementation the
ai-worker side should eventually match in spirit (typed exceptions, no ad hoc retry-in-adapter).

### 2.3 Work items

| # | Item | Where |
|---|---|---|
| E1 | Wrap `build_frame_source()` / `source.frames()` the same way the per-frame path is wrapped; on failure, log with camera id and re-raise into a supervisor (see E2), not silently die. | `ai-worker/worker.py` |
| E2 | Add a supervisor that restarts a dead per-camera thread with backoff (reuse the backoff shape already in `capture/rtsp_source.py`), capped so a permanently-broken camera doesn't spin forever — surface it as unhealthy instead. | `ai-worker/worker.py` |
| E3 | Distinguish CUDA/OOM exceptions from generic stage exceptions in the except blocks; emit a separate counter/label so this is visible before it degrades into a crash loop. | `ai-worker/pipeline.py`, `monitoring/metrics.py` |
| E4 | Live-test the RTSP reconnect path against a real disconnect (pull a cable / stop the stream) before depending on it — currently unverified per the module's own docstring. | `ai-worker/capture/rtsp_source.py` |
| E5 | `backend_client.py`: on detection POST failure, spool to a local append-only file instead of dropping; a background flusher retries the spool when the backend becomes reachable again. Bounded by disk quota / max spool age. | `ai-worker/backend_client.py` |

---

## 3. Multi-instance coordination / load balancing

### 3.1 Federation.Worker — already correct, copy this pattern

Dynamic, PostgreSQL-lease-based, not static config. `Program.cs:10-14` states the design
directly: each instance claims a bounded share of connector targets via a lease; the fleet
rebalances itself when an instance is added or dies; no external orchestrator. Backed by
`Federation.Storage/LeaseStore.cs` and `Runtime/LeaseManager.cs` (a `BackgroundService`,
`LeaseManager.cs:25`), which raises `TargetAcquired`/`TargetReleased` events (lines 56, 59) that
`ConnectorSupervisor` (`Program.cs:92`) subscribes to in order to start/stop polling a target
dynamically. `WorkerOptions.MaxTargets` / `LeaseTtl` bound per-instance load, and a dead
instance's leases simply expire and get reclaimed — no manual rebalancing step.

### 3.2 ai-worker — static, and the known weak point

`scaling.py:22-35` partitions cameras by `sha256(camera_id) % worker_count == worker_index` —
every instance fetches the same full camera list and keeps only its shard. `scaling.py:6-12`
documents this as a deliberate placeholder and names the `.NET` lease mechanism as the intended
Phase-2 replacement — this plan follows that pointer rather than inventing something new.

Consequences today: scaling up/down requires restarting **every** instance with a new
`WORKER_COUNT`/`WORKER_INDEX`; if one instance dies, its shard of cameras goes unprocessed until
someone manually restarts the fleet with a smaller count.

The heartbeat push that would make "instance X is dead" observable already exists as a
documented no-op — `monitoring/heartbeat.py:1-6` only sends once `BACKEND_HEARTBEAT_URL` is set,
and per `CLAUDE.md` there is still no live consumer on the `.NET` side (`ai_worker_health`,
v1.13).

### 3.3 Work items

| # | Item | Where |
|---|---|---|
| M1 | Add a lease table for camera-to-ai-worker assignment, same shape as `federation.worker_node` / `LeaseStore.cs` — `(camera_id, worker_id, leased_until)` with a claim/renew/expire cycle. | new `db/versions/v1.X.sql`, `Federation.Storage` or a small Python-side client against the same table |
| M2 | Replace `scaling.py`'s hash partition with lease claims: on startup and on a poll interval, claim up to `N` cameras not currently leased by another live worker; renew held leases; release on graceful shutdown. | `ai-worker/scaling.py`, `worker.py` |
| M3 | Wire `monitoring/heartbeat.py` against the real `ai_worker_health` table once `BACKEND_HEARTBEAT_URL` has a consumer — needed so a dead worker's leases expire promptly rather than waiting out a long TTL. | `ai-worker/monitoring/heartbeat.py`, `.NET` heartbeat endpoint (v1.13 schema already exists) |
| M4 | Validate under `tools/Trinetra.VmsSimulator`-style synthetic load: kill an ai-worker instance mid-run and confirm its cameras get reclaimed within the TTL; add an instance and confirm rebalance without restarting survivors. | test harness, both worker types |

---

## 4. Single camera vs. NVR/VMS-aggregated cameras

Federation.Worker already gets this right structurally: it polls per VMS target, never per
camera (`CLAUDE.md` rule 1; `IVmsAdapter` has no `GetCameraAsync(id)` by design) — this is
enforced by the interface shape, not convention.

ai-worker's `camera_source.py` / `capture/registry.py` run one capture thread per camera
regardless of whether that camera is direct-RTSP or fed through an NVR/VMS aggregator. This is
*correct* for the capture stage — video decode is inherently per-stream, an NVR doesn't change
that — but there is no NVR-aware batching anywhere else in ai-worker (health/liveness checks,
discovery). That's acceptable today because discovery is already delegated to `Federation.Api`
(`GET /api/v1/vms/{id}/cameras`, per `CLAUDE.md`), which does the one-call-per-VMS-not-per-camera
work correctly upstream. No work item here beyond what Phase 3 (below) already covers for
per-camera health surfacing.

---

## 5. Performance observability

### 5.1 ai-worker — mostly built, a few gaps

Prometheus metrics exist and are reasonably complete: `frames_captured_total`,
`frames_processed_total`, `frames_dropped_total{reason}`, `inference_latency_seconds{stage}`
(histogram), `detections_total`, `errors_total{stage}`, `active_cameras`, `worker_info`
(`monitoring/metrics.py:20-55`), served via `/metrics` and `/health` on a separate FastAPI
thread from the capture loops (`monitoring/app.py`, `worker.py:124`) — deliberately isolated so
metrics stay reachable even if a camera thread hangs.

Gaps:
- `frames_dropped_total` is defined but has no `.inc()` call site found in `worker.py` /
  `pipeline.py` / `capture/*` — confirm with a repo-wide grep, then either wire it or drop it.
- No per-camera FPS gauge — only cumulative counters, so FPS must be derived externally via
  `rate()`.
- No queue-depth metric, because there is no queue — processing is synchronous per-camera-thread.
  Worth deciding whether that's the intended long-term model before adding a metric for
  something that structurally doesn't exist.
- No GPU utilization/memory metric.

### 5.2 Federation.Worker — needs confirming, likely a real gap

No metrics/Prometheus file was found under `Federation.Runtime` or `Federation.Worker` in this
pass. The only operational signal is `AddSystemd()` (`Program.cs:25`) giving coarse
process-level liveness (`READY=1`/`STOPPING=1`) — real, but says nothing about per-target
throughput, lease churn, or adapter error rates. Confirm against
`docs/ARCHITECTURE-MODEL-3.md` whether a metrics endpoint is specified-but-unbuilt or genuinely
out of scope before treating this as a gap.

### 5.3 Work items

| # | Item | Where |
|---|---|---|
| O1 | Wire or remove `frames_dropped_total`. | `ai-worker/worker.py`, `pipeline.py`, `capture/*` |
| O2 | Add a per-camera FPS gauge and (if `device=cuda`) GPU utilization/memory gauges. | `ai-worker/monitoring/metrics.py` |
| O3 | Confirm Model-3 metrics scope against `docs/ARCHITECTURE-MODEL-3.md`; if in scope, add a `/metrics` endpoint to `Federation.Worker` exposing lease count, active targets, adapter error rate, circuit-breaker state per target. | `src/Trinetra.Federation.Worker`, `Federation.Runtime` |

---

## 6. Docker / bare-metal readiness

### 6.1 Current state — inverted between the two workers

| | ai-worker | Federation.Worker |
|---|---|---|
| Dockerfile | Exists, but its own header states "NOT used to build or run this pass... Docker packaging comes later" (`Dockerfile:1-3`). `python:3.11-slim` base, no CUDA/cuDNN, no GPU runtime flags, no non-root user, no `HEALTHCHECK`. | **Does not exist.** Root `Dockerfile` builds the API only (per `CLAUDE.md`, "Container image + Scalar" — API-equivalent target, not the worker). |
| systemd unit | **Does not exist** under `deploy/systemd/`. | Exists and templated: `deploy/systemd/trinetra-worker@.service` + `worker.env.example`, wired to `Program.cs`'s `Type=notify` / journald comments (lines 18-24). |
| docker-compose | Not found; the ai-worker Dockerfile comments reference it as a future service, not built. | N/A |

### 6.2 Work items

| # | Item | Where |
|---|---|---|
| D1 | Build a real ai-worker Dockerfile: CUDA/cuDNN base image for GPU builds (a CPU-only variant if `device=cpu` needs to run without the toolkit), non-root user, `HEALTHCHECK` against `/health`, document `--gpus all` / nvidia-container-toolkit prerequisite. | `ai-worker/Dockerfile`, `docs/DEPLOYMENT.md` |
| D2 | Add `deploy/systemd/trinetra-ai-worker@.service`, matching the shape of `trinetra-worker@.service`, for bare-metal parity with the `.NET` worker. | `deploy/systemd/` |
| D3 | Decide whether Federation.Worker needs a container target at all, given bare-metal/systemd is its documented primary path — if yes, add it to the root `Dockerfile` (multi-stage, second target) rather than a second Dockerfile, to keep one image-build story. | `Dockerfile`, `docs/DEPLOYMENT.md` |
| D4 | `docker-compose.yml` for local dev bringing up API + Worker + ai-worker + Postgres/Kafka together, once D1/D3 exist. | repo root |

---

## 7. Build order

Phases are ordered by dependency, not by the numbering above.

1. **Phase 0** — `POST /api/v1/detections` ingest endpoint + `plate_detections`-style table +
   `GET /api/v1/detections?plate=...` search endpoint on the `.NET` side (§0). Without this
   nothing else in Model 2 delivers user value.
2. **Phase 1** — ai-worker reliability: E1, E2, E3, E5 (crash containment, spool-on-failure).
   Independent of Phase 0's schema but should land before Phase 2's lease work, since a
   supervisor that can restart a camera thread is a prerequisite for clean lease release/renew.
3. **Phase 2** — ai-worker multi-instance: M1, M2, M3, M4 (lease-based assignment, mirroring
   Federation.Worker's proven pattern).
4. **Phase 3** — deployment: D1, D2, D3, D4.
5. **Phase 4** — observability: O1, O2, O3 (useful throughout, but lowest risk to defer since
   nothing above blocks on it).

E4 (RTSP reconnect live-test) and M4 (kill-and-rebalance validation) are verification tasks that
should run once their respective phases land, not standalone code changes.
