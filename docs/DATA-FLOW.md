# Data Flow & Workflow — Current Implementation

This document reflects what is actually built today, not the full three-model target
state. See `CLAUDE.md` for repository status and `docs/ARCHITECTURE-MODEL-3.md` for the
authoritative design. Model 3 (VMS Federation) is the only model with running code under
`src/`; the `ai-worker/` (Model 2's capture + inference component) exists standalone and
is not yet wired to it — its ingest call is written but unverified end-to-end.

---

## 1. System overview

```text
                         +-------------------------+
                         |   Vendor VMS / NVR /     |
                         |   ONVIF camera targets   |
                         +------------+--------------+
                                      |  poll inventory/status, stream events
                                      v
+------------------+       +-------------------------+       +----------------------+
|  ai-worker (Py)  |       | Federation.Adapters      |       |  Federation.Storage  |
|  capture+infer   |       | (Dahua/Hikvision/Onvif/  |       |  Npgsql + Dapper     |
|  (standalone,    |       |  Rtsp/Http, vendor-only) |       |  repositories        |
|  not yet wired)  |       +------------+--------------+       +----------+-----------+
+---------+--------+                    |                                 ^
          |                             v                                 |
          |                  +-------------------------+                  |
          |                  | Federation.Runtime       |                  |
          |                  | TargetWorker per leased  |------------------+
          |                  | ConnectorTarget:         |   batched event writes
          |                  |  - inventory  (~daily)   |   (EventStore, idempotent
          |                  |  - status     (~30s)     |    on source_vms+event_id)
          |                  |  - events     (stream)   |
          |                  | + LeaseManager,          |
          |                  |   TokenBucket, resilience|
          |                  +------------+--------------+
          |                               |
          |     POST /api/v1/detections   |  normalised events (EventEnvelope)
          |     POST /api/v1/worker-      v
          |     health/heartbeat   +-------------------------+
          +----------------------->|   Federation.Api        |
                                    |  minimal API, RBAC +    |
                                    |  department/org scoped, |
                                    |  audited                |
                                    +------------+--------------+
                                                 |
                                                 v
                                    +-------------------------+
                                    | PostgreSQL (db/versions)|
                                    | orgs, sites, VMS/camera |
                                    | registry, events,       |
                                    | detections, watchlists, |
                                    | credentials, RBAC, audit|
                                    +-------------------------+
```

Kafka/OpenSearch (the event bus and search index called for in
`docs/ARCHITECTURE-MODEL-3.md`) are part of the target design but are not present in the
current `src/` tree — today `TargetWorker` writes normalised events directly through
`Federation.Storage.EventStore`, batched (200 events or 2s, whichever first).

---

## 2. Model 3 — VMS Federation workflow (implemented)

1. **Register a target.** An operator calls `POST /api/v1/vms` (`VmsEndpoints`) with a
   `ConnectorTarget` — vendor, host, credential reference, poll/rate-limit settings.
   Credentials are written separately via `PUT /api/v1/vms/{id}/credential` and stored as
   `CredentialReference` pointers, resolved only in memory at connect time
   (`Federation.Storage/Secrets`). The connector worker resolves in-process; Model 2's AI
   worker resolves over HTTP via `GET /api/v1/vms/{id}/credential/resolve` (the one route
   that returns secret material — `credential.resolve`, `DETECTION_WORKER` only, audited).
2. **Connection test (optional, pre-flight).** `POST /api/v1/connection-tests` exercises
   the adapter against the live target without registering a worker, so an operator can
   validate credentials/reachability first; results are polled via
   `GET /api/v1/connection-tests/{id}`.
3. **Lease acquisition.** `LeaseManager` assigns each `ConnectorTarget` to exactly one
   `Federation.Worker` process/host, so an 80k-camera estate's ~500-2,000 targets are
   sharded across workers without double-polling. Losing a lease cancels that target's
   `TargetWorker` immediately.
4. **Per-target polling (`TargetWorker`, one instance per leased target, 50-200 run
   concurrently per worker process):**
   - **Inventory** (~daily): `IVmsAdapter.GetCamerasAsync()` — one call returns the
     target's *entire* camera inventory. Never per-camera.
   - **Status** (~30s): health/recording state for all cameras on the target in one call.
   - **Events** (continuous): adapter streams/polls vendor events, which are mapped onto
     the shared `EventType` taxonomy (`CameraOnline`, `MotionDetected`, `AnprDetection`,
     `TamperDetected`, ... with `VendorSpecific` as an explicit escape hatch that
     preserves the vendor's own label rather than dropping it).
   - Every adapter call goes through `TokenBucket` (politeness) and `ConnectorResilience`
     (Polly retry/circuit-breaking) — adapters themselves implement neither.
5. **Normalisation & envelope.** Each vendor event becomes a `NormalisedEvent`, wrapped in
   a versioned `EventEnvelope` (`SchemaVersion`, `Producer`, `TraceId`, `ProducedAt`) so
   consumers can trace and safely reject incompatible major versions during rolling
   upgrades.
6. **Persistence.** `EventStore` batches and writes envelopes, idempotent on
   `(source_vms, source_event_id)` — transport is at-least-once, so duplicates are
   expected and must no-op.
7. **Read side.** `Federation.Api` serves everything downstream — read-only, RBAC + org/
   department + geography scoped, every mutation and its audit row sharing one
   `UnitOfWork` transaction:
   - `GET /api/v1/events` — query normalised events (`EventEndpoints`).
   - `GET /api/v1/vms`, `/{id}`, `/{id}/cameras`, `/{id}/health`, `/{id}/capabilities`,
     `/{id}/cameras/{nativeCameraId}/status-history`, `/api/v1/overview` — registry/health
     read side (`VmsEndpoints`).
   - `POST /api/v1/detections` — ingest normalised ANPR/vehicle/person detections (from
     Model 2 or the `ai-worker`), matched against watchlists in the same transaction
     (`DetectionEndpoints`).
   - `GET /api/v1/detections` — search ingested detections.
   - `POST /api/v1/watchlists`, `GET .../alerts`, `POST .../alerts/{id}/acknowledge` —
     watchlist management and alert workflow (`WatchlistEndpoints`).
   - `POST /api/v1/worker-health/heartbeat`, `GET /api/v1/worker-health` — connector/
     AI-worker liveness (`WorkerHealthEndpoints`).
   - `GET /api/v1/api-keys`, `POST /api/v1/api-keys`, `DELETE /api/v1/api-keys/{id}` —
     provision, list and revoke the `X-Api-Key` credentials service integrations
     authenticate with; a key acts through one access group exactly as a user does
     (`ApiKeyEndpoints`, gated by `apikey.read` / `apikey.manage`).
   - Org/unit/area/site hierarchy, access groups, roles/permissions, users, auth — the
     RBAC and org-scoping surface everything above is gated by.

---

## 3. Model 2 component — `ai-worker` capture + inference (standalone)

Not yet wired into the `.NET` event/metadata layer as a running pipeline; it discovers
cameras from `Federation.Api` and has code written against the ingest endpoint, but that
leg is unverified end-to-end (needs a provisioned `TRINETRA_API_KEY`).

```text
Federation.Api                    ai-worker (Python)
  GET /api/v1/vms            <---  1. discover VMS targets caller can see
  GET /api/v1/vms/{id}/cameras <-- 2. per target: nativeCameraId, streamReferences
  GET /api/v1/vms/{id}/          <- 2a. per target (once, cached): resolve the RTSP
      credential/resolve              login — credential.resolve, DETECTION_WORKER only
                                        |
                                        v
                              3. camera_source.py -> CameraConfig list
                                        |
                                        v
                              4. capture/*.py (opencv/ffmpeg/gstreamer/deepstream)
                                 pulls the RTSP stream named by streamReferences,
                                 credentials injected from 2a (or RTSP_USERNAME/PASSWORD)
                                        |
                                        v
                              5. pipeline.py: VehicleDetector -> PlateDetector -> OCR
                                 -> plate_normalizer.normalize_plate()
                                        |
                                        v
                              6. DetectionEvent { cameraId, eventType (ANPR_DETECTED /
                                 VEHICLE_DETECTED), timestamp, confidence, attributes,
                                 evidence }
                                        |
                                        v
  POST /api/v1/detections    <---  7. backend_client.py posts the event (watchlist
                                       matching happens inside this call, same txn)
  POST /api/v1/worker-health/
       heartbeat              <---  8. monitoring/heartbeat.py liveness ping
```

Camera discovery is authenticated with `X-Api-Key` (service integration, `vms.read`
scope) rather than a person's bearer login token. Video itself never crosses into
`Federation.Api`/`.Storage` — only the normalised `DetectionEvent` and, optionally, an
evidence snapshot (`evidence.snapshotBase64`) do, matching the "video is the source,
metadata is the product" invariant.

---

## 4. Application-wise data flow (layer by layer)

No frontend exists in this repo yet (no `frontend/`, `client/`, or `web/` project) —
`docs/TECHNICAL-DESIGN.md`'s "User Interfaces" box is still spec only. The layering below
is the request/response path each layer will use once a frontend lands, and the path that
already runs today for the layers that exist (`Federation.Api` down to PostgreSQL, plus
the standalone `ai-worker`).

```text
[ PLANNED ]                  [ IMPLEMENTED ]
 Frontend (GIS/Search/
 Video/Events/Alerts) --https--> Federation.Api --sql--> PostgreSQL
        |  bearer login token          |                      ^
        |  (person)                    | UnitOfWork per        |
        |                              | mutation + audit row   |
        |                              v                        |
        |                    RBAC / org+geography scope         |
        |                    check (Auth/, CallerContext)       |
        |                                                       |
        +-- ai-worker --X-Api-Key--> Federation.Api ------------+
        |   (service integration,      (POST /detections,
        |    vms.read scope)            worker-health/heartbeat)
        |
        +-- (no direct path) --------> Federation.Worker
                                         (TargetWorker fleet)
                                             |  IVmsAdapter calls
                                             v
                                        Federation.Adapters
                                             |  vendor protocol
                                             v
                                        Vendor VMS / NVR / ONVIF
```

Layer-by-layer:

1. **Frontend → Federation.Api** *(planned)*. A person authenticates via
   `POST /api/v1/auth/login` and gets a bearer token; every subsequent call carries it.
   The frontend never talks to `Federation.Worker`, `Federation.Adapters`, or PostgreSQL
   directly — `Federation.Api` is the only ingress, and it holds no vendor-specific code
   (non-negotiable #2 in `CLAUDE.md`). GIS/coverage screens, search, event/alert views,
   and admin (users, access groups, VMS registration, credentials) all map onto the
   endpoint groups in `Endpoints/`.
2. **ai-worker → Federation.Api** *(implemented, unverified end-to-end)*. A separate
   trust path from the frontend's: a service integration authenticated with `X-Api-Key`
   through the `DETECTION_WORKER` role rather than a person's login token. Discovery
   (`GET /api/v1/vms`, `GET /api/v1/vms/{id}/cameras`, read-only, already live);
   `GET /api/v1/vms/{id}/credential/resolve` to obtain a camera's RTSP login
   (`credential.resolve`, added in `db/versions/v1.5.sql`); and Phase 2 write-back
   (`POST /api/v1/detections`, `POST /api/v1/worker-health/heartbeat`, endpoints exist but
   haven't been exercised against a real deployed worker yet). `scripts/check-detection-api-key.sql`
   audits and repairs an existing worker key against these grants.
3. **Federation.Api → Federation.Storage → PostgreSQL** *(implemented)*. No SQL lives in
   `Federation.Api` itself — enforced by `BannedSymbols.txt`, not just review. Every
   query lives in `Federation.Storage`, scoped by a required `CallerContext`; every
   mutation and its audit row commit through one `UnitOfWork`.
4. **Federation.Worker → Federation.Adapters → vendor devices** *(implemented, no
   frontend-facing entry point)*. This is the connector fleet described in §2 above —
   it runs independently of API traffic, driven by `LeaseManager` assignment, and only
   *feeds* `Federation.Api`'s read side indirectly by writing through
   `Federation.Storage.EventStore`. A frontend never calls a `Federation.Worker` process;
   it only ever reads the events/health that worker produced, via `Federation.Api`.
5. **Federation.Api → Federation.Worker** *(implemented, control plane)*. The one place
   the API layer reaches toward the worker fleet's *effect* rather than its data: VMS
   registration (`POST /api/v1/vms`), credential writes, and connection tests
   (`POST /api/v1/connection-tests`) change rows that `LeaseManager`/`TargetWorker` read
   on their own polling cadence — there is no synchronous API-to-worker call; the worker
   picks up target/credential changes the next time it polls its lease and target state.

## 5. What is spec-only (not in this diagram's "implemented" scope)

- **Model 1 (Registry & GIS)** — camera registry beyond what Model 3 needs for its own
  `cameras` rows, coverage-sector geometry, GIS plotting. Spec only:
  `docs/MODEL-1-REGISTRY-GIS.md`.
- **Kafka event bus / OpenSearch search index** — `docs/ARCHITECTURE-MODEL-3.md` names
  these as the eventual seam between producers (adapters, `ai-worker`) and consumers
  (correlation, search, alerting, GIS); today `EventStore` and `POST /api/v1/detections`
  play that role directly against PostgreSQL.
- **Cross-camera correlation / Re-ID** — must carry a confidence score and never be
  presented as certainty once built; not present in the current endpoint surface.
