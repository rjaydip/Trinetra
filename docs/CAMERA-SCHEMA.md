# Camera Schema

## Purpose

Represents a CCTV asset registered in Trinetra.

Every camera has two independent dimensions:

1. **Ownership / operation** through the organization hierarchy.
2. **Physical location** through the geographic hierarchy.

```text
Camera
├── Ownership
│   └── Organization Unit
└── Location
    └── Geographic Area (any level)
        └── parent chain
```

## Core Rule

Do not duplicate the full geography or organization hierarchy inside the camera table.

Avoid:

```text
camera
├── department_id
├── district_id
├── taluka_id
└── village_id
```

Prefer:

```text
camera
├── organization_unit_id
└── geographic_area_id
```

The referenced entities resolve the hierarchy.

## `cameras`

| Field | Type | Required | Description |
|---|---|---:|---|
| `id` | UUID | Yes | Immutable internal camera ID |
| `camera_code` | VARCHAR(100) | Yes | Unique business identifier |
| `name` | VARCHAR(255) | Yes | Display name |
| `organization_unit_id` | UUID | Yes | Owner/operator organization unit |
| `geographic_area_id` | UUID | Yes | The area the camera sits in, at any level of the hierarchy |
| `vendor_id` | UUID | No | Vendor reference |
| `manufacturer` | VARCHAR(255) | No | Manufacturer |
| `model` | VARCHAR(255) | No | Camera model |
| `camera_type` | VARCHAR(50) | Yes | Fixed, PTZ, dome, bullet, ANPR, etc. |
| `serial_number` | VARCHAR(255) | No | Hardware serial number |
| `latitude` | DECIMAL(10,7) | Yes | Camera latitude |
| `longitude` | DECIMAL(10,7) | Yes | Camera longitude |
| `altitude` | DECIMAL(10,3) | No | Elevation if available |
| `mounting_height` | DECIMAL(10,3) | No | Mounting height |
| `azimuth` | DECIMAL(7,3) | No | Horizontal orientation |
| `tilt` | DECIMAL(7,3) | No | Vertical tilt |
| `horizontal_fov` | DECIMAL(7,3) | No | Horizontal FOV |
| `vertical_fov` | DECIMAL(7,3) | No | Vertical FOV |
| `effective_range` | DECIMAL(10,3) | No | Effective detection/visibility range |
| `ip_address` | INET | No | Camera/network IP |
| `port` | INTEGER | No | Network/service port |
| `protocol` | VARCHAR(50) | No | RTSP, ONVIF, HTTP, HTTPS, etc. |
| `vms_id` | UUID | No | Associated VMS |
| `credential_reference` | VARCHAR(255) | No | Secure secret reference |
| `installation_date` | DATE | No | Installation date |
| `operational_status` | VARCHAR(30) | Yes | ONLINE / OFFLINE / DEGRADED / UNKNOWN |
| `connectivity_status` | VARCHAR(30) | Yes | CONNECTED / DISCONNECTED / UNKNOWN |
| `maintenance_status` | VARCHAR(30) | Yes | NORMAL / REQUIRED / UNDER_MAINTENANCE / RETIRED |
| `last_seen_at` | TIMESTAMPTZ | No | Last successful health observation |
| `last_health_check_at` | TIMESTAMPTZ | No | Last health check |
| `created_at` | TIMESTAMPTZ | Yes | Creation time |
| `updated_at` | TIMESTAMPTZ | Yes | Last update time |
| `created_by` | UUID | Yes | Creator |
| `updated_by` | UUID | Yes | Last updater |

## Identity

Use the UUID as the immutable internal identity and `camera_code` as the human/business identifier.

Do **not** use an IP address as the camera identity because IP addresses may change.

## Ownership

```text
Camera
  └── organization_unit_id
       └── Organization Unit
            └── Organization
```

Example:

```text
Camera CAM-AHM-001245
└── Ahmedabad Commissionerate
    └── Police Department
```

## Location

```text
Camera
└── Geographic Area (e.g. a ward, or a per-junction area)
    └── Taluka
        └── District
            └── State
```

This lets the system answer both:

- Which Police cameras are in Ahmedabad?
- Which departments have cameras in this area?

## GIS

The camera provides a geographic point from latitude/longitude.

Estimated 2D coverage can be derived from:

```text
latitude
longitude
azimuth
horizontal_fov
effective_range
```

Additional technical parameters:

```text
vertical_fov
tilt
mounting_height
```

should still be stored for future visibility modelling.

Coverage is an estimated planning representation and may be affected by buildings, terrain, obstructions, lighting, and lens characteristics.

## Status

### Operational

```text
ONLINE
OFFLINE
DEGRADED
UNKNOWN
```

### Connectivity

```text
CONNECTED
DISCONNECTED
UNKNOWN
```

### Maintenance

```text
NORMAL
REQUIRED
UNDER_MAINTENANCE
RETIRED
```

Keep these separate. A camera can be `OFFLINE` because it is `UNDER_MAINTENANCE`.

## Network and Credentials

IP/port/protocol are infrastructure properties and may change.

Never store raw usernames/passwords as ordinary camera fields.

Use:

```text
Camera
  └── credential_reference
       └── Secret Management Layer
```

## VMS Relationship

```text
Camera
  └── vms_id
       └── VMS System
            └── VMS Adapter
```

Vendor-specific identifiers should map to the stable Trinetra `camera_id`.

## Health History

Keep current health status on `cameras`, but store historical checks separately:

```text
camera_health_history
---------------------
id
camera_id
status
checked_at
latency_ms
error_code
details
```

## Maintenance History

```text
maintenance_records
-------------------
id
camera_id
maintenance_type
status
reported_at
started_at
completed_at
description
performed_by
created_at
updated_at
```

## Example

```json
{
  "id": "550e8400-e29b-41d4-a716-446655440000",
  "camera_code": "CAM-AHM-001245",
  "name": "Ring Road Junction Camera 01",
  "organization_unit_id": "org-unit-ahm-police",
  "geographic_area_id": "area-ring-road-jn-001",
  "camera_type": "FIXED",
  "latitude": 23.0225,
  "longitude": 72.5714,
  "mounting_height": 8.5,
  "azimuth": 90.0,
  "tilt": -5.0,
  "horizontal_fov": 90.0,
  "vertical_fov": 55.0,
  "effective_range": 120.0,
  "ip_address": "192.168.10.20",
  "port": 554,
  "protocol": "RTSP",
  "vms_id": "vms-001",
  "credential_reference": "secret/cameras/CAM-AHM-001245",
  "operational_status": "ONLINE",
  "connectivity_status": "CONNECTED",
  "maintenance_status": "NORMAL"
}
```

## API Examples

Implemented — see `docs/MODEL-1-API-PLAN.md` for the full contract.

```http
POST   /api/v1/cameras
GET    /api/v1/cameras
GET    /api/v1/cameras/{id}
PUT    /api/v1/cameras/{id}
PATCH  /api/v1/cameras/{id}
DELETE /api/v1/cameras/{id}

POST   /api/v1/cameras/bulk-import

GET    /api/v1/cameras/{id}/health          # + /health/history, PATCH /health
GET    /api/v1/cameras/{id}/maintenance     # + POST, + PATCH .../{recordId}
GET    /api/v1/cameras/{id}/coverage

GET    /api/v1/cameras/unreconciled
POST   /api/v1/cameras/{id}/reconcile
POST   /api/v1/cameras/from-federated
```

The `cameras`, `camera_health_history` and `maintenance_records` tables land in
`db/versions/v1.6.sql`.

## RBAC

Camera access should evaluate both organization and geographic scope:

```text
User
└── Access Group
    ├── Role
    │   └── Permissions
    └── Scope
        ├── Organization
        └── Geography
```

Example:

```text
Group:
  Ahmedabad Police Camera Operators

Role:
  CAMERA_OPERATOR

Organization Scope:
  Police Department

Geographic Scope:
  Ahmedabad District
```

For `camera.read`, the backend verifies the permission and that the requested camera falls inside the group's scope.

Frontend filtering is never a security boundary.

## Relationship With Models 1, 2 and 3

```text
                    CAMERA
                       |
       ┌───────────────┼────────────────┐
       |               |                |
     MODEL 1         MODEL 2          MODEL 3
       |               |                |
     GIS            Analytics        VMS Integration
   Coverage         ANPR             Events
   Health           Detection        Correlation
   Maintenance      Observations     Federation
```

Model 2 references the stable camera ID for observations.

Model 3 maps vendor/VMS camera identifiers to the stable Trinetra camera ID.

## Important Rules

- Camera IDs are immutable.
- `camera_code` must be unique.
- IP address is not the camera identity.
- Organization ownership and geographic location are separate relationships.
- Coordinates must be validated.
- Units for azimuth, FOV, tilt, range, and height must be documented.
- Raw credentials must never be stored in the camera table.
- Health and maintenance history should be stored separately.
- Vendor-specific configuration belongs in adapter/integration configuration, not the core camera model.
- Deactivation/soft deletion should normally be preferred where historical observations exist.
- Sensitive camera operations must be audited.
