# Technical Design --- Integrated Models 1, 2 and 3

> **Scope of this document.** This is the original integration view — the target shape of all
> three models and the phased order to get there. For what is *actually built* and its real API
> surface, see `DATA-FLOW.md` (current implementation) and `ARCHITECTURE-MODEL-3.md`
> (authoritative Model 3 design). Where this document and those disagree, those are correct.
> The API group list in §5 and the event JSON in §6 are illustrative, not the deployed contract.

## 1. Architecture Goal

Create a modular platform where CCTV asset information, video-derived
metadata, and heterogeneous VMS integrations are connected through
common APIs and event interfaces.

## 2. Logical Architecture

``` text
                    Users
                      |
          +-----------+-----------+
          | Web / GIS / Operations|
          +-----------+-----------+
                      |
                API Gateway
                      |
     +----------------+----------------+
     |                |                |
 Registry         Analytics        Federation
 Services         Services         Services
     |                |                |
     |           FFmpeg/GPU           |
     |                |                |
     |          AI Models             |
     |                |                |
     +----------------+----------------+
                      |
             Metadata / Event Bus
                      |
       +--------------+---------------+
       |              |               |
   PostgreSQL       Search         Object Store
   (no extensions)  Index           References
```

> PostgreSQL runs with **no extensions today**. Coordinates are plain `DECIMAL(10,7)` with range
> checks; `gen_random_uuid()` is core. PostGIS is reserved for the coverage-gap analysis slice
> (v1.7) as its own decision — see `MODEL-1-API-PLAN.md`.

## 3. Data Flow

### Camera Registration

``` text
Bulk / Manual / API
        |
        v
Validation
        |
        v
Camera Registry
        |
        v
Coverage geometry (trigonometry over DECIMAL lat/long; no PostGIS)
        |
        v
GIS Map / Coverage
```

### Video Analytics

``` text
VMS / Recording
      |
      v
Adapter / Source
      |
      v
FFmpeg
      |
      v
Frame Sampling
      |
      v
GPU Inference
      |
      v
Metadata
      |
      v
Event Bus + Search + DB
```

### Cross-System Correlation

``` text
Adapter A ----\
Adapter B -----+--> Event Bus --> Correlation Engine
Adapter C ----/                         |
                                        v
                              Unified Event / Alert
```

## 4. Suggested Technology Choices

### Backend

A suitable implementation can use a REST API framework such as FastAPI,
Node.js, Java Spring Boot, or .NET depending on the development team's
expertise.

### Database

**PostgreSQL** (no extensions today; PostGIS is reserved for coverage-gap analysis — see
`docs/MODEL-1-API-PLAN.md`) for:

-   Camera registry
-   Departments
-   Locations
-   Camera coverage (sectors computed in the application over plain lat/long)
-   Events
-   Maintenance
-   Geographic queries

### Search

OpenSearch or Elasticsearch can be used for high-volume metadata and
event search.

### Event Bus

Kafka can provide asynchronous metadata/event exchange between adapters,
analytics, correlation, and dashboard services.

### Video

FFmpeg can handle decoding, transcoding, frame extraction, and
compatible stream processing.

### AI

Prototype options:

-   YOLO for object detection
-   ByteTrack / BoT-SORT for tracking
-   ANPR pipeline with plate detection + OCR
-   InsightFace for controlled, authorised face matching use cases
-   Optional person/vehicle Re-ID for cross-camera similarity

### GIS

A web GIS frontend can render camera points, coverage sectors, routes,
and gaps using a map library. The backend serves GeoJSON from plain
`DECIMAL` lat/long (`/api/v1/gis/*`); coverage sectors are computed by
trigonometry in the application. No PostGIS today — it is reserved for
the coverage-gap slice (v1.7).

## 5. API Design

All routes are versioned under `/api/v1`. The surface built today (see `DATA-FLOW.md` for the
per-endpoint detail and the RBAC permission each requires):

``` text
# Auth
/api/v1/auth/login  /auth/token (dev only)  /auth/password

# Model 3 — VMS federation
/api/v1/vms  /vms/{id}  /vms/{id}/state  /vms/{id}/health  /vms/{id}/capabilities
/api/v1/vms/{id}/cameras  /vms/{id}/cameras/{nativeCameraId}/status-history
/api/v1/vms/{id}/credential (PUT)  /vms/{id}/credential/status  /vms/{id}/credential/resolve
/api/v1/vms/{id}/test          # connection tests
/api/v1/overview
/api/v1/events
/api/v1/worker-health  /worker-health/heartbeat

# Model 1 — registry & GIS
/api/v1/cameras  /cameras/bulk-import  /cameras/{id}  /cameras/unreconciled
/api/v1/cameras/{id}/reconcile  /cameras/from-federated
/api/v1/cameras/{id}/health  /cameras/{id}/health/history  /cameras/{id}/maintenance
/api/v1/cameras/{id}/coverage
/api/v1/gis/cameras  /gis/coverage  /gis/gaps

# Model 2 — detections (ingest from ai-worker)
/api/v1/detections
/api/v1/watchlist  /watchlist/alerts  /watchlist/alerts/{id}/acknowledge

# Org / geography hierarchy
/api/v1/organizations  /organizations/{id}/units  /organization-units/{id}/deactivate
/api/v1/geographic-areas  /geographic-areas/{id}/children  /geographic-areas/{id}/ancestors

# RBAC administration
/api/v1/users  /access-groups  /access-groups/{id}/scopes  /access-groups/{id}/members
/api/v1/roles  /api/v1/permissions  /api/v1/api-keys
```

Not built: `/api/v1/search` (person/vehicle search), `/api/v1/audit` (audit-log read API),
a Kafka-fronted `/observations` stream. Audit rows exist (`config_audit`,
`credential_access_log`); there is no read endpoint for them yet.

## 6. Common Event Contract

Conceptual shape. The stored table is `federation.federation_event`; field names there are
`source_vms_id`, `organization_unit_id`, `occurred_at`, and `latitude`/`longitude` (no nested
`location`). The natural dedup key is `(source_vms_id, source_event_id)`.

``` json
{
  "event_id": "EVT-001",
  "source_vms_id": "…",
  "camera_id": "CAM-001",
  "organization_unit_id": "…",
  "timestamp": "2026-08-19T10:32:15Z",
  "event_type": "ANPR_DETECTION",
  "object_reference": "GJ05AB1234",
  "confidence": 0.96,
  "location": {
    "latitude": 21.1702,
    "longitude": 72.8311
  }
}
```

## 7. Security Architecture

-   RBAC for all user functions
-   Department-scoped access where required
-   Secure credential/secret references
-   Encryption in transit
-   Encryption at rest where applicable
-   API authentication and authorisation
-   Audit trails
-   Connector isolation
-   Network segmentation
-   Least-privilege service accounts
-   Configurable data retention

## 8. Production Boundary

This section originally scoped the work as a hackathon prototype demonstrated on controlled
sample feeds. **That boundary has been superseded by a decision of the project owner: Model 3 is
built production-ready from day one.** The items below are therefore work to complete, not
validation to defer — and the table records honestly where each stands today.

| Concern | Status |
|---|---|
| Capacity | Designed for 80,000 cameras; the constraint is VMS instances, not cameras. Validated against the simulator, **not yet against a real estate** |
| Reliability | Lease-based work assignment, circuit breaking, at-least-once delivery with idempotent sinks |
| Cybersecurity | JWT and API-key authentication, group-based RBAC scoped by organization and geography, application-side AES-256-GCM credential sealing, full audit trail. **No mTLS between services yet** |
| Privacy and legal compliance | **Outstanding.** Retention is configurable and enforced, but the periods are a policy decision nobody has yet confirmed against statutory obligation |
| Model accuracy | Model 2 is unbuilt. Cross-camera identity remains probabilistic by design — see below |
| Retention policies | Implemented: partition-based, irreversible, guarded against misconfiguration. The *values* still need a policy owner |
| Disaster recovery | **Outstanding.** Assumes PostgreSQL replication and a tested restore; neither is set up here |
| High availability | API instances are stateless and interchangeable; the worker fleet self-rebalances. The **database is a single point of failure** until replication exists |
| Large-scale network/storage | Sized in `ARCHITECTURE-MODEL-3.md` §2. Not yet exercised at scale |

Two constraints from the original scoping survive unchanged, because they are about honesty
rather than readiness:

- **Cross-camera identity is probabilistic.** A tracker ID is valid only within one camera. Any
  cross-camera association carries a confidence score and must never be surfaced as certainty in
  an API response or a UI string.
- **Coverage is estimated, not measured.** Sectors are derived from azimuth, field of view and
  effective range. Terrain and obstructions are not modelled, so coverage is a planning aid.

Implementation status for Model 3 is in `ARCHITECTURE-MODEL-3.md`; what is deliberately not built
is listed there under Deliberate non-goals.

## 9. Suggested Development Order

Phases 1–2 have a first implementation (`docs/MODEL-1-API-PLAN.md`); Model 3 is in progress
(`ARCHITECTURE-MODEL-3.md`).

### Phase 1 — implemented (first slice)

Model 1 registry and GIS: the `cameras` registry (`v1.6`), CRUD + bulk-import + reconciliation
under `/api/v1/cameras`, and a GeoJSON map source + application-computed coverage sectors under
`/api/v1/gis`. Coverage-gap analysis (`GET /api/v1/gis/gaps`) is a documented 501 until spatial
querying is introduced.

### Phase 2 — implemented (first slice)

Camera health (`/api/v1/cameras/{id}/health`, `/health/history`, manual override) and
maintenance (`/api/v1/cameras/{id}/maintenance`, records driving `maintenance_status`).

### Phase 3

Build Model 2 video-processing pipeline.

### Phase 4

Add ANPR and searchable metadata.

### Phase 5

Add person/vehicle event tracking for the controlled demo.

### Phase 6

Build two VMS adapters for Model 3.

### Phase 7

Add event bus and common event schema.

### Phase 8

Implement cross-system correlation.

### Phase 9

Connect correlation results to GIS and unified alerts.

### Phase 10

Harden security, audit logging, testing, and demonstration workflows.
