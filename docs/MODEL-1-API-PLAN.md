# Model 1 — Centralised CCTV Registry & GIS Mapping — API & Data Plan

Status: proposal for review. No code exists yet. Implements `docs/MODEL-1-REGISTRY-GIS.md`
against the entity models in `docs/CAMERA-SCHEMA.md`, `docs/GEOGRAPHY-SCHEMA.md`,
`docs/DEPARTMENT-SCHEMA.md` and the authorization model in `docs/AUTHORIZATION.md` /
`docs/RBAC-LOGICAL-FLOW.md`.

This document is the contract. It is written to be handed to a .NET reviewer and then
implemented directly, so the endpoint shapes and the SQL are meant to be precise, not
illustrative.

---

## 0. How Model 1 relates to what already exists

| Concept | Owner | Table | Meaning |
|---|---|---|---|
| `federation.federated_camera` | Model 3 | `(target_id, native_camera_id)` PK | **What a VMS currently reports.** An observation, refreshed by the inventory poll. Never authoritative. `camera_id` is `NULL` until reconciled. |
| `federation.cameras` (**new, v1.6**) | Model 1 | `id` UUID PK | **The authoritative registry record.** Created manually / by bulk import / by API. Carries ownership, location, optics, lifecycle. |

Model 3 already ships:

- permissions `camera.read`, `camera.create`, `camera.update`, `camera.health.read`,
  `camera.maintenance.read`, `camera.maintenance.update`, `gis.read`, `gis.coverage.read`
  (`db/versions/v1.sql` lines 490–523).
- roles `CAMERA_OPERATOR`, `INVESTIGATOR`, `MAINTENANCE_OPERATOR`, `ANALYST` plus
  `STATE_ADMIN` / `DEPARTMENT_ADMIN` grants (lines 554–626).
- `federated_camera.camera_id` column and `ix_camera_unreconciled` partial index, already
  waiting for this work (lines 963, 993).
- the scope SQL surface: `has_permission(...)`, `authorized_org_units(...)`,
  `authorized_geographic_areas(...)`, `has_unscoped_permission(...)`,
  `has_unscoped_geography(...)`, `geographic_area_descendants(...)`,
  `org_unit_descendants(...)`.
- `config_audit` (partitioned) and `UnitOfWork.AuditAsync`.

### Missing permissions — must be added in v1.6

| Permission | Why | Granted to |
|---|---|---|
| `camera.delete` | Retire / soft-delete is a distinct, more sensitive action than `camera.update`. `connector_target` reuses `vms.update` for delete, but a camera retire has downstream effect on observations and coverage, so it gets its own grant. | `STATE_ADMIN`, `DEPARTMENT_ADMIN` |
| `camera.reconcile` | Linking a registry UUID to a VMS-native id is neither "create a camera" nor "edit a VMS". It is its own operation with its own audit trail. | `STATE_ADMIN`, `DEPARTMENT_ADMIN`, `VMS_ADMIN` |
| `camera.import` | Bulk import can create thousands of rows in one call; gate it separately from single `camera.create` so it can be delegated narrowly (or withheld). | `STATE_ADMIN`, `DEPARTMENT_ADMIN` |

`gis.read` / `gis.coverage.read` already exist and are sufficient for the GIS endpoints.
`ANALYST` already has both; `CAMERA_OPERATOR`, `INVESTIGATOR`, `MAINTENANCE_OPERATOR` have
`gis.read` only — matching "coverage/gap analysis is an analyst function".

Open question **Q1**: should `CAMERA_OPERATOR` gain `camera.create` / `camera.update`? Today
only the two admin roles have them. Manual onboarding by an operator is a stated core feature
("Manual camera onboarding"). Recommendation: add `camera.create`, `camera.update`,
`camera.maintenance.*` to `CAMERA_OPERATOR` is **not** done — instead keep onboarding an admin
action and revisit. Flagged for the reviewer.

---

## 1. Scope & non-goals for slice 1

### In slice 1 (ships now)

1. `cameras` table (v1.6) + full CRUD: manual create, get, list (paginated, filtered),
   `PUT` replace, `PATCH` partial, soft-delete/retire.
2. Bulk import (`POST /api/v1/cameras/bulk-import`) — synchronous, capped batch size,
   per-row result report.
3. Health: current status read (`GET .../health`), health history read
   (`GET .../health/history`) backed by `camera_health_history` (v1.6). **No** health
   *writer* in Model 1 in slice 1 — see non-goals.
4. Maintenance: list (`GET .../maintenance`), create a maintenance record
   (`POST .../maintenance`), which is the mechanism that moves `maintenance_status` to
   `UNDER_MAINTENANCE` / back to `NORMAL`.
5. Reconciliation: `POST /api/v1/cameras/{id}/reconcile` and
   `GET /api/v1/cameras/unreconciled` (the backlog view over `federated_camera`).
6. GIS camera feed: `GET /api/v1/gis/cameras` — bbox-filtered GeoJSON `FeatureCollection`,
   point geometry + direction bearing + derived coverage-sector polygon **computed in the
   application** from `azimuth` + `horizontal_fov` + `effective_range`. No stored geometry.
7. Per-camera coverage: `GET /api/v1/cameras/{id}/coverage` — the single sector polygon as
   GeoJSON.
8. Coverage aggregate: `GET /api/v1/gis/coverage` — **counts and layer summaries only**
   (cameras per status / department / vendor within a bbox or area), not a merged coverage
   polygon.

### Non-goals for slice 1 (deferred to slice 2)

| Deferred | Reason |
|---|---|
| **Coverage-gap analysis** (`GET /api/v1/gis/gaps`) | Requires a real spatial engine: union of all sector polygons, subtract from an area-of-interest polygon, return the residual. That needs PostGIS **and** administrative boundary polygons on `geographic_areas` (which `GEOGRAPHY-SCHEMA.md` explicitly does not carry yet). Heaviest item; no correct cheap version. Ship the endpoint as `501 Not Implemented` documented, or omit. |
| **Stored / merged coverage polygons, overlap analysis, blind-spot detection** | Same reason. Merged-geometry queries at 80k cameras need `geometry(Polygon,4326)` + GiST index + PostGIS `ST_Union`/`ST_Difference`. |
| **PostGIS extension itself** | `CLAUDE.md` removed PostGIS deliberately and reserved spatial work for "Model 1's coverage work … added then, against a requirement". Slice 1's coverage is pure trigonometry over `DECIMAL` lat/long and needs no extension. Introduce PostGIS in the v1.7 slice that does gap analysis, as its own reviewed decision (**Q2**). |
| **Model 1 health *writer*** (heartbeat ingest, `last_seen_at` updates) | In production, health comes from Model 3's poll (`federated_camera.health`, `camera_status_history`) and from Model 2's worker. Wiring a Model 1 health-projection consumer off the bus is a separate slice. Slice 1 exposes `PATCH /api/v1/cameras/{id}/health` as a **manual operator override only** (mark failed / clear), audited — enough for the demo step "Mark selected cameras as failed". |
| **Async / job-based bulk import**, CSV upload parsing | Slice 1 takes a JSON array, synchronous, max 500 rows. CSV → JSON is a client concern. |
| **Vendor-layer / department-layer pre-rendered tile endpoints** | The GeoJSON feed carries `organizationUnitId` / `vendorId` / `operationalStatus` per feature; the client filters into layers. Server-side vector tiles are a later optimisation. |
| **Camera credential write/resolve** | Already owned by Model 3 (`PUT /vms/{id}/credential`, `GET /vms/{id}/credential/resolve`). Model 1 stores only `credential_reference` as an opaque string and never resolves it. |

### Recommendation on the two heavy items

- **Coverage-sector polygon rendering: IN slice 1**, but computed on the fly in C#
  (a circular-sector approximated by N vertices), returned as GeoJSON. Cost is trivial
  (`O(cameras in bbox)`), no schema, no extension. This covers demo steps 4–5.
- **Coverage-gap analysis: NOT in slice 1.** It is the only feature that genuinely needs
  PostGIS + boundary polygons. Ship `GET /api/v1/gis/gaps` returning `501` with a
  `Problem` body pointing at the slice-2 tracking issue, so the route exists in OpenAPI and
  the contract is stable.

---

## 2. Endpoint catalogue

Conventions (from `HierarchyEndpoints.cs` / `VmsEndpoints.cs`):

- New file `src/Trinetra.Federation.Api/Endpoints/CameraEndpoints.cs` and
  `GisEndpoints.cs`.
- `app.MapGroup("/api/v1/cameras").WithTags(ApiTags.Cameras).RequireAuthorization()` and
  `.MapGroup("/api/v1/gis").WithTags(ApiTags.Gis)`. Add `Cameras` and `Gis` to `ApiTags`.
- Every route `.RequirePermission("…")` in metadata **and** `caller.Require("…")` /
  a scoped repository method (defence in depth, per `AUTHORIZATION.md` §3).
- `Results<>` union return types. Out-of-scope reads → `404`, never `403`
  (`AUTHORIZATION.md` §5).
- All SQL in `Federation.Storage` (`CLAUDE.md` invariant 9). New repositories:
  `CameraRepository`, `CameraHealthRepository`, `MaintenanceRepository`,
  `GisQueryRepository`, `ReconciliationRepository`.
- Mutations go through `UnitOfWork`; the mutation and its `config_audit` row share one
  transaction (`CLAUDE.md` invariant 10).
- `DateTimeOffset` everywhere (invariant 6).

### Pagination shape

`EventPage` uses keyset (`NextCursor`) because that table is billions of rows. `cameras`
is bounded (~80k). Use **keyset pagination on `(camera_code)`** anyway for consistency and
to stay cheap:

```
GET /api/v1/cameras?limit=100&cursor=<opaque>
→ 200  { "items": [ CameraResponse, … ], "nextCursor": "…" | null }
```

`cursor` is a base64 of the last `camera_code` seen. `limit` default 50, max 200 (clamp,
do not error — matches `CameraStatusHistoryAsync`). New contract:

```csharp
public sealed record CameraPage(IReadOnlyList<CameraResponse> Items, string? NextCursor);
```

### 2.1 Registry CRUD

| # | Method & path | Purpose | Permission | Request | Response | Codes |
|---|---|---|---|---|---|---|
| 1 | `POST /api/v1/cameras` | Manual onboarding. Creates one registry record. | `camera.create` | `CameraWriteRequest` (body) | `201` + `CreatedResponse { id }`, `Location: /api/v1/cameras/{id}` | `201`, `400` (validation), `404` (org unit or site not in caller scope), `409` (`camera_code` exists) |
| 2 | `GET /api/v1/cameras/{id:guid}` | Read one registry record. | `camera.read` | — | `CameraResponse` | `200`, `404` (absent or out of scope) |
| 3 | `GET /api/v1/cameras` | Paginated, filtered list. | `camera.read` | query params below | `CameraPage` | `200`, `400` (bad bbox / filter) |
| 4 | `PUT /api/v1/cameras/{id:guid}` | Full replace. Every field taken from body; omitted optional field reverts to null/default. Read-then-send pattern, like `PUT /vms/{id}`. | `camera.update` | `CameraWriteRequest` | `CameraResponse` (`200`) | `200`, `400`, `404`, `409` (code collision with another row) |
| 5 | `PATCH /api/v1/cameras/{id:guid}` | Partial update. Only supplied fields change. JSON merge semantics — a field absent from the body is untouched; an explicit `null` clears a nullable field. | `camera.update` | `CameraPatchRequest` (all-nullable) | `CameraResponse` (`200`) | `200`, `400`, `404`, `409` |
| 6 | `DELETE /api/v1/cameras/{id:guid}` | **Soft delete / retire.** Sets `maintenance_status = 'RETIRED'`, `deleted_at = now()`, `deleted_by`. Row stays for historical observations (`CAMERA-SCHEMA.md`: "Deactivation/soft deletion should normally be preferred where historical observations exist"). | `camera.delete` | optional `?reason=` | `204` | `204`, `404`, `409` (already retired — idempotent choice: return `204`; see Q3) |

**PUT vs PATCH decision.** Both are offered, matching `CAMERA-SCHEMA.md`'s API examples
which list both. `PUT` is the full-replace used by an "edit camera" form that loaded the
whole record; `PATCH` is for targeted changes (e.g. a script correcting azimuth on 200
cameras, or the GIS UI dragging a pin → `PATCH { latitude, longitude }`). `PATCH` is the
one the reconciliation and bulk flows conceptually resemble. Hard delete is **not**
offered.

#### `GET /api/v1/cameras` query parameters

| Param | Type | Notes |
|---|---|---|
| `limit` | int | default 50, clamp 1..200 |
| `cursor` | string | opaque keyset cursor |
| `organizationUnitId` | guid | filter to this unit **and its descendants** (`org_unit_descendants`) |
| `siteId` | guid | exact site |
| `geographicAreaId` | guid | this area **and descendants** (`geographic_area_descendants`), resolved via the camera's site |
| `vendorId` | guid | exact |
| `cameraType` | string | `FIXED`/`PTZ`/`DOME`/`BULLET`/`ANPR`/… |
| `operationalStatus` | string | `ONLINE`/`OFFLINE`/`DEGRADED`/`UNKNOWN` |
| `connectivityStatus` | string | `CONNECTED`/`DISCONNECTED`/`UNKNOWN` |
| `maintenanceStatus` | string | `NORMAL`/`REQUIRED`/`UNDER_MAINTENANCE`/`RETIRED` |
| `bbox` | string | `minLon,minLat,maxLon,maxLat` — inclusive rectangle on `latitude`/`longitude` |
| `q` | string | ILIKE prefix match on `camera_code` and `name` |
| `includeRetired` | bool | default `false` — retired rows hidden unless asked |

Multiple filters AND together. All list results are additionally constrained by the
caller's org-AND-geo scope (§5).

### 2.2 Bulk import

| # | Method & path | Purpose | Permission | Request | Response | Codes |
|---|---|---|---|---|---|---|
| 7 | `POST /api/v1/cameras/bulk-import` | Create/insert many registry records in one call. Synchronous. | `camera.import` | `BulkImportRequest { mode: "insert" \| "upsert", items: CameraWriteRequest[] }` — `items` 1..500 | `207`-style body `BulkImportResult { created: int, updated: int, failed: int, rows: BulkRowResult[] }` where `BulkRowResult { index, cameraCode, status: "created"\|"updated"\|"error", cameraId?, error? }` | `200` (report, even with partial failures), `400` (>500 rows, malformed envelope), `403` |

- `mode: "insert"` — a row whose `camera_code` exists → that row fails with
  `error: "duplicate camera_code"`, others proceed.
- `mode: "upsert"` — existing `camera_code` is updated (same rules as `PUT`).
- **One transaction per row**, not per batch: a bad row must not roll back 499 good ones.
  Each successful row writes its own `config_audit` entry (`action: "create"` /
  `"update"`, `entity_type: "camera"`).
- Every row is scope-checked individually (its `organization_unit_id` + its site's area);
  a row outside caller scope fails with `error: "organization_unit or site not in scope"`.
- `409` is not used — partial success is normal for bulk, so the HTTP status is `200` and
  the body carries per-row outcomes.

### 2.3 Health

| # | Method & path | Purpose | Permission | Request | Response | Codes |
|---|---|---|---|---|---|---|
| 8 | `GET /api/v1/cameras/{id:guid}/health` | Current health snapshot: `operationalStatus`, `connectivityStatus`, `maintenanceStatus`, `lastSeenAt`, `lastHealthCheckAt`, plus the newest `failure_reason` if any. | `camera.health.read` | — | `CameraHealthResponse` | `200`, `404` |
| 9 | `GET /api/v1/cameras/{id:guid}/health/history` | Time-ordered health checks, newest first. | `camera.health.read` | `from` / `to` (`DateTimeOffset`, default last 30 days, max window 90 days), `limit` (default 200, max 1000) | `CameraHealthHistoryResponse { cameraId, from, to, items: CameraHealthCheckResponse[] }` | `200`, `400` (bad range), `404` |
| 10 | `PATCH /api/v1/cameras/{id:guid}/health` | **Manual operator override.** Sets `operational_status` / `connectivity_status` and writes a `camera_health_history` row with `source = 'MANUAL'`. Demo step "mark cameras as failed". | `camera.update` | `HealthOverrideRequest { operationalStatus?, connectivityStatus?, reason }` (`reason` required) | `CameraHealthResponse` | `200`, `400`, `404` |

Note the path `.../health/history` rather than a query flag on `.../health`: keeps the
snapshot response small and cache-friendly, mirrors `VmsEndpoints` `/health` vs
`/cameras/{n}/status-history` split.

### 2.4 Maintenance

| # | Method & path | Purpose | Permission | Request | Response | Codes |
|---|---|---|---|---|---|---|
| 11 | `GET /api/v1/cameras/{id:guid}/maintenance` | List maintenance records for a camera, newest first. | `camera.maintenance.read` | `limit` (default 100, max 500), `status` filter (`OPEN`/`IN_PROGRESS`/`COMPLETED`/`CANCELLED`) | `IReadOnlyList<MaintenanceRecordResponse>` | `200`, `404` |
| 12 | `POST /api/v1/cameras/{id:guid}/maintenance` | Open a maintenance record. If `status` is `IN_PROGRESS`, camera `maintenance_status → UNDER_MAINTENANCE` in the same transaction. | `camera.maintenance.update` | `MaintenanceCreateRequest { maintenanceType, status, description, reportedAt?, startedAt?, performedBy? }` | `201` + `CreatedResponse { id }`, `Location: /api/v1/cameras/{id}/maintenance/{recordId}` | `201`, `400`, `404` |
| 13 | `PATCH /api/v1/cameras/{id:guid}/maintenance/{recordId:guid}` | Update a record: progress it, complete it, cancel it. Completing (`status → COMPLETED`) sets `completed_at` and moves camera `maintenance_status → NORMAL` **unless another record for that camera is still open** (checked in-transaction). | `camera.maintenance.update` | `MaintenanceUpdateRequest { status?, description?, startedAt?, completedAt?, performedBy? }` | `MaintenanceRecordResponse` | `200`, `400`, `404`, `409` (illegal transition, e.g. `COMPLETED → OPEN`) |

`maintenance_status = 'REQUIRED'` is set by `PATCH /api/v1/cameras/{id}` explicitly, or by
a future health rule; slice 1 has no automatic setter.

### 2.5 Reconciliation

| # | Method & path | Purpose | Permission | Request | Response | Codes |
|---|---|---|---|---|---|---|
| 14 | `GET /api/v1/cameras/unreconciled` | The backlog: `federated_camera` rows with `camera_id IS NULL`, i.e. cameras a VMS reports that the registry has never matched. Uses `ix_camera_unreconciled`. | `camera.reconcile` | `targetId?` (guid), `limit` (default 100, max 500), `cursor` | `UnreconciledPage { items: UnreconciledCameraResponse[], nextCursor }` where each item = `{ targetId, nativeCameraId, name, vendorModel, firmware, organizationUnitId, siteId, latitude, longitude, lastSeen, streamReferences[] }` | `200` |
| 15 | `POST /api/v1/cameras/{id:guid}/reconcile` | Link an existing registry record to a VMS-native camera: set `federated_camera.camera_id = {id}` for `(targetId, nativeCameraId)`. Optionally copy `vms_id` / `stream_reference` onto the registry row. | `camera.reconcile` | `ReconcileRequest { targetId, nativeCameraId, adoptStreamReference: bool = true, adoptVmsId: bool = true }` | `ReconcileResponse { cameraId, targetId, nativeCameraId, vmsId }` | `200`, `404` (registry camera or federated row not in scope), `409` (that federated row is already linked to a **different** `camera_id`) |
| 16 | `POST /api/v1/cameras/from-federated` | Convenience: create a registry record **from** an unreconciled federated row and link it in one call. Body carries the fields the federated row cannot supply (`cameraCode`, `cameraType`, optics). | `camera.create` + `camera.reconcile` | `CreateFromFederatedRequest { targetId, nativeCameraId, cameraCode, name?, cameraType, azimuth?, horizontalFov?, effectiveRange?, … }` | `201` + `CreatedResponse { id }` | `201`, `400`, `404`, `409` (`camera_code` exists / federated row already linked) |

See §4 for the exact SQL and rules.

### 2.6 GIS

| # | Method & path | Purpose | Permission | Request | Response | Codes |
|---|---|---|---|---|---|---|
| 17 | `GET /api/v1/gis/cameras` | Map source. GeoJSON `FeatureCollection`, one `Feature` per in-scope camera: `Point` geometry, properties `{ cameraId, cameraCode, name, organizationUnitId, vendorId, cameraType, operationalStatus, connectivityStatus, maintenanceStatus, azimuth, horizontalFov, effectiveRange, hasCoverage }`. | `gis.read` | `bbox` (**required**, `minLon,minLat,maxLon,maxLat`), `organizationUnitId?`, `vendorId?`, `operationalStatus?`, `maintenanceStatus?`, `includeRetired=false`, `includeSectors=false` (when `true`, each feature gets a `coverageSector` GeoJSON `Polygon` in properties) | `application/geo+json` FeatureCollection | `200`, `400` (missing/invalid bbox, bbox area over cap) |
| 18 | `GET /api/v1/cameras/{id:guid}/coverage` | The single derived coverage sector for one camera as a GeoJSON `Feature` (`Polygon`), plus scalar echo of the inputs and a `estimated: true` flag. `204` if the camera has insufficient optics (`azimuth`/`horizontal_fov`/`effective_range` any null). | `gis.coverage.read` | — | GeoJSON `Feature` | `200`, `204`, `404` |
| 19 | `GET /api/v1/gis/coverage` | Aggregate summary over an area or bbox: camera counts by `operationalStatus`, by `maintenanceStatus`, by `organizationUnitId`, by `vendorId`, plus `withCoverageParams` / `withoutCoverageParams` counts and total `effectiveRange` histogram buckets. **No geometry.** | `gis.coverage.read` | one of `geographicAreaId` or `bbox` (required), `organizationUnitId?` | `CoverageSummaryResponse` | `200`, `400` |
| 20 | `GET /api/v1/gis/gaps` | Coverage-gap layer. **Slice 1: not implemented.** | `gis.coverage.read` | `geographicAreaId` (required) | `501` `Problem { title: "Coverage-gap analysis is not available yet", detail: "Planned for the slice that introduces PostGIS and area boundary polygons — see MODEL-1-API-PLAN.md §1." }` | `501` |

**bbox area cap** (endpoint 17): reject a bbox whose span exceeds e.g. 2° × 2° with `400`
("zoom in") so a client cannot pull all 80k cameras as GeoJSON in one call. Tunable
constant `MaxGisBboxDegrees`.

`hasCoverage` in properties = all three of `azimuth`, `horizontal_fov`, `effective_range`
are non-null.

---

## 3. Data model — `db/versions/v1.6.sql`

New version file (latest is v1.5 → this is v1.6). Never edit once applied. Follows the
existing conventions: `SET search_path TO federation, public;`, `CREATE TABLE IF NOT
EXISTS`, `VARCHAR` + `CHECK` for enumerations (no new `CREATE TYPE` — matches `sites`,
`geographic_areas`, and `CAMERA-SCHEMA.md` which lists the status vocabularies as plain
strings), `DECIMAL(10,7)` coordinates with range `CHECK` (matches `sites`,
`federated_camera`, and `CLAUDE.md` "No PostgreSQL extensions").

### 3.1 `cameras`

```sql
CREATE TABLE IF NOT EXISTS cameras (
    id                    UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    camera_code           VARCHAR(100) NOT NULL UNIQUE,
    name                  VARCHAR(255) NOT NULL,

    -- Ownership and location: the two independent dimensions. Both required.
    organization_unit_id  UUID NOT NULL REFERENCES organization_units(id),
    site_id               UUID NOT NULL REFERENCES sites(id),

    vendor_id             UUID,                 -- no vendors table yet; see Q4. Plain UUID for now.
    manufacturer          VARCHAR(255),
    model                 VARCHAR(255),
    camera_type           VARCHAR(50) NOT NULL
                          CHECK (camera_type IN
                              ('FIXED','PTZ','DOME','BULLET','ANPR','THERMAL','MULTISENSOR','OTHER')),
    serial_number         VARCHAR(255),

    -- Geometry decision: authoritative position is a plain lat/long pair, identical to
    -- sites and federated_camera. NO geometry(Point,4326), NO PostGIS in v1.6 — coverage
    -- in slice 1 is computed in the application and no query filters spatially beyond a
    -- bounding-box comparison, which a plain btree on (latitude, longitude) serves.
    latitude              DECIMAL(10,7) NOT NULL CHECK (latitude  BETWEEN -90  AND 90),
    longitude             DECIMAL(10,7) NOT NULL CHECK (longitude BETWEEN -180 AND 180),
    altitude              DECIMAL(10,3),
    mounting_height       DECIMAL(10,3) CHECK (mounting_height >= 0),

    -- Units: degrees for azimuth/tilt/FOV, metres for range/height/altitude. Documented here.
    azimuth               DECIMAL(7,3) CHECK (azimuth        >= 0   AND azimuth        <  360),
    tilt                  DECIMAL(7,3) CHECK (tilt           >= -90 AND tilt           <= 90),
    horizontal_fov        DECIMAL(7,3) CHECK (horizontal_fov >  0   AND horizontal_fov <= 360),
    vertical_fov          DECIMAL(7,3) CHECK (vertical_fov   >  0   AND vertical_fov   <= 180),
    effective_range       DECIMAL(10,3) CHECK (effective_range > 0 AND effective_range <= 5000),

    ip_address            INET,
    port                  INTEGER CHECK (port BETWEEN 1 AND 65535),
    protocol              VARCHAR(50)
                          CHECK (protocol IS NULL OR protocol IN
                              ('RTSP','RTSPS','ONVIF','HTTP','HTTPS','RTMP','SRT','OTHER')),

    vms_id                UUID,                 -- Model 3 connector_target.id; no FK (loose coupling, Q5)
    stream_reference      VARCHAR(512),
    credential_reference  VARCHAR(255),         -- opaque; Model 1 never resolves it

    installation_date     DATE,

    operational_status    VARCHAR(30) NOT NULL DEFAULT 'UNKNOWN'
                          CHECK (operational_status IN ('ONLINE','OFFLINE','DEGRADED','UNKNOWN')),
    connectivity_status   VARCHAR(30) NOT NULL DEFAULT 'UNKNOWN'
                          CHECK (connectivity_status IN ('CONNECTED','DISCONNECTED','UNKNOWN')),
    maintenance_status    VARCHAR(30) NOT NULL DEFAULT 'NORMAL'
                          CHECK (maintenance_status IN
                              ('NORMAL','REQUIRED','UNDER_MAINTENANCE','RETIRED')),

    last_seen_at          TIMESTAMPTZ,
    last_health_check_at  TIMESTAMPTZ,

    deleted_at            TIMESTAMPTZ,          -- soft delete / retire
    deleted_by            UUID,

    created_at            TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at            TIMESTAMPTZ NOT NULL DEFAULT now(),
    created_by            UUID NOT NULL,
    updated_by            UUID NOT NULL,

    -- A retired camera must carry its retirement stamp, and vice versa.
    CONSTRAINT ck_cameras_retire_consistent CHECK (
        (maintenance_status = 'RETIRED') = (deleted_at IS NOT NULL)
    )
);

CREATE INDEX IF NOT EXISTS ix_cameras_org        ON cameras (organization_unit_id);
CREATE INDEX IF NOT EXISTS ix_cameras_site       ON cameras (site_id);
CREATE INDEX IF NOT EXISTS ix_cameras_vendor     ON cameras (vendor_id) WHERE vendor_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS ix_cameras_type       ON cameras (camera_type);
CREATE INDEX IF NOT EXISTS ix_cameras_op_status  ON cameras (operational_status);
CREATE INDEX IF NOT EXISTS ix_cameras_maint      ON cameras (maintenance_status);
CREATE INDEX IF NOT EXISTS ix_cameras_bbox       ON cameras (latitude, longitude)
    WHERE deleted_at IS NULL;
CREATE INDEX IF NOT EXISTS ix_cameras_code_live  ON cameras (camera_code)
    WHERE deleted_at IS NULL;                          -- keyset list ordering
```

`updated_at` maintenance: add a `BEFORE UPDATE` trigger `set_updated_at()` matching the
pattern used elsewhere, or set it in the repository `UPDATE`. (Check whether v1.sql already
defines a generic `touch_updated_at` trigger fn and reuse it — **Q6**.)

### 3.2 `camera_health_history`

Not partitioned. Unlike Model 3's `camera_status_history` (230M rows/day at 80k cameras,
so day-partitioned and transition-only), Model 1's health history is written only by:
manual overrides, and (later) a bus consumer that records *transitions*, not polls. Volume
is low. Revisit partitioning in slice 2 if a per-poll writer is added (**Q7**).

```sql
CREATE TABLE IF NOT EXISTS camera_health_history (
    id                  BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    camera_id           UUID NOT NULL REFERENCES cameras(id) ON DELETE CASCADE,

    operational_status  VARCHAR(30) NOT NULL
                        CHECK (operational_status IN ('ONLINE','OFFLINE','DEGRADED','UNKNOWN')),
    connectivity_status VARCHAR(30) NOT NULL
                        CHECK (connectivity_status IN ('CONNECTED','DISCONNECTED','UNKNOWN')),

    checked_at          TIMESTAMPTZ NOT NULL DEFAULT now(),
    latency_ms          INTEGER CHECK (latency_ms >= 0),
    error_code          VARCHAR(100),
    failure_reason      TEXT,
    source              VARCHAR(20) NOT NULL DEFAULT 'MANUAL'
                        CHECK (source IN ('MANUAL','FEDERATION','AI_WORKER','PROBE')),
    details             JSONB NOT NULL DEFAULT '{}'::jsonb,
    recorded_by         UUID
);

CREATE INDEX IF NOT EXISTS ix_camera_health_hist_camera
    ON camera_health_history (camera_id, checked_at DESC);
```

`GET /api/v1/cameras/{id}/health` returns the current values from `cameras` plus the
newest `camera_health_history.failure_reason`. History endpoint reads this table.

### 3.3 `maintenance_records`

```sql
CREATE TABLE IF NOT EXISTS maintenance_records (
    id                UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    camera_id         UUID NOT NULL REFERENCES cameras(id) ON DELETE CASCADE,

    maintenance_type  VARCHAR(40) NOT NULL
                      CHECK (maintenance_type IN
                          ('PREVENTIVE','CORRECTIVE','INSPECTION','INSTALLATION',
                           'RELOCATION','DECOMMISSION','OTHER')),
    status            VARCHAR(20) NOT NULL DEFAULT 'OPEN'
                      CHECK (status IN ('OPEN','IN_PROGRESS','COMPLETED','CANCELLED')),

    description       TEXT NOT NULL,
    failure_reason   TEXT,

    reported_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    started_at       TIMESTAMPTZ,
    completed_at     TIMESTAMPTZ,
    next_due_at      TIMESTAMPTZ,             -- feeds "ageing infrastructure" / upcoming maintenance

    performed_by     VARCHAR(255),           -- free text: often an external contractor, not a platform user

    created_at       TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at       TIMESTAMPTZ NOT NULL DEFAULT now(),
    created_by       UUID NOT NULL,
    updated_by       UUID NOT NULL,

    CONSTRAINT ck_maint_completed_at CHECK (
        (status = 'COMPLETED') = (completed_at IS NOT NULL)
    ),
    CONSTRAINT ck_maint_time_order CHECK (
        started_at   IS NULL OR started_at   >= reported_at
    )
);

CREATE INDEX IF NOT EXISTS ix_maint_camera  ON maintenance_records (camera_id, reported_at DESC);
CREATE INDEX IF NOT EXISTS ix_maint_open    ON maintenance_records (camera_id)
    WHERE status IN ('OPEN','IN_PROGRESS');
CREATE INDEX IF NOT EXISTS ix_maint_due     ON maintenance_records (next_due_at)
    WHERE status <> 'CANCELLED' AND next_due_at IS NOT NULL;
```

### 3.4 v1.6 permission / role seed

```sql
INSERT INTO permissions (code, name, category, description) VALUES
    ('camera.delete',    'Retire cameras',   'camera', 'Soft-delete / retire camera registry entries'),
    ('camera.import',    'Bulk import cameras','camera','Create cameras in bulk from an import batch'),
    ('camera.reconcile', 'Reconcile cameras','camera', 'Link registry cameras to VMS-discovered cameras')
ON CONFLICT (code) DO NOTHING;

-- role_permissions is keyed by role_id, so join by code — same idiom as v1.sql line 568.
-- SUPER_ADMIN is handled by v1.sql's CROSS JOIN backfill only if v1.sql is re-run; since a
-- version file is never re-applied, v1.6 must also grant the three new permissions to
-- SUPER_ADMIN explicitly.
INSERT INTO role_permissions (role_id, permission_code)
SELECT r.id, p.code FROM roles r JOIN permissions p ON TRUE
WHERE (r.code, p.code) IN (
    ('SUPER_ADMIN','camera.delete'), ('SUPER_ADMIN','camera.import'), ('SUPER_ADMIN','camera.reconcile'),
    ('STATE_ADMIN','camera.delete'), ('STATE_ADMIN','camera.import'), ('STATE_ADMIN','camera.reconcile'),
    ('DEPARTMENT_ADMIN','camera.delete'), ('DEPARTMENT_ADMIN','camera.import'),
    ('DEPARTMENT_ADMIN','camera.reconcile'),
    ('VMS_ADMIN','camera.reconcile')
)
ON CONFLICT DO NOTHING;
```

`permissions` PK is `code`; `role_permissions` is `(role_id, permission_code)` — v1.sql
lines 295–320. The `INSERT … SELECT` above matches the v1.sql line 568 idiom.

### 3.5 Coverage-sector geometry — how it is computed and stored

**Stored: nothing.** No geometry column in v1.6.

**Computed:** on read, in `Federation.Core` (pure, no I/O — a `CoverageSector` helper), from
`latitude, longitude, azimuth, horizontal_fov, effective_range`:

1. If any of azimuth / horizontal_fov / effective_range is null → no sector (`204` /
   `hasCoverage=false`).
2. Sector = apex at the camera point, bisector along `azimuth` (compass bearing, 0 = north,
   clockwise), angular width `horizontal_fov`, radius `effective_range` metres.
3. Approximate the arc with `N` points (`N = max(2, ceil(horizontal_fov / 5))`, cap 72).
4. Project each `(bearing, distance)` to lat/long with the equirectangular / haversine
   destination-point formula (`effective_range` ≤ 5 km, error negligible; document as
   estimate).
5. Emit GeoJSON `Polygon`: `[apex, arcPoint_0, …, arcPoint_N, apex]`, coordinates
   `[lon, lat]`, WGS84.
6. Response carries `"estimated": true` and the disclaimer string from `CLAUDE.md`
   ("planning aid — terrain and obstructions are not modelled").

When PostGIS is introduced (slice 2), the same parameters feed `ST_Project` + `ST_Buffer`
segments or a generated `coverage_geom geometry(Polygon,4326)` column maintained by
trigger, indexed with GiST, for `ST_Union` / `ST_Difference` gap analysis. That is a v1.7
decision (**Q2**), not v1.6.

---

## 4. Reconciliation — detail

`federated_camera` (Model 3) is keyed `(target_id, native_camera_id)` and already has a
nullable `camera_id UUID` with `ix_camera_unreconciled … WHERE camera_id IS NULL`.

### `POST /api/v1/cameras/{id}/reconcile`

`ReconciliationRepository.ReconcileAsync(Guid cameraId, Guid targetId, string nativeCameraId,
bool adoptStreamReference, bool adoptVmsId, CallerContext caller, UnitOfWork work, ct)`:

1. **Scope both sides.** Load the registry camera via `CameraRepository` scoped read
   (org AND geo, §5) — null → `404`. Load the federated row; verify the caller can reach
   its `organization_unit_id` + its site's area via `has_permission('camera.reconcile', …)`
   — fail → `404`.
2. **Conflict check.** If `federated_camera.camera_id IS NOT NULL AND camera_id <> @cameraId`
   → `409` (`"That VMS camera is already reconciled to a different registry record"`).
   If it already equals `@cameraId` → idempotent `200`, no write.
3. **Link.**
   ```sql
   UPDATE federation.federated_camera
      SET camera_id = @cameraId, updated_at = now()
    WHERE target_id = @targetId AND native_camera_id = @nativeCameraId;
   ```
4. **Optional adoption onto the registry row**, in the same transaction:
   - `adoptVmsId` → `cameras.vms_id = @targetId` if `cameras.vms_id IS NULL`.
   - `adoptStreamReference` → `cameras.stream_reference = federated_camera.stream_references[1]`
     if the registry value is null and the array is non-empty.
   - Never copy credentials.
5. **Audit** (one row, same tx): `action: "reconcile"`, `entity_type: "camera"`,
   `entity_id: cameraId`, `before: { vmsId, streamReference, federatedCameraId: null }`,
   `after: { vmsId, streamReference, targetId, nativeCameraId }`,
   `organization_unit_id: cameras.organization_unit_id`.
6. `work.CommitAsync`.

### Unlink

Not a slice-1 endpoint. If needed, `DELETE /api/v1/cameras/{id}/reconcile?targetId=&nativeCameraId=`
setting `camera_id = NULL`, gated `camera.reconcile`, audited. Deferred (**Q8**).

### Relationship to Model 3's poller

The poller upserts `federated_camera` and **must not** clear a non-null `camera_id`
(already stated in `MODEL-1-REGISTRY-GIS.md`: "does not remove a registry record … merely
because one poll returns a partial inventory"). No Model 3 code change is required for
slice 1 — verify the existing upsert in `ConnectorStateStore.cs` (line ~278) preserves
`camera_id` on conflict (it should not be in the `SET` list). **Q9**: confirm.

---

## 5. RBAC — org-scope AND geo-scope per endpoint

Rule (`AUTHORIZATION.md` §2, `CLAUDE.md` invariant 12): organization and geography are
independent dimensions, **ANDed**, tracked per permission, never collapsed to one flag.
For a camera:

- **organization dimension** = `cameras.organization_unit_id`, checked against
  `authorized_org_units(user, key, perm)` / `has_unscoped_permission(user, key, perm)`.
- **geographic dimension** = the area of the camera's site:
  `(SELECT geographic_area_id FROM sites WHERE id = cameras.site_id)`, checked against
  `authorized_geographic_areas(user, key, perm)` / `has_unscoped_geography(user, key, perm)`.

`sites.geographic_area_id` is `NOT NULL`, and `cameras.site_id` is `NOT NULL`, so — unlike
`connector_target` where `site_id` is optional — **every camera always has both
dimensions** and the geo filter is never skipped.

### Single-row reads / writes (`GET/PUT/PATCH/DELETE /cameras/{id}`, health, maintenance, coverage, reconcile)

Repository method issues one predicate (pattern from `ConnectorTargetRepository.GetAsync`):

```sql
SELECT …
FROM federation.cameras c
WHERE c.id = @id
  AND (@UnscopedOrg OR c.organization_unit_id IN (
        SELECT organization_unit_id
        FROM federation.authorized_org_units(
                 p_user_id => @UserId, p_api_key_id => @ApiKeyId, p_permission => @Perm)))
  AND (@UnscopedGeo OR (SELECT s.geographic_area_id FROM federation.sites s WHERE s.id = c.site_id) IN (
        SELECT geographic_area_id
        FROM federation.authorized_geographic_areas(
                 p_user_id => @UserId, p_api_key_id => @ApiKeyId, p_permission => @Perm)));
```

where `@Perm` is the permission the endpoint requires (`camera.read`, `camera.update`,
`camera.health.read`, …), `@UnscopedOrg = caller.IsUnscopedFor(perm)`,
`@UnscopedGeo = caller.IsUnscopedForGeography(perm)` (new `CallerContext` member backed by
`has_unscoped_geography` — **Q10**, confirm `CallerContext` already carries the geo-unscoped
set; `AUTHORIZATION.md` §2 says the `trinetra:unscoped-geo` claim exists, so it likely
does).

No row → `404` (never `403`). For writes, the same predicate gates the `UPDATE … WHERE`;
0 rows affected → `404`.

Equivalent single-resource form using `has_permission` (which ANDs all dimensions
internally) is also acceptable and matches `ConnectorTargetRepository.GetAsync`:

```sql
AND (@Unscoped OR federation.has_permission(
        p_user_id => @UserId, p_api_key_id => @ApiKeyId, p_permission => @Perm,
        p_organization_unit_id => c.organization_unit_id,
        p_geographic_area_id => (SELECT s.geographic_area_id FROM federation.sites s WHERE s.id = c.site_id)))
```

Prefer this single-call form for `{id}` reads; use the two explicit `IN (…)` sets for list
queries (planner-friendly, matches `ConnectorTargetRepository.ListAsync`).

### List / feed reads (`GET /cameras`, `GET /gis/cameras`, `GET /gis/coverage`, `GET /cameras/unreconciled`)

`WHERE` clause ANDs **both** authorized sets (org via `cameras.organization_unit_id`, geo
via the site subquery), each guarded by its own unscoped flag, plus the user filters from
§2.1. `GET /cameras/unreconciled` scopes on `federated_camera.organization_unit_id` and its
site area the same way, with `camera.reconcile` as the permission.

### Create / bulk-import

`camera.create` (or `camera.import`) is checked **and** the target
`organization_unit_id` + the chosen `site_id`'s area must both be within the caller's
authorized sets for that permission — otherwise `404` (single) / per-row error (bulk).
This is the `AUTHORIZATION.md` §5 "a scoped caller cannot create a resource outside their
oversight" rule. A camera has no "root" problem (it always sits under an existing unit and
site), so no special unscoped-only restriction like organizations have.

### Per-endpoint permission summary

| Endpoint | Permission | Org dim source | Geo dim source |
|---|---|---|---|
| `POST /cameras`, bulk-import | `camera.create` / `camera.import` | body `organizationUnitId` | body `siteId` → area |
| `GET /cameras`, `GET /cameras/{id}` | `camera.read` | `cameras.organization_unit_id` | site area |
| `PUT`/`PATCH /cameras/{id}` | `camera.update` | current row | current row's site area (and new site if changed — check both) |
| `DELETE /cameras/{id}` | `camera.delete` | current row | current row's site area |
| `GET .../health`, `.../health/history` | `camera.health.read` | camera | site area |
| `PATCH .../health` | `camera.update` | camera | site area |
| `GET .../maintenance` | `camera.maintenance.read` | camera | site area |
| `POST/PATCH .../maintenance` | `camera.maintenance.update` | camera | site area |
| `GET /cameras/{id}/coverage` | `gis.coverage.read` | camera | site area |
| `GET /gis/cameras` | `gis.read` | camera | site area |
| `GET /gis/coverage` | `gis.coverage.read` | camera | site area |
| `GET /cameras/unreconciled` | `camera.reconcile` | `federated_camera.organization_unit_id` | its site area |
| `POST /cameras/{id}/reconcile`, `/from-federated` | `camera.reconcile` (+ `camera.create` for from-federated) | both the registry camera **and** the federated row | both site areas |

If a PUT/PATCH moves the camera to a different `site_id` or `organization_unit_id`, the
caller must be authorized for **both** the old and the new placement (prevents "push a
camera into a unit I can't see" and "pull one out of view").

---

## 6. Validation rules

Enforced at the API boundary (return `400` `Problem` with a specific `detail`) **and** by
the DB `CHECK` constraints as a backstop.

| Field | Rule |
|---|---|
| `camera_code` | required; `^[A-Za-z0-9][A-Za-z0-9._/-]{1,99}$`; unique among non-deleted rows (`409` on collision). Immutable? — recommend **mutable via PUT/PATCH** but audited; `id` is the immutable identity (`CAMERA-SCHEMA.md`). |
| `name` | required, 1..255, trimmed |
| `organization_unit_id` | required, must exist, status `ACTIVE`, in caller scope |
| `site_id` | required, must exist, status `ACTIVE`, in caller scope |
| `camera_type` | required, one of `FIXED,PTZ,DOME,BULLET,ANPR,THERMAL,MULTISENSOR,OTHER` |
| `latitude` | required, `-90 ≤ lat ≤ 90`, ≤ 7 dp |
| `longitude` | required, `-180 ≤ lon ≤ 180`, ≤ 7 dp |
| coordinate sanity | reject `(0,0)` unless `?allowNullIsland=true` — a common bad default (**Q11**, soft rule, warn not block) |
| `altitude` | optional, `-500 ≤ alt ≤ 9000` (m) |
| `mounting_height` | optional, `0 ≤ h ≤ 200` (m) |
| `azimuth` | optional; degrees; `0 ≤ azimuth < 360`. `360` rejected (normalise client-side to `0`). |
| `tilt` | optional; degrees; `-90 ≤ tilt ≤ 90` |
| `horizontal_fov` | optional; degrees; `0 < hFOV ≤ 360` |
| `vertical_fov` | optional; degrees; `0 < vFOV ≤ 180` |
| `effective_range` | optional; metres; `0 < range ≤ 5000` |
| coverage completeness | not an error: if some-but-not-all of azimuth/hFOV/range are set, accept and set `hasCoverage=false`. Optionally warn. |
| `ip_address` | optional; valid `INET`; not used as identity |
| `port` | optional; `1..65535` |
| `protocol` | optional; enum list above |
| `operational_status` | `ONLINE,OFFLINE,DEGRADED,UNKNOWN`; default `UNKNOWN` on create |
| `connectivity_status` | `CONNECTED,DISCONNECTED,UNKNOWN`; default `UNKNOWN` |
| `maintenance_status` | `NORMAL,REQUIRED,UNDER_MAINTENANCE,RETIRED`; default `NORMAL`; cannot be set to `RETIRED` via PUT/PATCH (use `DELETE`); cannot be set away from `RETIRED` (no un-retire in slice 1 — **Q3**) |
| `installation_date` | optional; not in the future; not before `2000-01-01` (warn) |
| `maintenance_type` | required on create; enum in §3.3 |
| maintenance `status` transitions | `OPEN→IN_PROGRESS→COMPLETED`; any→`CANCELLED`; `COMPLETED`/`CANCELLED` are terminal (`409` otherwise) |
| bulk `items` | 1..500; each item validated as a single create; empty array → `400` |
| `bbox` | exactly 4 comma-separated finite numbers; `minLon<maxLon`, `minLat<maxLat`; within valid coord ranges; span ≤ `MaxGisBboxDegrees` for `/gis/cameras` |
| history `from`/`to` | `to > from`; window ≤ 90 days (health) |

Enum values are **case-sensitive uppercase** on the wire. Reject lowercase with a `400`
listing valid values (pattern: `VmsEndpoints.SetStateAsync`).

---

## 7. Audit — what writes `config_audit`

Via `UnitOfWork.AuditAsync(caller, action, entityType, entityId, before, after,
organizationUnitId, ct)`, in the **same transaction** as the mutation
(`CLAUDE.md` invariant 10). `config_audit` columns: `action`, `entity_type`, `entity_id`,
`organization_unit_id`, `before_state`, `after_state` (JSONB snapshots, never a diff;
never credential material).

| Operation | `action` | `entity_type` | `entity_id` | before / after |
|---|---|---|---|---|
| `POST /cameras` | `create` | `camera` | new id | `null` / full request (credential_reference kept, no secret) |
| `PUT /cameras/{id}` | `update` | `camera` | id | prior row snapshot / new |
| `PATCH /cameras/{id}` | `update` | `camera` | id | prior / patched |
| `DELETE /cameras/{id}` (retire) | `delete` | `camera` | id | prior / `{ maintenanceStatus: RETIRED, deletedAt, reason }` |
| bulk-import, per successful row | `create` or `update` | `camera` | row id | as single create/update |
| `PATCH /cameras/{id}/health` (manual override) | `update` | `camera_health` | camera id | prior status / new status + reason |
| `POST /cameras/{id}/maintenance` | `create` | `maintenance_record` | record id | `null` / record; if it flips camera to `UNDER_MAINTENANCE`, a second `update`/`camera` row for the status change |
| `PATCH /cameras/{id}/maintenance/{recordId}` | `update` | `maintenance_record` | record id | prior / new; plus `update`/`camera` row if `maintenance_status` changes |
| `POST /cameras/{id}/reconcile` | `reconcile` | `camera` | camera id | `{ vmsId, streamReference }` / `{ vmsId, streamReference, targetId, nativeCameraId }` |
| `POST /cameras/from-federated` | `create` + `reconcile` | `camera` | camera id | two rows |
| `DELETE .../reconcile` (if built) | `reconcile` | `camera` | camera id | linked / unlinked |

**Not audited** (pure reads): all `GET` endpoints, `GET /gis/*`, `GET /cameras/{id}/coverage`,
`GET /cameras/unreconciled`. Consistent with Model 3 (reads are not in `config_audit`;
credential *resolution* has its own `credential_access_log`, not relevant here).

`config_audit.actor` / `actor_user_id` / `actor_api_key_id` / `source_address` are filled
by `AuditAsync` from `CallerContext` as they are today.

---

## 8. Open questions / decisions needed before coding

| # | Question | Recommendation |
|---|---|---|
| Q1 | Should `CAMERA_OPERATOR` hold `camera.create` / `camera.update` / `camera.maintenance.*`? Spec lists "manual onboarding" as core, but v1.sql gives create/update to admins only. | Keep onboarding admin-only for slice 1; add a `CAMERA_ONBOARDER`-style grant later if operators need it. Reviewer to confirm. |
| Q2 | When is PostGIS introduced, and by whom? It reverses a `CLAUDE.md` decision. | Its own version (v1.7) with its own design note, coupled to the gap-analysis slice. Not v1.6. |
| Q3 | Is "un-retire" ever allowed? | No in slice 1. `DELETE` is terminal; recreate under a new/same code if needed (code unique among non-deleted, so reuse works). Revisit if operationally painful. |
| Q4 | Is there a `vendors` table? `CAMERA-SCHEMA.md` has `vendor_id UUID` but no schema for it; `connector_target` has `vendor` as an enum, not a table. | Slice 1: `cameras.vendor_id` is a plain `UUID` with no FK, plus free-text `manufacturer`/`model`. Add a `vendors` table + `GET /api/v1/vendors` in a follow-up if vendor-layer filtering needs canonical names. |
| Q5 | FK from `cameras.vms_id` to `connector_target(id)`? | No FK — loose coupling (a registry camera can name a VMS that is later removed; `ON DELETE SET NULL` is the alternative). Recommend no FK, matching `federated_camera.camera_id` having no FK for the mirror reason. |
| Q6 | Does v1.sql define a reusable `updated_at` touch trigger? | Check lines ~130–260; reuse if present, else set `updated_at` in the repository `UPDATE` statements. |
| Q7 | Partition `camera_health_history`? | No in slice 1 (low volume: manual + transition-only). Partition by month in the slice that adds a bus-driven health writer, if volume warrants. |
| Q8 | Ship `DELETE .../reconcile` (unlink)? | Defer. Add when a real mis-reconciliation is hit. |
| Q9 | Does Model 3's `federated_camera` upsert (`ConnectorStateStore.cs`) preserve a non-null `camera_id` on conflict? | Verify before shipping reconcile — if `camera_id` is in the `ON CONFLICT … SET` list, a poll would wipe reconciliations. Expected: it is not. If it is, fix in v1.6 / a Model 3 patch. |
| Q10 | Does `CallerContext` already expose a per-permission geography-unscoped check (`IsUnscopedForGeography`)? `AUTHORIZATION.md` §2 names the `trinetra:unscoped-geo` claim. | If not present, add it alongside `IsUnscopedFor`; both must be per-permission sets, never single flags. |
| Q11 | Block `(0,0)` coordinates? | Warn, don't block (there is a valid point at 0,0). Provide `?strictCoordinates=true` later if bad data appears. |
| Q12 | `GET /api/v1/gis/gaps` — ship as `501` or omit entirely from slice 1? | Ship as documented `501` so the route is in OpenAPI and the client contract is stable. |
| Q13 | GeoJSON media type — return `application/geo+json` or plain `application/json`? | `application/geo+json` for `/gis/cameras` and `/cameras/{id}/coverage`; keep `/gis/coverage` (no geometry) as `application/json`. |
| Q14 | Does `RequirePermission` metadata support the two admin-added permissions without a code change (it reads the `permissions` table at startup for coverage reporting)? | Adding rows to `permissions` in v1.6 is sufficient; confirm the startup coverage check tolerates permissions it has never seen used. |
| Q15 | Bulk-import size cap (500) and sync model — acceptable for the first-phase 100+ camera rollout and the 80k target? | 500/call sync is fine for onboarding waves. An async job endpoint is a later addition, not a slice-1 blocker. |
| Q16 | Should `PATCH /cameras/{id}` allow moving `site_id` / `organization_unit_id` at all, or force a dedicated "transfer" endpoint with its own permission? | Allow via PATCH in slice 1, gated by the dual-scope (old AND new) check in §5. Revisit if transfers need a distinct approval flow. |

---

## 9. Implementation checklist (hand-off)

1. `db/versions/v1.6.sql` — §3 tables, indexes, permission/role seed. New file, not an edit.
2. `Federation.Core` — `Camera`, `CameraHealthCheck`, `MaintenanceRecord` domain records
   (`DateTimeOffset`); `CoverageSector` pure helper (§3.5).
3. `Federation.Storage` — `CameraRepository`, `CameraHealthRepository`,
   `MaintenanceRepository`, `GisQueryRepository`, `ReconciliationRepository`; every scoped
   method takes a required `CallerContext`; all SQL here, none in the API project
   (`BannedSymbols.txt`).
4. `Federation.Api/Contracts` — request/response records (§2), `CameraPage`,
   `BulkImportResult`, GeoJSON DTOs.
5. `Federation.Api/Endpoints/CameraEndpoints.cs`, `GisEndpoints.cs` — `MapGroup`,
   `RequirePermission`, `WithSummary`/`WithDescription`, `Results<>` unions, `404`-not-`403`.
6. `ApiTags` — add `Cameras`, `Gis`.
7. `CallerContext` — add geography-unscoped per-permission accessor if missing (Q10).
8. Tests: scope matrix (in-scope / out-of-org / out-of-geo / unscoped-org-only /
   unscoped-geo-only), `camera_code` uniqueness, coordinate/azimuth/FOV bounds,
   reconcile conflict (`409`), bulk partial-failure report, coverage-sector vertex output,
   audit-row-per-mutation, `PUT` vs `PATCH` semantics.
9. Warnings-as-errors clean; `dotnet list package --vulnerable` clean.

---

## 10. .NET review (dotnet-expert)

Reviewed against the code on disk, not the specs. Verdict at the end.

### 10.1 Endpoint conventions — matches, with two fixes

The plan's endpoint style is faithful to `VmsEndpoints.cs` / `HierarchyEndpoints.cs`:
`MapGroup(...).WithTags(ApiTags.X).RequireAuthorization()`, `.RequirePermission("...")`,
`.WithSummary`/`.WithDescription`, `Results<>` unions returning `TypedResults`,
`CallerContextFactory.From(http)`, and the `UnitOfWork.BeginAsync(db, ct)` → mutate →
`work.AuditAsync(...)` → `work.CommitAsync(ct)` sequence (`VmsEndpoints.cs:303-322`). All SQL
in `Federation.Storage` is enforced by `src/Trinetra.Federation.Api/BannedSymbols.txt`
(bans `Dapper.SqlMapper` / `Dapper.CommandDefinition` in the API project) — the plan respects it.
`CreatedResponse(Guid Id)` already exists (`Contracts/Responses.cs:14`).

Fixes:

- **`ApiTags` has an `Ordered` list (name → description) that drives the Scalar section order and
  descriptions** (`OpenApi/ApiTags.cs`). Adding `Cameras` / `Gis` as bare constants is not enough —
  add them to `Ordered` too (with a description, placed next to `Vms`), or they render last and
  undescribed.
- **Pagination:** the established keyset pattern (`EventQueryRepository.cs:38-98`) passes the
  cursor as *separate typed columns* (`CursorTime`, `CursorId`) in a row-comparison
  `(occurred_at, event_id) < (@CursorTime, @CursorId)`, with base64 encode/decode done in the API
  contract layer. `CameraPage` keyed on `camera_code` alone is fine (single unique non-null
  column), but keep the opaque-cursor encode/decode in the API layer exactly as events does, and
  name the list field to match `EventPage` (which calls it `Events`, not `Items` — cosmetic, your
  call).

**Q14 — CONFIRMED: `RequirePermission` is *not* startup-validated against the `permissions`
table.** `ValidatePermissionCoverage` (`Auth/PermissionEndpoints.cs`) only checks that every
endpoint carrying `IAuthorizeData` also carries `RequiredPermissionMetadata` — it never touches
the database. So (a) adding rows to `permissions` in v1.6 is sufficient and nothing rejects a
permission string it "has never seen used"; (b) conversely, **nothing catches a typo'd
permission string** in `.RequirePermission("camera.recncile")` — it would 403 everyone forever
with a green build. Match the seed codes character-for-character.

### 10.2 RBAC plumbing — one real correctness issue

SQL function signatures verified in `db/versions/v1.sql` (the *final* redefinitions, from the
`DROP FUNCTION` block at line 1481 onward — the earlier copies at 656/768 are superseded):

| Function | Signature | Plan's usage |
|---|---|---|
| `has_permission` | `(p_user_id, p_api_key_id, p_permission, p_organization_unit_id, p_geographic_area_id, p_resource_type, p_resource_id)` (last 4 default NULL) — line 1495 | matches |
| `authorized_org_units` | `(p_user_id, p_api_key_id, p_permission)` — line 1600 | matches |
| `authorized_geographic_areas` | `(p_user_id, p_api_key_id, p_permission)` — line 2029 | matches |
| `has_unscoped_permission` | `(p_user_id, p_api_key_id, p_permission)` — line 1641 | matches |
| `has_unscoped_geography` | `(p_user_id, p_api_key_id, p_permission)` — line 2054 | matches |
| `org_unit_descendants(root)` / `geographic_area_descendants(root)` | single UUID arg — lines 226/241 | matches |

Inside `has_permission`, a group that *constrains* geography but is passed
`p_geographic_area_id IS NULL` yields `geo_ok := FALSE` (deny) — lines 1554-1558. Passing real
ids for both dimensions is therefore always safe.

**The issue:** §5's line "Prefer this single-call form for `{id}` reads … matches
`ConnectorTargetRepository.GetAsync`" is wrong to carry over. That method guards a
*both-dimension* `has_permission` call with a *single* org flag
`@Unscoped = caller.IsUnscopedFor("vms.read")` (`ConnectorTargetRepository.cs:77-86`). A caller
who is **org-unscoped but geography-scoped** takes the `@Unscoped` branch and the row is returned
with the geo dimension never checked — that is exactly the "reuse one dimension's unscoped answer
for the other" failure `CLAUDE.md` invariant 12 forbids. It is a latent bug in the Model 3 code;
`connector_target.site_id` being nullable has hidden it. For cameras, `site_id` is `NOT NULL` and
geo always matters, so **do not replicate that pattern.** Use one of:

- **(preferred) always call `has_permission(... p_organization_unit_id => c.organization_unit_id,
  p_geographic_area_id => (SELECT s.geographic_area_id FROM sites s WHERE s.id = c.site_id))` with
  no unscoped bypass** — a genuinely unscoped admin has no constraining scopes, so
  `has_permission` returns TRUE for them anyway. The bypass is only a micro-optimisation and here
  it is a security hole.
- or the two-flag form §5 already describes for lists (`@UnscopedOrg` + `@UnscopedGeo`, each
  guarding its own `authorized_*` `IN (...)` set).

Delete the "prefer the single-call form" sentence; use the same dual-dimension logic for `{id}`
reads and lists.

**Q10 — the geography-unscoped data is already plumbed end to end, but the accessor is in the
wrong place.** `CallerContext.UnscopedGeography` exists (`CallerContext.cs:62`), is populated
from the `trinetra:unscoped-geo` claim on **both** the JWT path
(`CallerContextFactory.cs:28-30`, `JwtTokenService.cs:31/104`) and the API-key path
(`ApiKeyAuthenticationHandler.cs:86`). But there is **no `CallerContext.IsUnscopedForGeography`
method** — the check currently lives as a private helper `Geo(caller, permission) =>
caller.IsSystem || caller.UnscopedGeography.Contains(permission)` in
`GeographyRepository.cs:289`. Promote that to `CallerContext.IsUnscopedForGeography(string)`
next to `IsUnscopedFor` (line 66), and refactor `GeographyRepository.Geo` to call it. Then the
camera repositories use the pair `caller.IsUnscopedFor(perm)` / `caller.IsUnscopedForGeography(perm)`.

### 10.3 UnitOfWork / audit — signature and columns confirmed

`UnitOfWork.AuditAsync(CallerContext caller, string action, string entityType, string entityId,
object? before, object? after, Guid? organizationUnitId, CancellationToken ct)` —
`UnitOfWork.cs:83-108`. Matches §7 exactly. Note `entityId` is `string`; pass `id.ToString()`
(as `VmsEndpoints` does).

`config_audit` columns (`db/versions/v1.sql:1177-1195`): `action`, `entity_type`, `entity_id`,
`organization_unit_id`, `before_state`, `after_state` — all as §7 states. The timestamp column is
**`changed_at`** (not `created_at`), PK is `(changed_at, id)`, and the table is
`PARTITION BY RANGE (changed_at)` with a `config_audit_default` catch-all partition, so v1.6 adds
**no** partition-maintenance work. `actor` / `actor_user_id` / `actor_api_key_id` /
`source_address` are filled by `AuditAsync` from `CallerContext`. There is no writable audit path
outside `UnitOfWork` (the plan's §2 bullet is correct).

### 10.4 Model 3 coupling — Q9 CONFIRMED SAFE

`ConnectorStateStore.cs:276-303`: the inventory merge COPYs into a `staged_camera` temp table
then `INSERT INTO federation.federated_camera (... camera_id ...) SELECT ... FROM staged_camera
ON CONFLICT (target_id, native_camera_id) DO UPDATE SET name=..., vendor_model=..., firmware=...,
is_enabled=..., is_recording=..., health=..., last_seen=..., stream_references=CASE..., updated_at=now()`.
**`camera_id` is not in the `DO UPDATE SET` list**, so a poll never clears a reconciled link. No
Model 3 change is required for slice 1. (Also not in the SET list: `organization_unit_id`,
`site_id` — a camera that moves org/site in the VMS keeps its original values in `federated_camera`;
out of scope here but worth knowing for the reconcile UI.) The value inserted for `camera_id`
comes from the adapter row (`c.CameraId`), which is always NULL today.

`federated_camera` columns (`db/versions/v1.sql:957-985`): `target_id`, `native_camera_id`,
`camera_id UUID` (nullable, **no FK**), `organization_unit_id UUID NOT NULL`,
`site_id UUID` (**nullable**, FK to `sites`), `name`, `vendor_model`, `firmware`,
`latitude`/`longitude DECIMAL(10,7)`, `is_enabled`, `is_recording`, `health health_status`,
`last_seen`, `stream_references TEXT[] NOT NULL DEFAULT '{}'`, `raw_reference`, `first_seen_at`,
`updated_at`. Indexes: `ix_camera_registry_id` (partial, `camera_id IS NOT NULL`),
`ix_camera_org`, `ix_camera_unreconciled` (partial, `camera_id IS NULL`, on `target_id`).
Two consequences for the plan:

- `federated_camera.site_id` is **nullable**, so `GET /cameras/unreconciled`'s geo dimension
  ("its site area") can be null. Decide: fall back to scoping on `organization_unit_id` only when
  `site_id IS NULL`, or exclude such rows. The plan currently assumes every federated row has a
  site.
- The unreconciled item shape lists `streamReferences[]` — that maps to
  `federated_camera.stream_references` directly. Good. `vendorModel`/`firmware` map to
  `vendor_model`/`firmware`. There is no `vms_id` on `federated_camera`; the VMS is `target_id`.

### 10.5 Schema conventions

- **Version-file header:** v1.1–v1.5 all open with a `====` comment banner, a rationale section,
  and an explicit `-- Requires: v1.sql, v1.1.sql, ...` line, then
  `SET search_path TO federation, public;` and a `-- Transaction is owned by MigrationRunner: do
  not add BEGIN/COMMIT here.` note. v1.6 must match. (`CLAUDE.md` says "no migration tool" but the
  SQL comments and `try_claim_daily_job` say otherwise — follow the files, not the doc.)
- **`permissions` seed:** v1.sql uses `ON CONFLICT (code) DO UPDATE SET name = EXCLUDED.name,
  category = EXCLUDED.category, description = EXCLUDED.description` (line ~544), not `DO NOTHING`.
  Match it. Column is `category` (plan has this right).
- **SUPER_ADMIN grant:** the plan is **correct** that v1.6 must grant the new permissions to
  SUPER_ADMIN explicitly (the `CROSS JOIN permissions` backfill only runs when its own file runs —
  v1.4 exists precisely because that was missed once). Use the exact v1.sql idiom rather than
  enumerating: `INSERT INTO role_permissions (role_id, permission_code) SELECT r.id, p.code FROM
  roles r CROSS JOIN permissions p WHERE r.code = 'SUPER_ADMIN' ON CONFLICT DO NOTHING;`
- **`role_permissions` join idiom:** `FROM roles r JOIN permissions p ON TRUE WHERE (r.code,
  p.code) IN (...)` — plan matches v1.sql line ~568 and v1.5.
- **CHECK-based enums, `IF NOT EXISTS`, `DECIMAL(10,7)` + range CHECK, `gen_random_uuid()`:** all
  correct and consistent with `sites` / `federated_camera`.
- **Index naming:** existing convention keeps the **full table name** in the index
  (`ix_camera_status_history_camera`, `ix_config_audit_entity`). Rename `ix_maint_*` →
  `ix_maintenance_records_*` and `ix_camera_health_hist_camera` →
  `ix_camera_health_history_camera`.
- **Q6 — no reusable `updated_at` trigger exists.** The only trigger functions in the schema are
  `assert_*_acyclic`, `record_camera_status_change`, `stamp_camera_status_changed_at`,
  and partition helpers. Existing tables (`connector_target`) stamp `updated_at = now()` **in the
  repository `UPDATE` statement** (`ConnectorTargetRepository.cs:159`). Do that; do not add a
  trigger. If you do want a trigger, follow the `v1.1` pattern: `CREATE OR REPLACE FUNCTION` +
  `DROP TRIGGER IF EXISTS` + `CREATE TRIGGER` (not `CREATE OR REPLACE TRIGGER`).
- **Q4 — CONFIRMED: no `vendors` table.** `vendor_kind` is an `ENUM` type
  (`db/versions/v1.sql:838`) used by `connector_target.vendor`. A bare `cameras.vendor_id UUID`
  references nothing and does not match how Model 3 models vendor. For slice 1, **drop `vendor_id`**
  and keep `manufacturer` / `model` free-text, or use `vendor VARCHAR` + `CHECK` mirroring
  `connector_target`. Do not ship an unreferenced UUID column plus an index on it. This also
  removes the `vendorId` filter/property from endpoints 3/17/19 until a real vendor entity exists
  (`GET /gis/coverage` "by vendor" bucket becomes "by manufacturer" string grouping).

### 10.6 `Federation.Core` `CoverageSector` helper — fine

`Federation.Core` is "domain models, error taxonomy, no I/O" (`CLAUDE.md` project table). A pure
trigonometric `CoverageSector` helper (lat/long + azimuth + FOV + range → vertex list) is a
domain calculation with no I/O and belongs there. Keep it `static`, take primitives or a small
`readonly record struct`, return `IReadOnlyList<(double Lon, double Lat)>`. At O(cameras in bbox)
with N≤72 vertices this is not a performance concern for slice 1; don't pool or span-optimise it
yet.

### 10.7 GeoJSON / JSON serialization

**There is no `ConfigureHttpJsonOptions` / custom `JsonSerializerOptions` anywhere in the API
host** (`Program.cs`, `ApiHttpExtensions.cs`, none of the `Api*Extensions.cs`). So the minimal-API
default applies: `JsonSerializerOptions.Web` — camelCase, case-insensitive, **enums serialized as
numbers** (no `JsonStringEnumConverter` registered). The existing convention handles this by
projecting enums to `string` in the response record and never exposing an enum-typed property
(`VmsResponse` takes `t.Vendor.ToString()`, `VmsEndpoints.cs:265`). Your GeoJSON DTOs
(`FeatureCollection`, `Feature`, `Geometry`, properties bag) must be plain
`string` / `double` / `IReadOnlyList<double[]>` / nested records — no enums, no `JsonElement`
gymnastics on the write side.

`application/geo+json` (Q13): `services.AddProblemDetails()` is registered and there are three
`IExceptionHandler`s (`BadRequestExceptionHandler`, `ConstraintViolationExceptionHandler`,
`UnhandledExceptionHandler`) — so `Problem`/`ProblemDetails` responses and DB-constraint → HTTP
mapping already work. For the geo media type, return `TypedResults.Json(featureCollection,
contentType: "application/geo+json")` inside the `Results<JsonHttpResult<FeatureCollection>, ...>`
union. No host change needed.

**New concern not in the plan — PATCH merge semantics.** There is **no PATCH-with-merge endpoint
anywhere in the codebase today** (VMS is `PUT`-replace only, `VmsEndpoints.cs:74`). System.Text.Json
on a plain record cannot distinguish "field absent" from "field present and null", which endpoints
5, 13 and the GIS-pin-drag flow all require. This needs a deliberate mechanism: read the raw
`JsonElement`/`JsonDocument` in the handler and check `TryGetProperty`, or a per-field
`Optional<T>` wrapper struct, or explicit `bool XxxSpecified` companions. Pick one and specify it
in the plan — it is the fiddliest part of this endpoint set and "all-nullable record" (as
§2.1 row 5 implies) silently gives you "null clears it" with no way to express "leave alone".

### 10.8 Slice-1 scope call — agree

Deferring PostGIS + coverage-gap analysis is the right call. `CLAUDE.md` is explicit: "No
PostgreSQL extensions … spatial querying belongs to Model 1 … added then, against a requirement."
Slice-1 coverage sectors are pure C# trig over `DECIMAL` lat/long and a bbox btree — no extension,
no stored geometry, and they cover demo steps 4–5 ("place on GIS → show coverage"). Gap analysis
genuinely needs `ST_Union`/`ST_Difference` + boundary polygons that `GEOGRAPHY-SCHEMA.md` does not
carry. Ship `GET /api/v1/gis/gaps` as a documented `501` (Q12) so the OpenAPI contract is stable,
and make PostGIS its own reviewed v1.7 decision (Q2). This matches the demo path in `CLAUDE.md`.

### 10.9 Permission grants — gaps that will break endpoints (extends Q1)

Verified against `db/versions/v1.sql:544-626`:

- **`camera.maintenance.read` / `camera.maintenance.update` are granted to `MAINTENANCE_OPERATOR`
  only.** Neither `STATE_ADMIN` nor `DEPARTMENT_ADMIN` has them. Endpoints 11–13 would be
  admin-inaccessible. v1.6 should grant `camera.maintenance.read` + `camera.maintenance.update`
  to `STATE_ADMIN` and `DEPARTMENT_ADMIN`.
- **`STATE_ADMIN` lacks `camera.health.read`** (DEPARTMENT_ADMIN, VMS_ADMIN, VMS_OPERATOR,
  CAMERA_OPERATOR, MAINTENANCE_OPERATOR, VIEWER have it). Endpoints 8–9 would fail for a State
  Administrator. Grant it in v1.6.
- **`VMS_ADMIN` gets `camera.reconcile` (per the plan) but has no `camera.create`.** Endpoint 16
  (`POST /cameras/from-federated`) requires both — it would 403 for the exact role the plan
  positions as the reconciliation user. Either grant `camera.create` to `VMS_ADMIN` in v1.6, or
  document that `from-federated` is admin-only and `VMS_ADMIN` uses the two-step (create by an
  admin, then `VMS_ADMIN` reconciles).
- `gis.coverage.read` is on `STATE_ADMIN` / `DEPARTMENT_ADMIN` / `ANALYST` only — the plan's §0
  statement about the GIS roles is accurate.

### 10.10 Verdict — GO WITH CHANGES

The plan fits the codebase well: endpoint style, `UnitOfWork`/audit flow, `CallerContext`
threading, "SQL in Storage" and the version-file discipline are all correctly understood, and the
open questions are mostly answered correctly by the plan itself. Nothing here blocks starting, but
the following must be settled in the plan text (not discovered mid-implementation):

Must-fix before coding:
1. **RBAC `{id}` predicate** — drop "prefer the single-call form"; use the dual-dimension check
   (no single-flag bypass) for reads *and* lists (§10.2).
2. **Add `CallerContext.IsUnscopedForGeography`** and refactor `GeographyRepository.Geo` onto it
   (§10.2 / Q10).
3. **v1.6 permission grants** — add `camera.maintenance.*` + `camera.health.read` to the admin
   roles; resolve the `VMS_ADMIN` + `from-federated` gap; use the exact SUPER_ADMIN CROSS JOIN
   idiom and `ON CONFLICT (code) DO UPDATE` for `permissions` (§10.5 / §10.9).
4. **`vendor_id`** — remove it for slice 1 or model it as `vendor VARCHAR` + CHECK; adjust the
   `vendorId` filter/bucket on endpoints 3/17/19 (§10.5 / Q4).
5. **Specify the PATCH merge mechanism** (JsonElement read / `Optional<T>` / `*Specified`) — an
   all-nullable record does not express "leave unchanged" (§10.7).
6. **`federated_camera.site_id` is nullable** — define the geo-scope fallback for
   `GET /cameras/unreconciled` (§10.4).

Should-fix (cheap, do them now):
7. Version-file header + `Requires:` line + `SET search_path` note to match v1.1–v1.5 (§10.5).
8. Index names carry the full table name (§10.5).
9. `updated_at` stamped in the repository `UPDATE`, no trigger (§10.5 / Q6).
10. Add `Cameras` / `Gis` to `ApiTags.Ordered`, not just as constants (§10.1).

Confirmed answers: **Q4** no `vendors` table (`vendor_kind` enum only). **Q6** no reusable
`updated_at` trigger; stamp in repo. **Q9** the `federated_camera` upsert preserves a non-null
`camera_id` — no Model 3 change needed (`ConnectorStateStore.cs:288-302`). **Q10** the
`unscoped-geo` claim and `CallerContext.UnscopedGeography` set exist and are populated on both
auth paths; only the convenience accessor is missing. **Q14** `RequirePermission` is not
DB-validated at startup — adding `permissions` rows is sufficient, and typos are not caught.
The scope-SQL function signatures in §5 all match `v1.sql`'s final definitions.

---

## 11. Resolutions applied for implementation (slice 1)

Decisions taken from §10 before coding. This section governs where it differs from §1–§9.

1. **RBAC predicate.** Both single-row `{id}` reads/writes and list/feed queries use the
   **two-flag dual-dimension** form: `(@UnscopedOrg OR c.organization_unit_id IN
   authorized_org_units(...))` **AND** `(@UnscopedGeo OR site_area IN
   authorized_geographic_areas(...))`, where `@UnscopedOrg = caller.IsUnscopedFor(perm)` and
   `@UnscopedGeo = caller.IsUnscopedForGeography(perm)`. No single-flag `has_permission`
   bypass. The "prefer the single-call form" sentence in §5 is withdrawn.
2. **`CallerContext.IsUnscopedForGeography(string)`** added next to `IsUnscopedFor`;
   `GeographyRepository.Geo` refactored to call it.
3. **v1.6 permission seed** (§3.4 superseded): new permissions `camera.delete`,
   `camera.import`, `camera.reconcile` with `ON CONFLICT (code) DO UPDATE SET …`. Grants:
   - SUPER_ADMIN via the exact `CROSS JOIN permissions` idiom.
   - `camera.delete`, `camera.import`, `camera.reconcile` → STATE_ADMIN, DEPARTMENT_ADMIN.
   - `camera.reconcile` + `camera.create` → VMS_ADMIN (so `from-federated` works for it).
   - `camera.maintenance.read`, `camera.maintenance.update` → STATE_ADMIN, DEPARTMENT_ADMIN.
   - `camera.health.read` → STATE_ADMIN.
4. **`vendor_id` dropped for slice 1.** `cameras` keeps free-text `manufacturer` / `model`.
   The `vendorId` filter on endpoints 3/17 and the "by vendor" bucket on 19 become
   `manufacturer` string grouping. A `vendors` entity is a later slice.
5. **PATCH merge mechanism:** the handler reads the request body as `JsonElement` and
   applies only the properties present (`TryGetProperty`); an explicit `null` clears a
   nullable column, an absent property is untouched. Repository takes a
   `CameraPatch` record of `JsonElement?`-free resolved values plus a `HashSet<string>
   changed` field list. No `Optional<T>` wrapper.
6. **`GET /cameras/unreconciled` geo scope:** when `federated_camera.site_id IS NULL`, the
   row is scoped on `organization_unit_id` only (its geo dimension is treated as in-scope
   for a geo-scoped caller). Such rows are still returned — hiding a VMS-reported camera
   because it lacks a site defeats the point of the backlog view.
7. v1.6 file: `====` banner, rationale, `-- Requires: v1.sql … v1.5.sql`, `SET search_path
   TO federation, public;`, and the "Transaction is owned by MigrationRunner" note.
8. Index names carry the full table name (`ix_cameras_*`, `ix_camera_health_history_*`,
   `ix_maintenance_records_*`).
9. `updated_at` stamped in the repository `UPDATE`; no trigger.
10. `ApiTags`: `Cameras` and `Gis` added as constants **and** to `Ordered` (after `Vms`),
    with descriptions.
11. GeoJSON responses: `TypedResults.Json(fc, contentType: "application/geo+json")`; DTOs
    are plain `string`/`double`/`double[]`/nested records, no enum-typed properties.

---

## 12. Implementation status

**Slice 1a — shipped (builds clean, 0 warnings; 66 unit tests pass):**

- `db/versions/v1.6.sql` + `db/objects/tables/{cameras,camera_health_history,maintenance_records}.sql`
- `CallerContext.IsUnscopedForGeography`; `GeographyRepository.Geo` refactored onto it
- `Federation.Core`: `Camera` domain record + `CameraStatus`/`CameraVocab`; `CoverageSector` trig helper (`Core/Geo/`)
- `Federation.Storage`: `CameraRepository` (get, keyset list, create, replace, JsonElement-driven patch, retire — all dual-scope), `GisQueryRepository` (bbox feed, per-camera point, coverage aggregate)
- `Federation.Api`: `CameraContracts.cs` (+ GeoJSON DTOs); `CameraEndpoints.cs` — `POST /cameras`, `GET /cameras` (paged/filtered), `GET/PUT/PATCH/DELETE /cameras/{id}`; `GisEndpoints.cs` — `GET /gis/cameras`, `GET /cameras/{id}/coverage`, `GET /gis/coverage`, `GET /gis/gaps` (501); `ApiTags` Cameras/Gis; DI + route wire-up
- `tests/Trinetra.UnitTests/CoverageSectorTests.cs`

**Slice 1b — shipped (builds clean, 0 warnings; 66 unit tests pass; API host starts):**

- Health: `CameraHealthRepository`; `GET /cameras/{id}/health`, `GET /cameras/{id}/health/history` (30d default / 90d max), `PATCH /cameras/{id}/health` (manual override, `MANUAL` history row, audited)
- Maintenance: `CameraMaintenanceRepository` (named to avoid the scheduler's `MaintenanceRepository`); `GET/POST /cameras/{id}/maintenance`, `PATCH /cameras/{id}/maintenance/{recordId}` — record lifecycle drives `cameras.maintenance_status` in the same tx; terminal-state change → 409
- Bulk import: `POST /cameras/bulk-import` (`insert`/`upsert`, 1..500 rows, one tx + one audit row per row, always 200 with a per-row report)
- Reconciliation: `ReconciliationRepository`; `GET /cameras/unreconciled` (keyset), `POST /cameras/{id}/reconcile` (idempotent; 409 on relink), `POST /cameras/from-federated` (create + link in one call)
- `CameraScope` shared predicate helper; `CameraRepository.FindLiveIdByCodeAsync`

**Not yet built:**

- Integration tests: scope matrix, `camera_code` uniqueness, reconcile 409, bulk partial-failure, maintenance→status transitions, audit-row-per-mutation (need Postgres/Docker — not available in the current environment)
- `GET /gis/gaps` real implementation (PostGIS — deliberately deferred)
