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
| `description` | TEXT | No | Free-text operator note, round-tripped by GET/PUT |
| `geographic_area_id` | UUID | No | **Descriptive "home area" label only** — see below |
| `status` | VARCHAR(20) | Yes | ACTIVE / INACTIVE |
| `metadata` | JSONB | No | Organization-specific attributes |
| `created_at` | TIMESTAMPTZ | Yes | Creation time |
| `updated_at` | TIMESTAMPTZ | Yes | Last update time |

`geographic_area_id` records the area a unit is *based in*, for display and reporting — the way a
mailing address is attached to a department. It is **never an authorization input**: no scope
predicate, `has_permission`, `authorized_org_units` or `authorized_geographic_areas` reads it.
Organization and geography stay independent scope dimensions (see `AUTHORIZATION.md` and
`RBAC-LOGICAL-FLOW.md`). The API validates the area exists and is ACTIVE but does **not** check
the caller's geographic scope to set it — a unit admin with no geography grant may still label
their unit with the area it sits in. Operators who want "show me my unit's cameras" should build
it as a saved geographic filter, not expect this field to scope anything.

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
PUT  /api/v1/organization-units/{id}          # edit fields + description + geographicAreaId;
                                             # refuses a parentUnitId change (400)
POST /api/v1/organization-units/{id}/deactivate
```

`PUT` on an organization or unit replaces its editable fields; `status` is left as-is when
omitted (deactivate through `/deactivate`, not by clearing a field). A unit's `organizationId`
is immutable and a `parentUnitId` change through `PUT` is refused (400) — re-parenting is the
`/deactivate` `reparent` flow. Editing an organization needs `organization.manage` held
unscoped.
