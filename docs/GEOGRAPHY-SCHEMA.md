# Geographic Schema

## Purpose

Represents the physical location hierarchy of CCTV infrastructure. It is independent from the organizational hierarchy.

A camera has an owner (organization) and a place (geography). These are separate questions and must not be modelled as one tree.

## Geographic Model

```text
Geographic Area
  └── Child Geographic Area   (to any depth)
       └── Camera             (attaches at any level)
```

The hierarchy is **defined by the operator**, not by the schema. There is no fixed bottom tier:
a camera attaches directly to a `geographic_area_id` at whatever level the operator finds
useful — a district, a ward, or a per-junction area they created for the purpose.

## Operator-Defined Levels

There is no fixed State/District/Taluka/Village structure in the database. Each deployment builds the hierarchy that matches how it actually works.

```text
Deployment A                Deployment B
------------                ------------
State                       City
 └── District                └── Zone
      └── Taluka                  └── Ward
           └── Village                 └── Sector
```

Both use the same tables. `area_type` is a label drawn from `geographic_area_types`, each of
which carries a `level_order`: **smaller = broader / higher in the tree** (STATE `10`),
**larger = finer / deeper** (BLOCK `70`). Two rules constrain the tree:

- **No cycles.**
- **Level-order containment** — a child area must sit at a *strictly finer* level than its
  parent (`child.level_order > parent.level_order`). A District cannot be placed inside a
  Village; two areas of the *same* level cannot nest either. Only the ordering is checked, never
  the size of the gap — an operator may skip intervening levels (District straight to Village).
  The check also fires when an area's `area_type` is changed, so a node cannot be relabelled
  coarser than its own children. Violations return `400`.

A deployment that wants arbitrary-depth trees registers its own ordered levels
(`AREA-1`, `AREA-2`, …) rather than reusing one label.

This mirrors the rule in `DEPARTMENT-SCHEMA.md`: use a generic hierarchical entity instead of hardcoding the levels of one organization's structure.

## `geographic_areas`

| Field | Type | Required | Description |
|---|---|---:|---|
| `id` | UUID | Yes | Immutable identifier |
| `parent_area_id` | UUID | No | Parent area. NULL marks a root |
| `code` | VARCHAR(50) | Yes | Area code — unique **within a parent**, not globally (roots unique among themselves) |
| `name` | VARCHAR(255) | Yes | Area name |
| `area_type` | VARCHAR(50) | Yes | FK into `geographic_area_types(code)`. Operator-defined level label |
| `description` | TEXT | No | Free-text operator note, round-tripped by GET/PUT |
| `status` | VARCHAR(20) | Yes | ACTIVE / INACTIVE |
| `metadata` | JSONB | No | Deployment-specific attributes |
| `created_at` | TIMESTAMPTZ | Yes | Creation time |
| `updated_at` | TIMESTAMPTZ | Yes | Last update time |

`code` is unique per parent (`UNIQUE NULLS NOT DISTINCT (parent_area_id, code)`), so "WARD-1"
can recur under every district. Because areas are the finest tier now, tools that resolve an
area by bare `code` must handle an ambiguous match.

There is **no boundary polygon**. Containment is resolved by walking the parent chain, not by
spatial maths, so the hierarchy is sufficient on its own — and carrying a geometry column meant
requiring PostGIS across every deployment for data nobody had populated and no query read. If
administrative polygons are ever needed for map rendering, they belong with Model 1's coverage
work.

## `geographic_area_types`

A registry of the level names a deployment uses, so the UI can render an ordered picker.

| Field | Type | Required | Description |
|---|---|---:|---|
| `code` | VARCHAR(50) | Yes | Level code, e.g. `DISTRICT` — referenced by `geographic_areas.area_type` |
| `name` | VARCHAR(255) | Yes | Display name |
| `level_order` | INTEGER | Yes | Smaller = broader/higher; larger = finer/deeper. Orders the picker **and** enforces containment (a child must be strictly finer than its parent) |
| `description` | TEXT | No | Free-text operator note |
| `status` | VARCHAR(20) | Yes | ACTIVE / INACTIVE |

`v1` shipped this table empty; `v1.11` seeds a baseline (STATE, DIVISION, DISTRICT, TALUKA,
CITY, ZONE, WARD, VILLAGE, SECTOR, BLOCK — in `level_order` gaps of 10) and makes
`geographic_areas.area_type` a required FK into it. An operator adds their own rows for other
level names; a new level's `level_order` decides where it may sit in the tree (see the
containment rule above).

## Cameras attach directly to an area

A camera references a `geographic_area_id` (`NOT NULL`) at **any** level of the hierarchy — there
is no separate "site" table. It does not reference the wider chain; ancestors are resolved by
walking `parent_area_id`.

```text
Camera
  └── geographic_area_id  (any level: a district, a ward, a per-junction area…)
       └── Geographic Area
            └── parent chain
```

A camera keeps its own precise `latitude` / `longitude` — several cameras in one place point in
different directions from different poles — so removing the site tier loses nothing about the
map. A VMS target (`connector_target`) and an event carry an **optional** `geographic_area_id`
for the same purpose; null means "no geographic key", and the scope predicates read that as
"the geography dimension does not constrain this row".

## Example

```text
Gujarat                          (area_type: STATE)
├── Ahmedabad                    (area_type: DISTRICT)
│   ├── Daskroi                  (area_type: TALUKA)
│   │   └── Village X            (area_type: VILLAGE)
│   │        └── Ring Road Jn    (area_type: SECTOR — an area the operator made for the junction)
│   │             ├── Camera CAM-AHM-001245
│   │             └── Camera CAM-AHM-001246
│   └── Sanand                   (area_type: TALUKA)   ← a camera could attach here directly too
└── Surat                        (area_type: DISTRICT)
```

Avoid:

```text
camera
├── state_id
├── district_id
├── taluka_id
└── village_id
```

Duplicating ancestors onto the camera means every hierarchy correction becomes a bulk update across the camera table, and any row that is missed becomes silently wrong.

## Two Independent Dimensions

```text
Organization                    Geography
     |                              |
Police Department               Gujarat
     |                              |
Organization Unit               Ahmedabad
     |                              |
     +-------- Camera -------------+
```

Keeping them separate is what lets the system answer both:

- Which Police cameras are in Ahmedabad?
- Which departments have cameras in this area?

Merging them into one tree makes the second question unanswerable.

## Containment

Containment is resolved by walking parents, typically with a recursive query:

```text
Is CAM-001 inside Ahmedabad District?

CAM-001 → Ring Road Jn
        → Village X
        → Daskroi
        → Ahmedabad District   ← match
```

The hierarchy is the single source of truth. Nothing else stores the answer.

## RBAC Usage

Geographic scope is one of the scope dimensions in `RBAC-LOGICAL-FLOW.md`.

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

A scope names **one** area. Everything beneath it is included by hierarchy resolution, so a scope of "Ahmedabad District" already covers a camera in a village three levels below.

Scope records must never list descendant IDs. Doing so means a newly added village grants nobody access until every affected scope row is found and updated — and nothing reports that it was missed.

## Deactivation

Deactivating an area that still has active child areas is **refused, never guessed**. The request returns `409 Conflict` with the affected children listed, and the operator resubmits with an explicit strategy.

```text
POST /api/v1/geographic-areas/{id}/deactivate
        |
        v
  Active child areas?
        |
   +----+----+
   |         |
   NO       YES
   |         |
   v         v
Deactivate  409 Conflict
            {
              affectedChildren: [...],
              resolutions: ["cascade", "reparent"]
            }
                  |
       +----------+----------+
       |                     |
   cascade               reparent
       |                     |
Deactivate the area     Move direct child areas
and every descendant    to a new parent, then
area                    deactivate this area
```

### Strategies

| Strategy | Effect |
|---|---|
| `cascade` | The area and every descendant area become INACTIVE |
| `reparent` | Direct child areas are moved to `newParentId`, then the area alone becomes INACTIVE. Grandchildren follow their parents |

```http
POST /api/v1/geographic-areas/{id}/deactivate
{ "childStrategy": "cascade" }

POST /api/v1/geographic-areas/{id}/deactivate
{ "childStrategy": "reparent", "newParentId": "…" }
```

`newParentId` is validated before anything moves: it must exist, be ACTIVE, and must not be the area being deactivated or any of its descendants — otherwise reparenting would detach a whole subtree from the root and leave it unreachable.

**Concurrency.** Every deactivation of the geographic hierarchy takes one advisory lock for the whole hierarchy, held until its transaction commits, so two operators cannot interleave two deactivations. Creating or reparenting an area under a non-ACTIVE parent is rejected (`400`, "does not exist or is not ACTIVE"), so the sequential "deactivate an area, then add a child under it" path cannot strand a live node.

One narrow race remains: a *grandchild* created under a still-active mid-tree node in the instant an ancestor is being cascaded can be left ACTIVE under an INACTIVE ancestor. This is a list/report inconsistency only — scope resolution walks the tree ignoring `status`, so no camera or event access is silently gained or lost. `OPERATIONS.md` carries a reconciliation query that finds ACTIVE nodes with an INACTIVE ancestor; re-running the deactivation clears them.

### Why it is not decided automatically

Both silent behaviours are harmful in different directions.

- **Silently cascading** can deactivate hundreds of cameras across a district from a single click, and the operator sees one success message.
- **Silently orphaning** leaves child areas pointing at an inactive parent, so containment queries stop matching and cameras quietly fall out of everyone's scope — with nothing reporting that access was lost.

Making the operator choose turns an invisible side effect into a visible decision. The same rule applies to `organization_units`.

## Important Rules

- Geographic area IDs are immutable.
- Areas cannot form circular parent relationships.
- A child area must be a strictly finer level than its parent (`level_order` containment); changing an `area_type` coarser than an existing child is refused. Both return `400`.
- `area_type` values are defined by the operator (rows in `geographic_area_types`), not fixed by the schema.
- Area codes are unique **within a parent**, not globally.
- Prefer deactivation over destructive deletion when historical records exist.
- Organization must remain a separate hierarchy. `organization_units.geographic_area_id` is a descriptive "home area" label only — never an authorization input (see `DEPARTMENT-SCHEMA.md`).
- Cameras attach directly to a `geographic_area_id` at any level. VMS targets and events carry an optional one.
- Camera coordinates must be validated before storage.
- Deactivating an area with active child areas is refused until the operator chooses `cascade` or `reparent`. Neither outcome is applied silently.
- A reparent target must be active and must not be a descendant of the area being deactivated.
- `PUT /api/v1/geographic-areas/{id}` edits `code` / `name` / `areaType` / `description`; it refuses a `parentAreaId` change (400) — reparenting is the `/deactivate` `reparent` flow.

## API Examples

```http
POST /api/v1/geographic-areas
GET  /api/v1/geographic-areas
GET  /api/v1/geographic-areas/{id}
PUT  /api/v1/geographic-areas/{id}          # edit fields; refuses a parent change

GET  /api/v1/geographic-areas/{id}/children
GET  /api/v1/geographic-areas/{id}/ancestors
GET  /api/v1/geographic-areas/types         # the geographic_area_types registry

# Deactivation. Returns 409 with affectedChildren until a strategy is supplied.
POST /api/v1/geographic-areas/{id}/deactivate
     { "childStrategy": "cascade" }
     { "childStrategy": "reparent", "newParentId": "…" }
```

There is no `/api/v1/sites` — the `sites` table was removed in `v1.11`.

## Relationship With Models 1, 2 and 3

```text
                  GEOGRAPHIC AREA
                         |
                      CAMERA   (attaches at any area level)
                         |
       ┌─────────────────┼─────────────────┐
       |                 |                 |
     MODEL 1           MODEL 2           MODEL 3
   GIS / coverage    Observations      VMS / events
```

- **Model 1** renders coverage and gaps within an area.
- **Model 2** inherits an observation's location from its camera.
- **Model 3** scopes VMS integrations and events, and may attach a connector target to a `geographic_area_id` so an NVR can be geographically scoped in its own right.
