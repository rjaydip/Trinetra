# Model 1 --- Centralised CCTV Registry & GIS Mapping

## Objective

Create a centralised, authoritative registry of CCTV assets across
departments and provide geographic intelligence about their location,
orientation, field of view, range, health, maintenance, and coverage.

## Core Features

-   Bulk camera onboarding
-   Manual camera onboarding
-   API-based camera onboarding
-   Department-wise camera registry
-   Vendor-wise camera registry
-   Unique camera identification
-   Camera technical metadata
-   Camera location and GIS mapping
-   Camera orientation / azimuth
-   Horizontal FOV
-   Vertical FOV
-   Effective detection range
-   Coverage-sector visualization
-   Camera health monitoring
-   Connectivity status
-   Maintenance status and history
-   Installation and ageing information
-   Search and filtering
-   Metadata export
-   Audit trail
-   Role-based access

## Camera Data Model

Suggested fields:

``` text
camera_id
department_id
geographic_area_id
camera_name
vendor
model
camera_type
latitude
longitude
altitude
azimuth
horizontal_fov
vertical_fov
effective_range
installation_date
operational_status
connectivity_status
maintenance_status
vms_id
stream_reference
credential_reference
created_at
updated_at
```

Sensitive credentials should not be stored as ordinary camera metadata.
Store only a secure reference to a secret-management system.

## GIS Coverage

The system converts camera parameters into an estimated coverage
representation:

``` text
Camera Point
     |
     +-- Azimuth
     +-- Horizontal FOV
     +-- Effective Range
     |
     v
Coverage Sector / Polygon
```

The GIS can display:

-   Camera point
-   Camera direction
-   Coverage sector
-   Department layer
-   Vendor layer
-   Operational-status layer
-   Maintenance layer
-   Coverage-gap layer

## Coverage Analysis

The registry can identify:

-   Areas with no camera coverage
-   Potential blind spots
-   Excessive overlap
-   Non-functional camera locations
-   Ageing infrastructure
-   Areas requiring additional CCTV

Coverage analysis should be presented as an estimated planning aid;
actual visibility can be affected by buildings, terrain, obstructions,
lighting, camera mounting, and lens characteristics.

## Health & Maintenance

Camera health records should include:

``` text
last_seen
last_heartbeat
connectivity_status
operational_status
maintenance_status
last_maintenance_date
next_maintenance_date
failure_reason
```

## APIs

A first slice is implemented under `/api/v1`. **`docs/MODEL-1-API-PLAN.md` is the authoritative
contract** — request/response shapes, the `v1.6` schema, scope rules, validation, and what is
and is not built. In outline:

``` text
POST   /api/v1/cameras                          register (manual onboarding)
GET    /api/v1/cameras                          paginated, filtered registry list
GET    /api/v1/cameras/{id}
PUT    /api/v1/cameras/{id}                      full replace
PATCH  /api/v1/cameras/{id}                      partial update
DELETE /api/v1/cameras/{id}                      soft-delete / retire

POST   /api/v1/cameras/bulk-import              insert | upsert, per-row result
GET    /api/v1/cameras/{id}/health              + /health/history
PATCH  /api/v1/cameras/{id}/health              manual override
GET    /api/v1/cameras/{id}/maintenance         + POST, + PATCH .../{recordId}

GET    /api/v1/cameras/unreconciled             reconciliation backlog
POST   /api/v1/cameras/{id}/reconcile           link to a VMS-discovered camera
POST   /api/v1/cameras/from-federated           create + link in one call

GET    /api/v1/gis/cameras                      GeoJSON map source, bbox-limited
GET    /api/v1/cameras/{id}/coverage            estimated coverage sector
GET    /api/v1/gis/coverage                     aggregate counts, no geometry
GET    /api/v1/gis/gaps                         501 until spatial querying lands
```

Coverage sectors are computed in the application from azimuth + horizontal FOV + effective
range; there is no stored geometry and no PostGIS. Coverage-gap analysis is deferred to the
version that introduces spatial querying.

## Federation Discovery and Reconciliation

Model 1 is the authoritative camera registry. Model 3 discovers what a VMS currently reports and
stores that observation in `federation.federated_camera`; discovery does not create or delete
registry records automatically.

``` text
VMS target (Model 3)
  └── native_camera_id
        |
        | background inventory poll
        v
federated_camera
        |
        | explicit reconciliation
        v
camera registry record (Model 1)
  └── camera_id
```

The discovered row is keyed by `(target_id, native_camera_id)`. Until reconciliation succeeds,
`federated_camera.camera_id` is `NULL`; this identifies cameras that still need a registry match.
The worker refreshes the discovered inventory approximately every five minutes and status every
30 seconds. It does not remove a registry record, or mark a camera retired, merely because one
poll returns a partial inventory.

Manual registration uses the registry resource (`POST /api/v1/cameras`). Reconciliation
(`POST /api/v1/cameras/{id}/reconcile`, or `GET /api/v1/cameras/unreconciled` for the backlog)
then links the stable registry UUID to the VMS-native identifier while preserving both ownership
models; the inventory poll never clears an existing link.

## Demonstration

1.  Import sample cameras from multiple departments.
2.  Validate and assign Camera IDs.
3.  Display cameras at their geographic coordinates.
4.  Configure azimuth, FOV, and range.
5.  Render coverage sectors.
6.  Filter by department/vendor/status.
7.  Mark selected cameras as failed or under maintenance.
8.  Show coverage gaps and ageing infrastructure.
