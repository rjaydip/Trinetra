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
site_id
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

Example REST resources:

``` text
POST   /api/cameras
GET    /api/cameras
GET    /api/cameras/{cameraId}
PUT    /api/cameras/{cameraId}
DELETE /api/cameras/{cameraId}

POST   /api/cameras/bulk-import
GET    /api/cameras/{cameraId}/health
GET    /api/cameras/{cameraId}/maintenance

GET    /api/gis/cameras
GET    /api/gis/coverage
GET    /api/gis/gaps
```

## Demonstration

1.  Import sample cameras from multiple departments.
2.  Validate and assign Camera IDs.
3.  Display cameras at their geographic coordinates.
4.  Configure azimuth, FOV, and range.
5.  Render coverage sectors.
6.  Filter by department/vendor/status.
7.  Mark selected cameras as failed or under maintenance.
8.  Show coverage gaps and ageing infrastructure.
