# Model 1 Registry & GIS API Design

**Status:** Approved for implementation on 2026-08-22

## Goal

Add the complete Model 1 backend contract for an authoritative CCTV registry and GIS planning layer. The API will provide live, authorization-scoped registry data for the planned frontend without creating a live-video or recording feature.

## Constraints

- Keep the existing .NET 10, Minimal API, Dapper, Npgsql, and PostgreSQL 17 stack.
- Add a forward-only `db/versions/v2_model_1_registry.sql` migration. No service applies it automatically.
- Do not require PostGIS or another PostgreSQL extension.
- Do not store a coverage polygon for each camera.
- All reads and mutations must enforce existing organization, geographic, and resource scope rules in SQL.
- Never store or return credential material. A credential reference remains a reference only.
- Return only fields supported by the Model 1 registry schema; do not manufacture frontend-only fields or metrics.
- Preserve historical evidence. Camera deletion is soft deactivation.
- Use RFC 7807 problem responses for invalid input, conflicts, and unavailable GIS prerequisites.

## Existing State

The current API has Model 3 VMS inventory routes, including `/api/v1/vms/{id}/cameras`. That inventory is not the Model 1 registry: it lacks a stable registry entity, camera optics, maintenance, coverage, reports, import, export, and audit read paths. `federated_camera.camera_id` is intentionally nullable until Model 1 reconciles it.

Existing RBAC permissions include `camera.read`, `camera.create`, `camera.update`, `camera.health.read`, `camera.maintenance.read`, `camera.maintenance.update`, `gis.read`, `gis.coverage.read`, and `audit.read`. The migration adds the narrowly-scoped `camera.health.update` permission for trusted health ingestion; it is granted to the appropriate existing administrative and maintenance roles. Existing scope functions already evaluate organization and geography independently and resource pins as an additional constraint.

## Data Model

### `cameras`

Create an authoritative registry table with:

- Immutable UUID `id` and unique business identifier `camera_code`.
- Required `name`, `organization_unit_id`, `site_id`, `camera_type`, latitude, longitude, operational status, connectivity status, maintenance status, `created_at`, `updated_at`, `created_by`, and `updated_by`.
- Optional manufacturer, model, vendor, serial number, altitude, mounting height, azimuth, tilt, horizontal and vertical FOV, effective range, IP address, port, protocol, VMS target id, credential reference, installation date, `last_seen_at`, and `last_health_check_at`.
- Nullable `deactivated_at`, `deactivated_by`, and `deactivation_reason` for soft deactivation.
- Database constraints for coordinate bounds, non-negative heights/ranges/ports, azimuth and FOV ranges, valid status vocabularies, and the uniqueness of the camera code.

The table does not contain raw credentials or a persisted coverage polygon. A camera may reference one VMS target; reconciliation to a VMS-native camera remains optional and is represented by `federated_camera.camera_id`.

### Operational history

`camera_health_observations` is append-only and records a camera id, checked time, operational and connectivity values, optional latency, failure reason, and actor metadata.

`camera_maintenance_records` records a camera id, maintenance type and status, reported/started/completed times, description, performer, creator, and timestamps. Creating or updating a maintenance record updates the camera's current maintenance summary in the same transaction.

### Import batches

`camera_import_batches` stores the requesting actor, source filename, accepted CSV row data, row-level validation results, lifecycle status, expiry, commit time, and created/failed counts. It is a short-lived server-side staging area for a validate-preview-commit workflow. Batch commits are transactional and idempotent.

### Geographic boundaries

Add nullable `boundary_geojson` JSONB fields to both `geographic_areas` and `sites`. Each holds one optional GeoJSON `Polygon` or `MultiPolygon` boundary. It is the analysis target for coverage and gap reports; it is not camera geometry.

Boundary writes use existing geography-management authorization and create normal configuration audit records.

## Authorization Model

Every registry query uses the existing `federation.has_permission` function with the camera's organization unit, the geographic area reached through its site, and `resource_type = 'camera'` with the camera id. This preserves the existing group-by-group rule: organization and geography scopes are combined within the same permission-bearing group, while a resource pin only narrows access.

List, map, coverage, export, report, and audit queries apply this predicate before returning or aggregating records. A record outside the caller's scope is omitted from collections and returns 404 from item routes. Mutations first load the resource under the corresponding write permission so out-of-scope resources cannot be distinguished from absent resources.

Permissions are applied as follows:

| Capability | Permission |
|---|---|
| Registry/map list, detail, CSV export | `camera.read` and `gis.read` for GIS routes |
| Create/import/commit | `camera.create` |
| Update/deactivate | `camera.update` |
| Health history and current health | `camera.health.read` |
| Record a health observation | `camera.health.update` |
| Maintenance read/write | `camera.maintenance.read` / `camera.maintenance.update` |
| Coverage and gap reports | `gis.coverage.read` |
| Camera audit history | `audit.read` plus scoped `camera.read` |
| Boundary read/write | `geography.read` / `geography.manage` |

Every mutation shares one `UnitOfWork` transaction with its `config_audit` entry. Export and reporting never bypass the scoped query used for ordinary list reads.

## HTTP API

All routes are under `/api/v1` and use typed request/response contracts.

### Registry

| Method | Route | Behaviour |
|---|---|---|
| `GET` | `/cameras` | Paged list with supported search, organization/site/area/type/vendor/status/VMS filters and allow-listed sort fields. Excludes deactivated records unless requested by an authorized caller. |
| `POST` | `/cameras` | Create one registry record. |
| `GET` | `/cameras/{id}` | Fetch one authorized registry record. |
| `PUT` | `/cameras/{id}` | Replace editable registry metadata. Immutable id and camera code are not silently changed. |
| `POST` | `/cameras/{id}/deactivate` | Soft deactivate with an explicit reason; historical records remain readable. |
| `GET` | `/cameras/export` | Stream the same authorized, filtered registry result as CSV. |
| `GET` | `/cameras/{id}/health` | Current health summary plus chronological observations. |
| `POST` | `/cameras/{id}/health-observations` | Record an authorized integration health observation and update the current summary. |
| `GET` | `/cameras/{id}/maintenance` | Return maintenance history. |
| `POST` | `/cameras/{id}/maintenance` | Create a maintenance record. |
| `PUT` | `/cameras/{id}/maintenance/{recordId}` | Update a maintenance record. |
| `GET` | `/cameras/{id}/audit` | Return the scoped camera entries from `config_audit`. |

### CSV import

| Method | Route | Behaviour |
|---|---|---|
| `GET` | `/camera-imports/template` | Download the canonical CSV header and field guidance. |
| `POST` | `/camera-imports` | Upload a CSV, validate every row, persist a draft batch, and return the preview and row errors. |
| `GET` | `/camera-imports/{id}` | Retrieve a batch preview while it is valid. |
| `POST` | `/camera-imports/{id}/commit` | Atomically create all valid rows, audit each creation, and mark the batch committed. A second commit returns the original completed summary. |

The initial supported format is CSV only. The parser rejects unsupported content types, malformed headers, excessively large uploads, invalid values, duplicate codes in the file, and duplicates that already exist in the database. No valid row is persisted as a camera until the explicit commit call succeeds.

### GIS

| Method | Route | Behaviour |
|---|---|---|
| `GET` | `/gis/cameras` | Return an authorized GeoJSON `FeatureCollection` of camera points with supported filters. |
| `GET` | `/gis/coverage` | Return an authorized GeoJSON `FeatureCollection` of derived sector polygons for cameras with enough optical data. Accepts an area or site target and supported registry filters. |
| `GET` | `/gis/gaps` | Return estimated uncovered GeoJSON geometry for one authorized area/site boundary. |
| `GET` / `PUT` | `/geographic-areas/{id}/boundary` | Read or replace an optional validated GeoJSON boundary. |
| `GET` / `PUT` | `/sites/{id}/boundary` | Read or replace an optional validated GeoJSON boundary. |

### Reports

| Method | Route | Behaviour |
|---|---|---|
| `GET` | `/reports/gap-analysis` | Return the scoped target boundary, derived coverage summary, uncovered geometry, and explanatory caveat. |
| `GET` | `/reports/ageing-infrastructure?minimumAgeYears={positive integer}` | Return registered cameras whose installation date is at least the explicit threshold old. No hidden policy default is applied. |
| `GET` | `/reports/registry-summary` | Return scoped department, camera type, and status summaries calculated from live registry records. |
| `GET` | `/reports/{reportName}/export` | Stream the selected authorized report as CSV using the same required query parameters and filters. |

## GIS and Reporting Calculations

The server derives a coverage sector only when a camera has latitude, longitude, azimuth, horizontal FOV, and effective range. It samples the sector edge into a bounded number of points, transforms inputs into a local metre-based coordinate plane, builds the sector, and converts the response back to WGS84 GeoJSON.

Gap analysis requires either an area boundary or a site boundary. The service filters accessible active cameras to the chosen target, unions their derived sectors, subtracts the union from the target boundary, and returns the remaining geometry. Missing, invalid, or incompatible boundaries receive an actionable 422 response; the server never returns an invented gap. Results include a permanent `estimated` caveat because actual visibility depends on terrain, structures, lighting, installation, and lens calibration.

The reports aggregate only authorized active cameras. Ageing excludes cameras without an installation date rather than estimating age. Report export accepts only the three named report types; it never dispatches a route name dynamically.

## Error Behaviour

- Invalid requests return the existing RFC 7807 validation/error shape with field-specific detail where safe.
- Existing camera-code conflicts return 409 without exposing records outside the caller's scope.
- Unauthorized API calls use the existing authentication/authorization pipeline; scoped-but-unreachable resources return 404.
- Missing coverage parameters or target boundaries return 422 and explain the valid next action.
- Import previews preserve row numbers and validation messages but never echo secrets or raw credentials.
- A failed import commit rolls back all camera inserts and audit rows.

## Implementation Boundaries

- `Core` holds camera, health, maintenance, import, and GIS value types independent of HTTP and SQL.
- `Storage` owns scoped camera, import, GIS, report, and audit repositories. The authorization predicate remains adjacent to each SQL query.
- `Api.Contracts` holds all wire DTOs. `Endpoints` maps focused camera, import, GIS, report, and boundary endpoint groups.
- A dedicated GIS calculation service derives coverage/gaps and is unit tested independently of storage.
- Existing Model 3 VMS inventory stays unchanged except for the nullable foreign key to its Model 1 registry id.

## Test Strategy

### Unit tests

- Coordinate, optical-field, status, GeoJSON, and CSV validation.
- Sector generation for known bearings/ranges/FOVs and omission of insufficient camera data.
- Gap subtraction and explicit missing-boundary behaviour.
- Import batch parsing, duplicate detection, all-or-nothing commit semantics, and ageing threshold validation.

### PostgreSQL integration tests

- Apply `v1.sql` followed by `v2_model_1_registry.sql` to the existing Testcontainers PostgreSQL fixture.
- Assert all new schema constraints, indexes, and the foreign key.
- Assert organization/geography/resource scope isolation for list, detail, map, export, reports, and audit records.
- Assert every mutation commits its matching `config_audit` record and rolls both back on failure.
- Assert batch preview and commit do not create partial registry records.

### API integration tests

- Exercise authentication, RFC 7807 validation, route permissions, 404 scope concealment, list/filter/paging, CSV responses, and GeoJSON content against a hosted test API connected to the PostgreSQL fixture.

## Deployment

1. Apply `db/versions/v2_model_1_registry.sql` to each database after verifying a backup and a previous-build compatibility window.
2. Deploy the API binary containing the Model 1 endpoints.
3. Import camera data through the batch API or the documented template.
4. Add area/site boundaries where actual gap analysis is required.

The implementation must not modify `http://localhost:5261` or any LAN database automatically. A deployment operator applies the migration and deploys the new binary deliberately.

## Explicitly Excluded

- Live video, recording, playback, video walls, ANPR, recognition, and Model 2/3 workflow expansion.
- Per-camera persisted polygons.
- PostGIS and other database extensions.
- A hard-coded ageing threshold.
- An Excel import format in the first release.
