# Department / Organization Schema

## Purpose

Represents the organizational ownership and operational hierarchy of CCTV infrastructure. It is independent from the geographic hierarchy.

## Organization Model

```text
Organization
  └── Organization Unit
       └── Child Organization Unit
```

A department/institution may own cameras across many districts or locations.

## `organizations`

| Field | Type | Required | Description |
|---|---|---:|---|
| `id` | UUID | Yes | Immutable identifier |
| `code` | VARCHAR(50) | Yes | Unique organization code |
| `name` | VARCHAR(255) | Yes | Organization name |
| `organization_type` | VARCHAR(50) | Yes | Department, institution, corporation, etc. |
| `description` | TEXT | No | Description |
| `status` | VARCHAR(20) | Yes | ACTIVE / INACTIVE |
| `created_at` | TIMESTAMPTZ | Yes | Creation time |
| `updated_at` | TIMESTAMPTZ | Yes | Last update time |

## `organization_units`

Use this generic hierarchical entity instead of hardcoding commissionerate/division/zone structures.

| Field | Type | Required | Description |
|---|---|---:|---|
| `id` | UUID | Yes | Immutable identifier |
| `organization_id` | UUID | Yes | Parent organization |
| `parent_unit_id` | UUID | No | Parent organization unit |
| `code` | VARCHAR(50) | Yes | Unit code |
| `name` | VARCHAR(255) | Yes | Unit name |
| `unit_type` | VARCHAR(50) | Yes | Commissionerate, division, zone, unit, etc. |
| `status` | VARCHAR(20) | Yes | ACTIVE / INACTIVE |
| `metadata` | JSONB | No | Organization-specific attributes |
| `created_at` | TIMESTAMPTZ | Yes | Creation time |
| `updated_at` | TIMESTAMPTZ | Yes | Last update time |

## Example

```text
Police Department
├── Ahmedabad Commissionerate
│   ├── Zone 1
│   └── Zone 2
└── Surat Commissionerate
    ├── Zone 1
    └── Zone 2
```

Another organization can have a different structure without changing the schema.

## Camera Relationship

A camera references an `organization_unit_id`.

```text
Organization
  └── Organization Unit
       └── owns / operates
            └── Camera
```

Do not store department names directly in the camera table.

## Important Rules

- Organization and organization-unit IDs are immutable.
- Organization units cannot form circular parent relationships.
- A child unit must belong to the same organization as its parent.
- Prefer deactivation over destructive deletion when historical records exist.
- Organization codes should be unique.
- Geography must remain a separate hierarchy.
- Deactivating a unit takes one advisory lock for the whole unit hierarchy, held until the
  transaction commits, so concurrent deactivations serialize (reparent is a `childStrategy`
  inside deactivate, not a standalone op). Creating or reparenting a unit under a non-ACTIVE
  parent is rejected (`400`), so a live unit cannot be attached under a retired one. One narrow
  race remains — a grandchild created under a still-active mid-tree node in the instant an
  ancestor is cascaded — a list/report inconsistency only (scope resolution ignores `status`);
  `OPERATIONS.md` has the reconciliation query.

## RBAC Usage

An access group can combine a role with an organizational scope:

```text
Group:
  Ahmedabad Police Camera Operators

Role:
  CAMERA_OPERATOR

Organization Scope:
  Police Department
```

Geographic scope can be added independently.

## API Examples

```http
POST /api/v1/organizations
GET  /api/v1/organizations
GET  /api/v1/organizations/{id}
PUT  /api/v1/organizations/{id}

POST /api/v1/organizations/{id}/units
GET  /api/v1/organizations/{id}/units

GET  /api/v1/organization-units/{id}
PUT  /api/v1/organization-units/{id}
```
