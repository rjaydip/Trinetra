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

- Organization and organization-unit IDs (primary keys) are immutable.
- Organization units cannot form circular parent relationships.
- A child unit must belong to the same organization as its parent. This is enforced by a
  **deferrable** constraint trigger (`trg_org_unit_same_org`, v1.12) so that
  `POST /organization-units/{id}/move` can rewrite a whole subtree's `organization_id` in one
  transaction and have it re-checked, consistently, at commit. A direct single-row `UPDATE` that
  changes `organization_id` while leaving the parent behind still fails immediately.
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
                                             # parentUnitId MAY change (same-org re-parent)
POST /api/v1/organization-units/{id}/activate     # INACTIVE -> ACTIVE
POST /api/v1/organization-units/{id}/move         # re-parent into another organization
POST /api/v1/organization-units/{id}/deactivate
```

`PUT` on an organization or unit replaces its editable fields; `status` is left as-is when
omitted (use `/activate` and `/deactivate`, not a field). Editing an `INACTIVE` unit through
`PUT` is refused (409) — reactivate it first.

`parentUnitId` **may** change through `PUT`: the unit and its whole subtree move under the new
parent **in the same organization** (descendants keep their `parent_unit_id` and move
implicitly). Re-parenting under the unit itself or one of its descendants is refused (400); so is
a parent that is not `ACTIVE` (400) or in a different organization (400). Re-parenting to root
(`parentUnitId: null`) needs `organization.manage` held unscoped (403). The `/deactivate`
`reparent` flow still exists — it is no longer the only way to re-parent.

A unit's `organizationId` is **no longer immutable**: `POST /organization-units/{id}/move`
re-parents a unit under a parent in a different organization and rewrites the whole subtree's
`organization_id` in one transaction. It requires `organization.manage` held unscoped (403
otherwise) and, when access groups have an `ORGANIZATION` scope pointing into the subtree, an
explicit `confirmScopeImpact: true` (409 with the affected groups listed otherwise). Cameras,
VMS targets and historic events reference the unit id and resolve organization through the live
tree, so they follow the move automatically. Editing an organization needs `organization.manage`
held unscoped.
