# Geographic Schema

## Purpose

Represents the physical location hierarchy of CCTV infrastructure. It is independent from the organizational hierarchy.

A camera has an owner (organization) and a place (geography). These are separate questions and must not be modelled as one tree.

## Geographic Model

```text
Geographic Area
  └── Child Geographic Area
       └── Site
            └── Camera
```

The hierarchy is **defined by the operator**, not by the schema.

## Operator-Defined Levels

There is no fixed State/District/Taluka/Village structure in the database. Each deployment builds the hierarchy that matches how it actually works.

```text
Deployment A                Deployment B
------------                ------------
State                       City
 └── District                └── Zone
      └── Taluka                  └── Sector
           └── Village                 └── Block
```

Both use the same tables. Neither requires a schema change.

This mirrors the rule in `DEPARTMENT-SCHEMA.md`: use a generic hierarchical entity instead of hardcoding the levels of one organization's structure.

## `geographic_areas`

| Field | Type | Required | Description |
|---|---|---:|---|
| `id` | UUID | Yes | Immutable identifier |
| `parent_area_id` | UUID | No | Parent area. NULL marks a root |
| `code` | VARCHAR(50) | Yes | Unique area code |
| `name` | VARCHAR(255) | Yes | Area name |
| `area_type` | VARCHAR(50) | Yes | Operator-defined level: state, district, taluka, village, zone, ward, etc. |
| `status` | VARCHAR(20) | Yes | ACTIVE / INACTIVE |
| `metadata` | JSONB | No | Deployment-specific attributes |
| `created_at` | TIMESTAMPTZ | Yes | Creation time |
| `updated_at` | TIMESTAMPTZ | Yes | Last update time |

There is **no boundary polygon**. Containment is resolved by walking the parent chain, not by
spatial maths, so the hierarchy is sufficient on its own — and carrying a geometry column meant
requiring PostGIS across every deployment for data nobody had populated and no query read.

A site's position is a plain `latitude` / `longitude` pair with range checks, the same
representation used everywhere else in the schema. If administrative polygons are ever needed for
map rendering, they belong with Model 1's coverage work, which is where spatial querying actually
starts — and they can be added then, against a requirement rather than in anticipation of one.

## `geographic_area_types`

Optional. Lets a deployment declare its own level names and their order, so the UI can render them consistently and the API can reject a district placed inside a village.

| Field | Type | Required | Description |
|---|---|---:|---|
| `code` | VARCHAR(50) | Yes | Level code, e.g. `DISTRICT` |
| `name` | VARCHAR(255) | Yes | Display name |
| `level_order` | INTEGER | Yes | Depth ordering, lower is broader |
| `status` | VARCHAR(20) | Yes | ACTIVE / INACTIVE |

Without this table `area_type` is free text and the hierarchy is unvalidated beyond cycle prevention. With it, level ordering can be enforced.

## `sites`

A site is a physical installation location — a junction, a building, a toll plaza. Cameras are installed at sites; sites sit inside geographic areas.

| Field | Type | Required | Description |
|---|---|---:|---|
| `id` | UUID | Yes | Immutable identifier |
| `code` | VARCHAR(100) | Yes | Unique site code |
| `name` | VARCHAR(255) | Yes | Site name |
| `geographic_area_id` | UUID | Yes | Containing geographic area |
| `site_type` | VARCHAR(50) | No | Junction, building, toll plaza, etc. |
| `address` | TEXT | No | Postal or descriptive address |
| `latitude` | DECIMAL(10,7) | No | Site centre latitude |
| `longitude` | DECIMAL(10,7) | No | Site centre longitude |
| `status` | VARCHAR(20) | Yes | ACTIVE / INACTIVE |
| `metadata` | JSONB | No | Deployment-specific attributes |
| `created_at` | TIMESTAMPTZ | Yes | Creation time |
| `updated_at` | TIMESTAMPTZ | Yes | Last update time |

A site's coordinates describe the location as a whole. A camera keeps its own precise coordinates, because several cameras at one junction point in different directions from different poles.

## Example

```text
Gujarat                          (area_type: STATE)
├── Ahmedabad                    (area_type: DISTRICT)
│   ├── Daskroi                  (area_type: TALUKA)
│   │   └── Village X            (area_type: VILLAGE)
│   │        └── Site: Ring Road Junction
│   │             ├── Camera CAM-AHM-001245
│   │             └── Camera CAM-AHM-001246
│   └── Sanand                   (area_type: TALUKA)
└── Surat                        (area_type: DISTRICT)
```

## Camera Relationship

A camera references a `site_id`. It does not reference the wider hierarchy.

```text
Camera
  └── site_id
       └── Site
            └── geographic_area_id
                 └── Geographic Area
                      └── parent chain
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
- Which departments have cameras at this site?

Merging them into one tree makes the second question unanswerable.

## Containment

Containment is resolved by walking parents, typically with a recursive query:

```text
Is CAM-001 inside Ahmedabad District?

CAM-001 → Site: Ring Road Junction
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

Deactivating an area that still has active children or sites is **refused, never guessed**. The request returns `409 Conflict` with the affected children listed, and the operator resubmits with an explicit strategy.

```text
PUT /api/v1/geographic-areas/{id}   status = INACTIVE
        |
        v
  Active children or sites?
        |
   +----+----+
   |         |
   NO       YES
   |         |
   v         v
Deactivate  409 Conflict
            {
              affectedAreas: [...],
              affectedSites: [...],
              resolutions: ["cascade", "reparent"]
            }
                  |
       +----------+----------+
       |                     |
   cascade               reparent
       |                     |
Deactivate the area     Move direct children and
and every descendant    sites to a new parent,
area and site           then deactivate this area
```

### Strategies

| Strategy | Effect |
|---|---|
| `cascade` | The area, every descendant area, and every site beneath them become INACTIVE |
| `reparent` | Direct child areas and sites are moved to `newParentId`, then the area alone becomes INACTIVE. Grandchildren follow their parents |

```http
PUT /api/v1/geographic-areas/{id}
{ "status": "INACTIVE", "childStrategy": "cascade" }

PUT /api/v1/geographic-areas/{id}
{ "status": "INACTIVE", "childStrategy": "reparent",
  "newParentId": "…", "newAreaIdForSites": "…" }
```

`newParentId` is validated before anything moves: it must exist, be ACTIVE, and must not be the area being deactivated or any of its descendants — otherwise reparenting would detach a whole subtree from the root and leave it unreachable.

**Concurrency.** Every deactivation of the geographic hierarchy takes one advisory lock for the whole hierarchy, held until its transaction commits, so two operators cannot interleave two deactivations (a reparent is a `childStrategy` inside a deactivate, not a standalone operation). Creating or reparenting an area or site under a non-ACTIVE parent is rejected (`400`, "does not exist or is not ACTIVE"), so the sequential "deactivate an area, then add a child under it" path cannot strand a live node.

One narrow race remains: a *grandchild* created under a still-active mid-tree node in the instant an ancestor is being cascaded can be left ACTIVE under an INACTIVE ancestor. This is a list/report inconsistency only — scope resolution walks the tree ignoring `status`, so no camera or event access is silently gained or lost. `OPERATIONS.md` carries a reconciliation query that finds ACTIVE nodes with an INACTIVE ancestor; re-running the deactivation clears them.

### Why it is not decided automatically

Both silent behaviours are harmful in different directions.

- **Silently cascading** can deactivate hundreds of cameras across a district from a single click, and the operator sees one success message.
- **Silently orphaning** leaves child areas pointing at an inactive parent, so containment queries stop matching and cameras quietly fall out of everyone's scope — with nothing reporting that access was lost.

Making the operator choose turns an invisible side effect into a visible decision. The same rule applies to `organization_units`.

## Important Rules

- Geographic area and site IDs are immutable.
- Areas cannot form circular parent relationships.
- `area_type` values are defined by the operator, not fixed by the schema.
- Area and site codes should be unique.
- Prefer deactivation over destructive deletion when historical records exist.
- Organization must remain a separate hierarchy.
- Cameras attach to geography through a site, never directly to an area.
- Coordinates must be validated before storage.
- Deactivating an area with active children or sites is refused until the operator chooses `cascade` or `reparent`. Neither outcome is applied silently.
- A reparent target must be active and must not be a descendant of the area being deactivated.

## API Examples

```http
POST /api/v1/geographic-areas
GET  /api/v1/geographic-areas
GET  /api/v1/geographic-areas/{id}
PUT  /api/v1/geographic-areas/{id}

GET  /api/v1/geographic-areas/{id}/children
GET  /api/v1/geographic-areas/{id}/ancestors
GET  /api/v1/geographic-areas/tree

# Deactivation. Returns 409 with affected children until a strategy is supplied.
PUT  /api/v1/geographic-areas/{id}
     { "status": "INACTIVE", "childStrategy": "cascade" }
     { "status": "INACTIVE", "childStrategy": "reparent", "newParentId": "…" }

POST /api/v1/sites
GET  /api/v1/sites
GET  /api/v1/sites/{id}
PUT  /api/v1/sites/{id}

GET  /api/v1/geographic-areas/{id}/sites
GET  /api/v1/geographic-areas/{id}/cameras
```

## Relationship With Models 1, 2 and 3

```text
                  GEOGRAPHIC AREA
                         |
                       SITE
                         |
                      CAMERA
                         |
       ┌─────────────────┼─────────────────┐
       |                 |                 |
     MODEL 1           MODEL 2           MODEL 3
   GIS / coverage    Observations      VMS / events
```

- **Model 1** renders coverage and gaps within an area.
- **Model 2** inherits an observation's location from its camera.
- **Model 3** scopes VMS integrations and events, and may attach a connector target to a site so an NVR can be geographically scoped in its own right.
