# API Plan — Hierarchy editing + Role / Access-group maintenance

Status: **Implemented in `v1.12`** (commit "review point based on front end requirement",
2026-09-08). This document turns the agreed plan items (P1, P2a, P2b, P3, P5–P13) into testable
requirements with acceptance criteria, validation rules, Problem Details responses and audit
expectations. It stays as the contract of record; `docs/IMPL-NOTES-HIERARCHY-RBAC.md` §"As-built
deviations" lists where the shipped code differs from the text below.

Project-owner decisions taken before implementation (override the open-questions table):
Q1 — P2b **built**, restricted to unscoped `organization.manage`, `confirmScopeImpact` flag,
dedicated `POST /organization-units/{id}/move`. Q2 — historic event rows follow the live tree.
Q3 — **`role.read` added** as a real permission (not "ratify group.read"). Q4 — soft-delete
reuses `INACTIVE`, no `ARCHIVED`. Q5 — route stays `/disable`. Q6 — `roles.status` column
default `DRAFT`. Q7 — geography re-parent gets the reach check. Q8 — `usedBy` filtered to
caller-visible groups. Q9 — re-activating an ACTIVE group is an idempotent no-op. Q10 — a scope
add/remove bumps `access_groups.updated_at`. **P8 further:** the `InUse` block is removed
entirely — a role in use can be soft-deleted, its groups are left untouched and simply grant
nothing onward (no `confirmInUse` flag).

Scope: `PUT /api/v1/organization-units/{id}`, `PUT /api/v1/geographic-areas/{id}`, their new
`/activate` routes, and the `/api/v1/roles` + `/api/v1/access-groups` surface.

Reads first (unchanged): `docs/AUTHORIZATION.md`, `docs/RBAC-LOGICAL-FLOW.md`,
`docs/DEPARTMENT-SCHEMA.md`, `docs/GEOGRAPHY-SCHEMA.md`, `docs/MODEL-1-API-PLAN.md`.

Database: **PostgreSQL** (local dev `trinetra` / `trinetra`, per `CLAUDE.md`). All schema
changes land in a **new version file `db/versions/v1.12.sql`** — `v1.11` is the newest applied
file and is never edited once applied (`CLAUDE.md`). No migration tool, no `schema_version`
table; deployment ordering is operational discipline.

---

## 0. Conventions used below

- **Problem Details** = RFC 7807 body via `TypedResults.Problem(title:, detail:, statusCode:)`,
  matching the existing endpoints. Each rule states `status` + `title`.
- **Out-of-scope reads return 404, never 403** (`AUTHORIZATION.md` §5). Kept throughout.
- **A mutation and its audit row share one transaction** (`CLAUDE.md` invariant 10). Every write
  below names its `config_audit` row: `action`, `entity_type`, `entity_id`, `before`, `after`.
- **Escalation guard** = `RBAC-LOGICAL-FLOW.md` §15 / `AUTHORIZATION.md` §5: a caller not
  unscoped for the governing permission may not grant/reach beyond their own hold.
- Acceptance criteria are **Given / When / Then**. "Caller" = authenticated principal (user or
  API key) resolved to a `CallerContext`.

---

## 1. Invariant-conflict register (read this first)

| # | Plan item | Documented rule it changes | Action required |
|---|---|---|---|
| C1 | **P2b** cross-organization move | `DEPARTMENT-SCHEMA.md` "Important Rules": *"A child unit must belong to the same organization as its parent"* and API-examples note *"a unit's `organizationId` is immutable"*. `assert_org_unit_acyclic()` (`v1.sql` L150-155) **raises** when `parent.organization_id <> NEW.organization_id`. | Relax the trigger to allow the parent check to pass when the whole subtree's `organization_id` is being moved in the same statement; rewrite the `DEPARTMENT-SCHEMA.md` rule and the API-examples note. **Project-owner sign-off required** — this is a deliberate reversal of a stated invariant. |
| C2 | **P1 / P2a** direct reparent via PUT | `DEPARTMENT-SCHEMA.md` + `GEOGRAPHY-SCHEMA.md`: *"a `parentUnitId`/`parentAreaId` change through `PUT` is refused (400) — re-parenting is the `/deactivate` `reparent` flow"*. `HierarchyEndpoints.UpdateUnitAsync` / `UpdateAreaAsync` currently return 400 on any parent change. | Remove the 400 guard; add the reparent logic + escalation guard described in P1/P2a. Update both schema docs and both endpoint `WithDescription` strings. The `/deactivate` `reparent` `childStrategy` **stays** (it also deactivates the node); it is no longer the *only* way to reparent. |
| C3 | **P1** must still honour the anti-detach rule | `AUTHORIZATION.md` §5: *"Re-parenting into a subtree being deactivated … Detaches it from the root"*. | Keep the descendant-as-parent rejection (400). It is independent of the deactivate flow. |
| C4 | **P7 / P8** new role states (`DRAFT`, `ARCHIVED`) | `roles.status` CHECK is `('ACTIVE','INACTIVE')` only (`v1.sql` L310-311). | `v1.12.sql` must `DROP` and re-add the CHECK. All authz SQL already joins `roles r … AND r.status = 'ACTIVE'`, so any non-ACTIVE state grants nothing with no function change — confirm during implementation (grep shows L466, L686, L782, L810, L1520, L1609, L1632, L1664). |
| C5 | **P10** `DISABLED` → `INACTIVE` | `access_groups.status` CHECK is `('DRAFT','ACTIVE','DISABLED')` (`v1.sql` L410-411); `RBAC-LOGICAL-FLOW.md` §25 names `DISABLED`; `AccessGroupEndpoints` writes the literal `"DISABLED"`; `docs/API-REVIEW-FINDINGS.md` references it. | `v1.12.sql` re-adds the CHECK with `INACTIVE`; update endpoint literals, `/disable` route semantics, RBAC doc §25, `AUTHORIZATION.md` §5 wording. **Breaking API change** for any client that reads `status`. No production deployment exists (`MEMORY.md`), so acceptable now; note in release notes. |
| C6 | **P12** empty-dimension activation response | `AUTHORIZATION.md` §5 + `AccessGroupEndpoints.ActivateAsync` currently return **403** when a scope dimension is empty and the caller is not unscoped. | Change to **409** + require `confirmUnscoped: true` in the body even for an already-unscoped caller. Update `AUTHORIZATION.md` §5 row and the endpoint. |
| C7 | **P9** `role.read` | Roles/permissions reads are currently gated by `group.read` (`RoleEndpoints`, `AccessGroupEndpoints.ListPermissionsAsync`). | Recommendation in P9 — ratify `group.read`, do **not** add a permission. If owner wants separation, it is a `v1.12` seed + role-grant change. |

Nothing in P3, P5, P6, P11, P13 conflicts with a documented invariant.

---

## P1 — `PUT /api/v1/organization-units/{id}`: direct re-parent (same organization)

### Intent
Allow `parentUnitId` to change through `PUT`, moving the unit **and its whole subtree** under a
new parent in the same organization, without the `/deactivate` flow. Reparent-to-root
(`parentUnitId: null`) is covered here too.

### Functional requirements

| Ref | Requirement | MoSCoW |
|---|---|---|
| P1-R1 | When `request.parentUnitId` differs from the stored value, the endpoint performs a re-parent instead of returning 400. | Must |
| P1-R2 | The subtree is **not** physically rewritten — only the moved node's `parent_unit_id` changes; descendants keep their `parent_unit_id` and move implicitly. | Must |
| P1-R3 | Reject **self-parent** (`parentUnitId == id`) → 400. | Must |
| P1-R4 | Reject **descendant-as-parent** (new parent is inside `org_unit_descendants(id)`) → 400. This is the anti-detach rule (C3). | Must |
| P1-R5 | New parent must **exist and be `ACTIVE`** and be in the **same organization** as the unit → 400 otherwise (same-org enforced by the unchanged part of `assert_org_unit_acyclic`; cross-org is P2b). | Must |
| P1-R6 | The unit being re-parented must itself be `ACTIVE` (see P3) → 409 otherwise. | Must |
| P1-R7 | **Escalation guard:** a caller not unscoped for `organization.manage` must reach **both** the unit (or its current parent) **and** the new parent via `authorized_org_units(…, 'organization.manage')` → 404 if either is unreachable (404 not 403, matching `GetUnitAsync`). Mirrors `OrganizationRepository.DeactivateUnitAsync` which already checks both ends. | Must |
| P1-R8 | Re-parenting to root (`parentUnitId: null`) is allowed **only** for a caller unscoped for `organization.manage` (a root unit "answers to no existing scope" — `AUTHORIZATION.md` §5, already enforced in `UpsertUnitAsync`) → 403. | Must |
| P1-R9 | Field edits (`code`, `name`, `unitType`, `description`, `geographicAreaId`, `status`) continue to apply in the same request; `organizationId` in the body stays ignored (unless P2b). | Must |
| P1-R10 | Concurrency: take the same `pg_advisory_xact_lock(hashtext('federation.organization_units.deactivate'))` used by `DeactivateUnitAsync` before reading/writing, so a re-parent cannot interleave with a deactivation cascade. | Should |

### Validation & error responses (Problem Details)

| Condition | status | title |
|---|---|---|
| `parentUnitId == id` | 400 | `A unit cannot be its own parent` |
| New parent is a descendant of the unit | 400 | `Cannot re-parent a unit beneath itself` |
| New parent not found / not ACTIVE | 400 | `New parent unit is not available` |
| New parent in a different organization (and P2b not in play) | 400 | `A unit cannot move between organizations` |
| Unit not ACTIVE | 409 | `Reactivate the unit first` |
| Caller cannot reach unit or new parent | 404 | *(no body beyond default)* |
| Re-parent to root by a scoped caller | 403 | `Only an unscoped administrator can create a root unit` |
| Unknown id | 404 | — |

### Audit

- One row: `action: "update"`, `entity_type: "organization_unit"`, `entity_id: id`,
  `organization_unit_id: id`.
- `before`: full prior projection (**must include `parentUnitId`**).
- `after`: full new projection.
- If the request both re-parents and edits fields, it is still **one** audit row (one PUT = one
  mutation).

### Edge cases

1. **No-op parent** (`parentUnitId` equals stored value) → treated as a field edit, no reparent
   path, existing `parentIsChanging: false` behaviour (rename not blocked by an INACTIVE current
   parent).
2. **Moved node has INACTIVE descendants** → they stay INACTIVE, move implicitly. No cascade of
   status.
3. **New parent becomes INACTIVE between validation and write** → advisory lock + `FOR UPDATE`
   on the new parent row closes the window; if still raced, the DB trigger does not check
   parent status so the app check is authoritative — acceptable residual, matches the documented
   `5-H2` residual race.
4. **Cycle via a concurrent third re-parent** → `assert_org_unit_acyclic` recursive check fires
   → surfaces as 400 `Would create a circular parent relationship` (map the raised exception).
5. **Unit has cameras / VMS targets / events attached** → unaffected; they reference the unit
   id, which is unchanged. Their `organization_id` for scope resolution is derived by walking
   `parent_unit_id`, so they follow the move automatically. (This is the key difference from
   P2b.)

### Acceptance criteria

```
P1-AC1  Same-org reparent, unscoped caller
  Given an ACTIVE unit U under parent A, and an ACTIVE unit B in the same organization,
        B is not a descendant of U,
        and a caller unscoped for organization.manage
  When  the caller PUTs U with parentUnitId = B
  Then  the response is 200 with the updated unit (parentUnitId = B)
  And   one config_audit row is written with before.parentUnitId = A, after.parentUnitId = B
  And   every camera under U resolves its organization scope through B on the next request.

P1-AC2  Descendant-as-parent is refused
  Given an ACTIVE unit U with a descendant D
  When  a caller PUTs U with parentUnitId = D
  Then  the response is 400, title "Cannot re-parent a unit beneath itself"
  And   no audit row is written and U is unchanged.

P1-AC3  Self-parent is refused
  Given an ACTIVE unit U
  When  a caller PUTs U with parentUnitId = U.id
  Then  the response is 400, title "A unit cannot be its own parent".

P1-AC4  Scoped caller cannot reach the destination
  Given a caller scoped for organization.manage to unit-subtree X only,
        an ACTIVE unit U in X, and an ACTIVE unit B outside X in the same organization
  When  the caller PUTs U with parentUnitId = B
  Then  the response is 404 (not 403)
  And   U is unchanged.

P1-AC5  Reparent onto an INACTIVE parent is refused
  Given an ACTIVE unit U and an INACTIVE unit B
  When  a caller PUTs U with parentUnitId = B
  Then  the response is 400, title "New parent unit is not available".

P1-AC6  Reparent to root by a scoped caller is refused
  Given a scoped (not unscoped) organization.manage caller and an ACTIVE non-root unit U
  When  the caller PUTs U with parentUnitId = null
  Then  the response is 403, title "Only an unscoped administrator can create a root unit".

P1-AC7  Concurrent deactivation of an ancestor
  Given unit U being re-parented and, concurrently, an ancestor of U's *old* parent being
        deactivated with childStrategy cascade
  When  both transactions run
  Then  they serialize on the hierarchy advisory lock; the final state is consistent
        (U is either moved before the cascade — and thus escapes it — or after — and the
        cascade did not reach U's new location); no cycle and no orphan results.
```

---

## P2a — `PUT /api/v1/geographic-areas/{id}`: direct re-parent

### Intent
Allow `parentAreaId` to change through `PUT`. Cycle **and** level-order containment are already
enforced by `trg_geo_area_acyclic` (`v1.11` — fires on `parent_area_id` and `area_type`
changes). The endpoint's job is to stop returning a blanket 400 and to map the trigger's
`RAISE EXCEPTION`s to clean 400s.

### Functional requirements

| Ref | Requirement | MoSCoW |
|---|---|---|
| P2a-R1 | When `request.parentAreaId` differs from stored, perform a re-parent (call `UpsertAreaAsync(… parentIsChanging: true)`). | Must |
| P2a-R2 | Rely on `trg_geo_area_acyclic` for: self-parent, cycle, `child.level_order > parent.level_order`. Map each `RAISE EXCEPTION` to **400** with a specific title (below). | Must |
| P2a-R3 | New parent must exist and be `ACTIVE` → 400 (`GeographyRepository.RequireActiveParentAsync` equivalent — confirm it exists for areas; if not, add it). | Must |
| P2a-R4 | Descendant-as-parent → 400 (explicit app check against `geographic_area_descendants(id)`; do not rely only on the cycle trigger, so the message is meaningful). | Must |
| P2a-R5 | Area being re-parented must be `ACTIVE` (see P3) → 409. | Must |
| P2a-R6 | **Escalation guard:** geography writes are gated by `geography.manage`. A caller not unscoped for `geography.manage` must reach both the area and the new parent via `authorized_geographic_areas(…, 'geography.manage')` → 404 otherwise. *(Note: current `GeographyRepository` does not appear to run a reach check on area writes the way `OrganizationRepository` does — confirm and add for parity; this is an open question, see below.)* | Must |
| P2a-R7 | Re-parent to root (`parentAreaId: null`) — only a caller unscoped for `geography.manage` (a root area "answers to no existing scope") → 403. | Must |
| P2a-R8 | Per-parent code uniqueness (`geographic_areas_parent_code_key`, `NULLS NOT DISTINCT`) — moving an area whose `code` collides under the new parent → 409. | Must |
| P2a-R9 | `areaType` may change in the same request; the trigger re-checks it against the new parent **and** existing children. | Must |

### Validation & error responses

| Condition | status | title |
|---|---|---|
| Self-parent (trigger) | 400 | `An area cannot be its own parent` |
| Cycle (trigger) | 400 | `Would create a circular parent relationship` |
| `area_type` not strictly finer than new parent (trigger) | 400 | `Area level must be finer than its parent` |
| `area_type` not strictly coarser than an existing child (trigger) | 400 | `Area level conflicts with a child area` |
| New parent not found / not ACTIVE | 400 | `New parent area is not available` |
| New parent is a descendant | 400 | `Cannot re-parent an area beneath itself` |
| `code` duplicate under new parent | 409 | `Area code already exists under that parent` |
| Area not ACTIVE | 409 | `Reactivate the area first` |
| Caller cannot reach area or new parent | 404 | — |
| Re-parent to root by scoped caller | 403 | `Only an unscoped administrator can create a root area` |

### Audit

- One row: `action: "update"`, `entity_type: "geographic_area"`, `entity_id: id`,
  `organization_unit_id: null`.
- `before` / `after` full projections, **including `parentAreaId` and `areaType`**.

### Edge cases

1. `areaType` unchanged but `parentAreaId` changes to a parent at the same or finer level →
   trigger raises → 400. The client must also send a coarser-parent or a finer `areaType`.
2. Area has cameras / targets / events attached → they reference `geographic_area_id`
   directly; containment for scope is resolved by walking `parent_area_id`, so they follow the
   move. No row rewrite. (Same as P1.)
3. Moving a subtree across the tree such that a **grandchild** now violates containment against
   *its* parent — not possible: only the moved node's parent link changes and only the moved
   node's level is compared to the new parent; the subtree's internal level relationships are
   unchanged and were already valid.
4. Trigger fires `BEFORE UPDATE OF parent_area_id, area_type` — a PUT that changes neither does
   not invoke it (existing `parentIsChanging: false` path).

### Acceptance criteria

```
P2a-AC1  Valid reparent
  Given an ACTIVE area "Ward 3" (level WARD, order 60) under "Zone A" (ZONE, 50),
        and an ACTIVE area "Zone B" (ZONE, 50)
  When  a caller with reach PUTs "Ward 3" with parentAreaId = "Zone B"
  Then  the response is 200 and the area's parentAreaId is "Zone B"
  And   one config_audit row records before.parentAreaId = "Zone A".

P2a-AC2  Level-order violation is a clean 400
  Given an ACTIVE area "Ahmedabad" (DISTRICT, order 30)
        and an ACTIVE area "Village X" (VILLAGE, order 60)
  When  a caller PUTs "Ahmedabad" with parentAreaId = "Village X"
  Then  the response is 400, title "Area level must be finer than its parent"
  And   no 500 leaks the raw trigger message
  And   the area is unchanged.

P2a-AC3  Descendant-as-parent
  Given area A with descendant D
  When  a caller PUTs A with parentAreaId = D
  Then  the response is 400, title "Cannot re-parent an area beneath itself".

P2a-AC4  Code collision under the new parent
  Given "WARD-1" exists under both "Zone A" and "Zone B", and A = the one under Zone A
  When  a caller PUTs A with parentAreaId = "Zone B"
  Then  the response is 409, title "Area code already exists under that parent".

P2a-AC5  Reparent an INACTIVE area
  Given an INACTIVE area A
  When  a caller PUTs A with a new parentAreaId
  Then  the response is 409, title "Reactivate the area first".
```

---

## P2b — `PUT /api/v1/organization-units/{id}`: cross-organization move (subtree cascade)

> **This item reverses a documented invariant (C1). It must not ship without project-owner
> sign-off.** The requirements below assume approval and describe the target behaviour.

### Intent
Moving a unit to a parent in a **different organization** moves the unit **and its whole
subtree** to that organization: every descendant's `organization_id` is rewritten to the new
organization in one transaction.

### Why every downstream effect must be enumerated
`organization_id` on `organization_units` is the anchor for the **organization scope
dimension**. Resources (cameras, VMS targets, events) do **not** store `organization_id` — they
store `organization_unit_id` and scope resolution walks up `parent_unit_id` to the unit, then
reads `organization_id`. So the blast radius of a cross-org move is: everything whose scope is
computed from a unit in the moved subtree.

### Downstream-effect matrix — what the requirement says about each

| Affected thing | Mechanism | Requirement |
|---|---|---|
| **The moved unit + descendants** | `organization_id` rewritten for all of `org_unit_descendants(id)` in one `UPDATE`. | P2b-R1. `updated_at = now()` on every rewritten row. |
| **`assert_org_unit_acyclic` trigger** | Currently raises when `parent.organization_id <> NEW.organization_id`. Fires per-row `BEFORE UPDATE OF parent_unit_id, organization_id`. | P2b-R2. Relax: the same-org check passes when `NEW.organization_id` equals the *new parent's* `organization_id` (i.e. the moved node is landing correctly). Descendant rows updated in the same statement each satisfy it because their parent is also being moved. **Order matters** — update parent before children, or defer the constraint; recommend a single recursive `UPDATE … FROM org_unit_descendants` and verifying the trigger tolerates statement-level ordering, else move the check to a `DEFERRABLE INITIALLY DEFERRED` constraint trigger. Flag for the .NET/pg reviewer. |
| **Access groups scoped to a unit in the subtree** (`scopes.scope_type = 'ORGANIZATION'`, `organization_unit_id` in subtree) | Scope rows reference `organization_unit_id`, not `organization_id`. They are **not** rewritten. After the move, that scope now grants access inside the **new** organization. | P2b-R3. **This is a silent privilege shift.** Requirement: the endpoint must (a) detect every access group with an ORGANIZATION scope pointing into the moved subtree, (b) include them in the response as `affectedGroups: [{id, code, memberCount}]`, (c) record them in the audit `after` snapshot, and (d) require an explicit `confirmScopeImpact: true` in the body when the list is non-empty → **409** `Move affects existing access-group scopes` otherwise. |
| **Users' "home unit"** — there is **no** `platform_users.home_unit`; users belong to groups, not units. | N/A | P2b-R4. No user record changes. Effective permissions change only via P2b-R3's groups. Document that "user home unit" is not a concept in this schema (the plan item's phrasing predates the current model). |
| **`organization_units.geographic_area_id`** ("home area" label) | Descriptive only (invariant 12), never scope. | P2b-R5. Left as-is on every moved row. Not validated against the new org. |
| **Cameras** (`cameras.organization_unit_id` in subtree) | No `organization_id` column. Scope resolves via the unit. | P2b-R6. No camera row changes. Their organization scope **follows the move automatically**. The response `summary` must report `camerasFollowing: <count>` so the operator sees the magnitude. |
| **VMS targets** (`connector_target.organization_unit_id`) | Same as cameras. | P2b-R7. No row change; `targetsFollowing: <count>` in the response. Credentials, leases, connector workers unaffected (they key on `target_id`). |
| **Events / detections** (`federation_event`, `detection_event`) | Carry a denormalised `organization_unit_id` (set at ingest) and optional `geographic_area_id`. Historic rows keep the unit id. | P2b-R8. Historic event rows are **not** rewritten. Their organization-scope answer changes the moment the unit's `organization_id` changes, because `authorized_org_units` walks the current tree. Requirement: **document this as intended** — an investigator querying a moved unit's history will see it under the new organization. If the owner wants history to stay with the old org, that needs a design decision (freeze `organization_id` onto event rows) — **open question**. |
| **`camera_status_history`** | Denormalised `organization_unit_id` + `geographic_area_id`, trigger-populated. | P2b-R9. Not rewritten; new rows carry the current unit id (unchanged) and resolve to the new org. |
| **Audit rows already written** (`config_audit.organization_unit_id`) | Point at the unit id, which does not change. | P2b-R10. Untouched. The move itself is a new audit row. |
| **API keys** bound to an affected group | Key acts through its group; the group's scope now covers the new org. | P2b-R11. Covered by P2b-R3's `confirmScopeImpact` gate — the affected-groups list is where a key's group would surface. Add `apiKeysAffected: <count>` to the response for visibility. |
| **The old organization** | Loses the subtree. May become empty. | P2b-R12. Allowed to become empty; not auto-deactivated. |
| **Root-unit rule** | The moved unit is not becoming a root; it gets a real parent in the new org. | P2b-R13. `parentUnitId` is **required** for a cross-org move (cannot move to "root of another org" — that is a scoped caller manufacturing territory; `AUTHORIZATION.md` §5). |

### Authorization requirements

| Ref | Requirement |
|---|---|
| P2b-A1 | Cross-org move requires the caller be **unscoped for `organization.manage`** (matches "a scoped caller creating a root org unit … answers to no existing scope" — a cross-org move places a subtree under an org the caller may have no standing in). Scoped callers → **403** `Cross-organization moves require unscoped organization.manage`. |
| P2b-A2 | Even for an unscoped caller, both the source unit and the destination parent must resolve (exist, ACTIVE) → 400/404 as in P1. |
| P2b-A3 | The `confirmScopeImpact` gate (P2b-R3) applies regardless of caller privilege. |

### Validation & error responses

| Condition | status | title |
|---|---|---|
| New parent in a different org, caller is scoped | 403 | `Cross-organization moves require unscoped organization.manage` |
| Cross-org move with `parentUnitId: null` | 400 | `A cross-organization move needs a destination parent` |
| Affected access groups exist and `confirmScopeImpact` not `true` | 409 | `Move affects existing access-group scopes` — body carries `affectedGroups`, `camerasFollowing`, `targetsFollowing`, `apiKeysAffected` |
| Destination parent not found / not ACTIVE | 400 | `New parent unit is not available` |
| Self / descendant as parent | 400 | as P1 |
| Source unit not ACTIVE | 409 | `Reactivate the unit first` |

### Audit

- **One** row: `action: "update"`, `entity_type: "organization_unit"`, `entity_id: id`,
  `organization_unit_id: id`.
- `before`: `{ parentUnitId, organizationId, subtreeUnitIds: [...], subtreeSize }`.
- `after`: `{ parentUnitId, organizationId, subtreeUnitIds: [...], affectedGroups: [...],
  camerasFollowing, targetsFollowing, apiKeysAffected, confirmScopeImpact: true }`.
- Rationale: one PUT is one operator decision; the snapshot must make the blast radius
  reconstructable years later (`AUTHORIZATION.md` §6 — snapshots, not diffs).
- **Do not** write a per-descendant audit row (could be thousands); the subtree id list in the
  snapshot is the record.

### Edge cases

1. **Destination org is INACTIVE** → 400 `New parent unit is not available` (the parent's org
   must be ACTIVE too — add the check).
2. **Subtree contains an INACTIVE unit** → its `organization_id` is still rewritten (so a later
   reactivation lands it in the right org); status is untouched.
3. **A `code` collision** — `organization_units.code` is globally `UNIQUE` (`v1.sql` L61), not
   per-org, so a move cannot collide on code. (If a future change makes code per-org, revisit.)
4. **Concurrent camera create under a moving unit** → advisory lock serializes; the camera
   lands under the unit and resolves to whichever org won.
5. **Very large subtree** (say 5,000 units) → single `UPDATE` over `org_unit_descendants`;
   acceptable, wrap in the request transaction. Note the statement takes row locks on the whole
   subtree for the transaction's life — document as an operator-scheduled action, not a
   hot-path edit.

### Acceptance criteria

```
P2b-AC1  Happy path, no affected groups
  Given ACTIVE unit U (subtree of 3 units, 12 cameras, 1 VMS target) in organization ORG-A,
        an ACTIVE destination unit P in organization ORG-B,
        no access group has an ORGANIZATION scope pointing into U's subtree,
        and an unscoped organization.manage caller
  When  the caller PUTs U with parentUnitId = P (and organizationId ORG-B or ignored)
  Then  the response is 200 with summary { subtreeSize: 3, camerasFollowing: 12,
        targetsFollowing: 1, affectedGroups: [] }
  And   all 3 units now have organization_id = ORG-B
  And   the 12 cameras and 1 target are unchanged in the database
  And   the 12 cameras now resolve their organization scope under ORG-B
  And   exactly one config_audit row is written.

P2b-AC2  Affected group requires confirmation
  Given the same as AC1 but one ACTIVE access group G has an ORGANIZATION scope on a unit
        inside U's subtree
  When  the caller PUTs U with parentUnitId = P and no confirmScopeImpact
  Then  the response is 409, title "Move affects existing access-group scopes"
  And   the body lists G with its memberCount
  And   nothing is changed.
  When  the caller re-sends with confirmScopeImpact = true
  Then  the response is 200
  And   the audit after-snapshot includes affectedGroups: [G].

P2b-AC3  Scoped caller is refused
  Given a caller scoped (not unscoped) for organization.manage
  When  the caller attempts any cross-organization move
  Then  the response is 403, title
        "Cross-organization moves require unscoped organization.manage".

P2b-AC4  Cross-org to root is refused
  Given an unscoped caller
  When  the caller PUTs U with parentUnitId = null and a body organizationId = ORG-B
  Then  the response is 400, title "A cross-organization move needs a destination parent".

P2b-AC5  Trigger still blocks a genuine mixed-org tree
  Given the relaxed trigger
  When  a single unit's organization_id is changed to ORG-B while its parent stays in ORG-A
        (i.e. not a subtree move — a direct malformed UPDATE)
  Then  the trigger still raises and the write fails.
```

---

## P3 — PUT requires ACTIVE; new `/activate` routes

### Intent
`PUT` on a unit or area edits are refused when the entity is `INACTIVE` (operator must
reactivate first). Add `POST …/activate` for both hierarchies.

### Functional requirements

| Ref | Requirement | MoSCoW |
|---|---|---|
| P3-R1 | `PUT /api/v1/organization-units/{id}` and `PUT /api/v1/geographic-areas/{id}` return **409** `Reactivate first` when the stored `status = 'INACTIVE'`, **before** applying any field or parent change. | Must |
| P3-R2 | `POST /api/v1/organization-units/{id}/activate` — sets `status = 'ACTIVE'`. Permission `organization.manage`. | Must |
| P3-R3 | `POST /api/v1/geographic-areas/{id}/activate` — sets `status = 'ACTIVE'`. Permission `geography.manage`. | Must |
| P3-R4 | Activation is **refused (409)** if the entity's parent is `INACTIVE` — activating a child under a dead parent recreates the orphan the deactivate flow exists to prevent (`DEPARTMENT-SCHEMA.md` / `GEOGRAPHY-SCHEMA.md`). Title `Parent is inactive`. | Must |
| P3-R5 | Activating an already-ACTIVE entity is idempotent → **200/204**, no audit row, no error. | Should |
| P3-R6 | Activation does **not** cascade to children — each child is activated explicitly. Response/doc must say so. | Must |
| P3-R7 | Escalation guard: caller must reach the entity (and, for a root, be unscoped) — same rule as the deactivate route. Out-of-reach → 404. | Must |
| P3-R8 | `status` in a `PUT` body continues to be **ignored for lifecycle** (kept as-is / `?? prior.Status`) — activation is only via `/activate`, deactivation only via `/deactivate`. Reaffirm in the endpoint descriptions. | Must |

### Validation & error responses

| Condition | status | title |
|---|---|---|
| PUT on an INACTIVE unit/area | 409 | `Reactivate the {unit\|area} first` |
| `/activate` when parent is INACTIVE | 409 | `Parent is inactive` |
| `/activate` on unknown / out-of-reach id | 404 | — |
| `/activate` on a root by a scoped caller | 403 | `Only an unscoped administrator can activate a root {unit\|area}` |
| `/activate` already ACTIVE | 204 | *(no body, idempotent)* |

### Audit

- `/activate` success that changes state: one row, `action: "update"`,
  `entity_type: "organization_unit"` / `"geographic_area"`, `before: { status: "INACTIVE" }`,
  `after: { status: "ACTIVE" }`. `organization_unit_id: id` for units, `null` for areas
  (matches the deactivate helper).
- Idempotent no-op: **no** audit row.

### Edge cases

1. **Grandparent INACTIVE, parent ACTIVE** → activation allowed (only the direct parent is
   checked, matching `RequireActiveParentAsync`). The `OPERATIONS.md` reconciliation query
   already covers ACTIVE-under-INACTIVE-ancestor drift.
2. **Reactivating a unit whose organization is INACTIVE** → 409 `Parent is inactive`
   (treat the organization as the parent for a root unit).
3. **Concurrent activate + deactivate** → both take the hierarchy advisory lock; last writer
   wins, both audited.
4. A unit deactivated as part of a `cascade` then individually reactivated → allowed if its
   parent is (still or again) ACTIVE; leaves siblings INACTIVE.

### Acceptance criteria

```
P3-AC1  PUT on inactive is refused
  Given an INACTIVE unit U
  When  a caller PUTs U with a new name
  Then  the response is 409, title "Reactivate the unit first"
  And   U is unchanged.

P3-AC2  Activate under an active parent
  Given an INACTIVE unit U whose parent A is ACTIVE
  When  a caller with reach POSTs /organization-units/U/activate
  Then  the response is 204
  And   U.status is ACTIVE
  And   one config_audit row records status INACTIVE -> ACTIVE.

P3-AC3  Activate under an inactive parent is refused
  Given an INACTIVE area X whose parent Z is INACTIVE
  When  a caller POSTs /geographic-areas/X/activate
  Then  the response is 409, title "Parent is inactive"
  And   X is unchanged.

P3-AC4  Activation does not cascade
  Given an INACTIVE unit U with INACTIVE children
  When  U is activated
  Then  U is ACTIVE and every child is still INACTIVE.

P3-AC5  Idempotent
  Given an ACTIVE unit U
  When  a caller POSTs /organization-units/U/activate
  Then  the response is 204 and no audit row is written.
```

---

## P5 — `GET /api/v1/roles?includeInactive=` (default active-only)

### Functional requirements

| Ref | Requirement | MoSCoW |
|---|---|---|
| P5-R1 | `GET /api/v1/roles` defaults to returning only roles with `status = 'ACTIVE'`. **Behaviour change** — `RoleRepository.ListAsync` currently returns every role regardless of status. | Must |
| P5-R2 | `?includeInactive=true` returns all roles (ACTIVE + INACTIVE + any P7/P8 states). | Must |
| P5-R3 | With P7, "active-only" means `status = 'ACTIVE'` — `DRAFT` and `ARCHIVED` roles are hidden unless `includeInactive=true`. Consider renaming the param to `?status=active|all|<state>` — **recommend** the simple boolean now (matches `cameras?includeRetired`), a `status` filter later if needed. | Should |
| P5-R4 | Permission unchanged: `group.read` (see P9). | Must |
| P5-R5 | Ordering unchanged (`ORDER BY code`). | Must |

### Error responses
- `includeInactive` not a boolean → 400 `Invalid query parameter` (or clamp to false — pick
  clamp, matches the codebase's "clamp don't error" habit for list params; **recommend clamp**).

### Audit
None (read).

### Acceptance criteria

```
P5-AC1  Default hides inactive
  Given roles R1 (ACTIVE) and R2 (INACTIVE)
  When  a group.read caller GETs /api/v1/roles
  Then  the response contains R1 and not R2.

P5-AC2  includeInactive shows all
  When  the caller GETs /api/v1/roles?includeInactive=true
  Then  the response contains both R1 and R2.

P5-AC3  DRAFT hidden by default (with P7)
  Given a newly created role R3 in DRAFT
  When  the caller GETs /api/v1/roles
  Then  R3 is absent
  And   GET /api/v1/roles?includeInactive=true includes R3.
```

---

## P6 — `GET /api/v1/roles/{id}` detail enrichment

### Functional requirements

| Ref | Requirement | MoSCoW |
|---|---|---|
| P6-R1 | The single-role response gains `createdAt`, `updatedAt`, `customizedAt` (nullable; distinct from the existing boolean `customized`). Source columns exist (`roles.created_at`, `updated_at`, `customized_at`). | Must |
| P6-R2 | Add `usageCount` = number of access groups referencing the role (`SELECT count(*) FROM access_groups WHERE role_id = @id`). | Must |
| P6-R3 | Add `usedBy: [{ id, code, name, status }]` — the access groups on this role. Cap at, say, 200 with a `usedByTruncated: bool`; a role on more than 200 groups is pathological but must not break the response. | Should |
| P6-R4 | `permissions` changes from `string[]` (codes) to `[{ code, name, category }]` — join `role_permissions` → `permissions`. Keep a flat `permissionCodes: string[]` too, so existing clients (and the escalation-guard logic) do not break. | Must |
| P6-R5 | This enrichment is **`GET /{id}` only**. `GET /` (list) keeps the lean shape for payload size — or optionally also carries `usageCount` (cheap, one subquery). **Recommend** list carries `usageCount` only. | Should |
| P6-R6 | `usedBy` is **not** scope-filtered by the caller — roles are global and already readable by any `group.read` holder; showing which groups use one leaks only group codes/names the same holder can already list where in reach. **Open question**: should `usedBy` be filtered to groups the caller can see (`GroupVisiblePredicate`)? Recommend **yes, filter it** for consistency with the access-group surface, with `usageCount` remaining a true total. | Should |

### Response shape (illustrative)

```
GET /api/v1/roles/{id}
200 {
  id, code, name, description, isSystem, status,
  customized: bool, customizedAt: DateTimeOffset|null,
  createdAt, updatedAt,
  usageCount: int,
  usedBy: [ { id, code, name, status } ],   // caller-visible groups only (P6-R6)
  usedByTruncated: bool,
  permissionCodes: [ "camera.read", ... ],
  permissions: [ { code, name, category } ]
}
```

### Error responses
- Unknown id → 404 (unchanged).

### Audit
None (read).

### Acceptance criteria

```
P6-AC1  Timestamps present
  Given a preset role edited once
  When  a caller GETs /api/v1/roles/{id}
  Then  createdAt, updatedAt and customizedAt are all populated
  And   customizedAt equals the time of the edit.

P6-AC2  Usage count and list
  Given role R referenced by ACTIVE groups G1, G2 and DISABLED group G3
  When  a caller who can see G1 and G2 (but not G3) GETs /api/v1/roles/{R}
  Then  usageCount is 3
  And   usedBy contains G1 and G2 only.

P6-AC3  Permission metadata
  When  a caller GETs a role granting camera.read
  Then  permissions contains { code: "camera.read", name: "...", category: "camera" }
  And   permissionCodes contains "camera.read".

P6-AC4  Unused role
  Given a custom role on no groups
  Then  usageCount is 0 and usedBy is [].
```

---

## P7 — Role `DRAFT` state

### Intent
Lifecycle `DRAFT → ACTIVE → INACTIVE`. New roles default to `DRAFT`. A `DRAFT` role grants
nothing.

### Functional requirements

| Ref | Requirement | MoSCoW |
|---|---|---|
| P7-R1 | `v1.12.sql`: `roles.status` CHECK becomes `('DRAFT','ACTIVE','INACTIVE')` (+ `ARCHIVED` if P8 chooses that token). `DROP CONSTRAINT` then `ADD CONSTRAINT` (never edit `v1.sql`). | Must |
| P7-R2 | `roles.status` **default stays `ACTIVE`** at the DB level (existing seed roles and migrations rely on it); the **API** sets `DRAFT` on create unless the body says otherwise, mirroring how `access_groups` is `DRAFT` by default in code while the column default is also `DRAFT`. Actually — align them: set the **column default to `DRAFT`** and have the v1.12 seed/backfill leave existing rows ACTIVE. Confirm with reviewer. | Must |
| P7-R3 | `POST /api/v1/roles` creates the role as `DRAFT` unless `status` explicitly provided; `RoleRepository.CreateAsync` already accepts `status` (`COALESCE(@status,'ACTIVE')` → change to `'DRAFT'`). | Must |
| P7-R4 | A `DRAFT` role **grants nothing**: every authz function joins `roles r … AND r.status = 'ACTIVE'`, so this holds with **no function change**. Confirm by test (sabotage-check: flip a group's role to DRAFT, assert its members lose the permissions). | Must |
| P7-R5 | Allowed transitions via `PUT` `status`: `DRAFT→ACTIVE`, `ACTIVE→INACTIVE`, `INACTIVE→ACTIVE`, `DRAFT→INACTIVE`. Disallow `ACTIVE→DRAFT` and `INACTIVE→DRAFT` (a role that was ever live should not pretend to be a draft) → **409** `Cannot return a role to DRAFT`. | Should |
| P7-R6 | Activating a `DRAFT` role that an access group **already references** is allowed (the group may have been built against the draft); the group only starts granting when **both** group and role are ACTIVE. | Must |
| P7-R7 | The escalation guard runs on create even for a `DRAFT` role (a draft that would carry permissions the caller lacks must still be refused — otherwise the caller drafts it, gets someone unscoped to activate it, and is mid-way to escalation). Current `CreateAsync` already checks. | Must |
| P7-R8 | `SUPER_ADMIN` and other presets are **not** forced through DRAFT — they are seeded ACTIVE and stay ACTIVE unless explicitly disabled. | Must |

### Validation & error responses

| Condition | status | title |
|---|---|---|
| `status` not in the vocabulary | 400 | `Unknown role status` |
| Transition to `DRAFT` from `ACTIVE`/`INACTIVE` | 409 | `Cannot return a role to DRAFT` |
| (unchanged) preset edit while scoped | 409 | `Preset edits require unscoped role.manage` |

### Audit
- Create: existing `create`/`role` row; `after` includes `status: "DRAFT"`.
- Status change via PUT: existing `update`/`role` row; `before.status` / `after.status` must be
  in the snapshot (currently `after` is the raw request — ensure `status` is captured).

### Acceptance criteria

```
P7-AC1  New role is DRAFT and inert
  Given a caller with role.manage
  When  they POST /api/v1/roles with a permission set and no status
  Then  the role is created with status DRAFT
  And   an access group put on that role, then activated, grants none of its permissions
        while the role is DRAFT.

P7-AC2  Activate makes it live
  When  the caller PUTs the role with status ACTIVE
  Then  members of an ACTIVE group on that role now hold its permissions.

P7-AC3  No going back to DRAFT
  Given an ACTIVE role
  When  a caller PUTs it with status DRAFT
  Then  the response is 409, title "Cannot return a role to DRAFT".

P7-AC4  Escalation guard on a draft
  Given a caller who does not hold vms.update and is not unscoped for role.manage
  When  they POST a role including vms.update
  Then  the response is 403, title "Would grant more than you hold"
  And   no DRAFT role is created.
```

---

## P8 — Role soft-delete

### Intent
`DELETE /api/v1/roles/{id}` stops removing rows; it sets the role to a non-granting terminal
state. Presets still 409.

### Design decision (recommend)
Use **`status = 'INACTIVE'`** for soft-delete, **not** a new `ARCHIVED` token. Reasons:
- `INACTIVE` already grants nothing and is already understood everywhere.
- Fewer states to reason about (aligns with `MEMORY.md` "user prefers less machinery").
- The DELETE verb's intent ("make it go away") is served by INACTIVE + hidden-by-default in P5.

If the owner wants to distinguish "disabled but may return" from "deleted", add `ARCHIVED` as a
second terminal state; the requirements below use `INACTIVE` and note where `ARCHIVED` differs.

### Functional requirements

| Ref | Requirement | MoSCoW |
|---|---|---|
| P8-R1 | `DELETE /api/v1/roles/{id}` sets `status = 'INACTIVE'` (was `DELETE FROM roles`). Returns **204**. | Must |
| P8-R2 | A **preset** (`is_system`) still **cannot be deleted** → 409 `Preset roles cannot be deleted` (unchanged). Presets are disabled via `PUT status=INACTIVE` with unscoped `role.manage`. | Must |
| P8-R3 | `SUPER_ADMIN` → 409 `SUPER_ADMIN cannot be edited` (unchanged). | Must |
| P8-R4 | **The `InUse` check is removed for soft-delete.** A custom role referenced by access groups **can** be soft-deleted; those groups keep the FK but the role grants nothing (status not ACTIVE). The response body must warn: `affectedGroups: [{id, code, memberCount}]` and require `confirmInUse: true` when non-empty → **409** `Role is in use` otherwise. (Keeps the operator-visible-decision principle from `GEOGRAPHY-SCHEMA.md`.) | Must |
| P8-R5 | Soft-deleting an already-INACTIVE role is idempotent → 204, no audit row. | Should |
| P8-R6 | No hard-delete endpoint. `role_permissions` rows are **kept** (so a reactivate restores the composition). | Must |
| P8-R7 | Escalation guard: a scoped `role.manage` holder may soft-delete only a **custom** role and only if they hold every permission it carries (same rule as edit). Unscoped exempt. | Must |
| P8-R8 | Scoped `role.manage` holder soft-deleting any preset → 409 `Preset roles cannot be deleted` (they can't delete presets at all; disabling a preset needs unscoped). | Must |

### Validation & error responses

| Condition | status | title |
|---|---|---|
| Preset | 409 | `Preset roles cannot be deleted` |
| SUPER_ADMIN | 409 | `SUPER_ADMIN cannot be edited` |
| Custom role in use, `confirmInUse` not true | 409 | `Role is in use` (body: `affectedGroups`) |
| Scoped caller, role carries a permission they lack | 403 | `Would remove more than you hold` |
| Unknown id | 404 | — |

### Audit
- One row: `action: "delete"`, `entity_type: "role"`, `entity_id: id`, `before`: full prior
  projection (incl. permission codes), `after`: `{ status: "INACTIVE", affectedGroups: [...] }`.
- Keeping `action: "delete"` (not `update`) so an auditor searching for role removals finds it.

### Edge cases

1. **Reactivating a soft-deleted role** → `PUT status=ACTIVE`; permissions are still there;
   groups on it start granting again. This is a feature, not a bug — but the audit trail shows
   the delete and the later reactivate.
2. **Soft-deleted role's code** — stays occupied (`roles.code UNIQUE`). Creating a new role
   with the same code → 409 `Role code already exists`. Document: reactivate the old one or
   choose a new code.
3. **`ARCHIVED` variant**: if adopted, `ARCHIVED` blocks `PUT status=ACTIVE` (terminal) → the
   only recovery is a new role. Choose this only if the owner wants deletes to be irreversible.

### Acceptance criteria

```
P8-AC1  Soft-delete a custom role not in use
  Given a custom ACTIVE role R on no access groups
  When  a role.manage caller DELETEs /api/v1/roles/{R}
  Then  the response is 204
  And   the row still exists with status INACTIVE
  And   role_permissions rows for R still exist
  And   one config_audit row with action "delete", entity_type "role".

P8-AC2  Preset cannot be deleted
  Given the preset VIEWER
  When  any caller DELETEs it
  Then  the response is 409, title "Preset roles cannot be deleted".

P8-AC3  In-use custom role needs confirmation
  Given a custom role R referenced by group G
  When  a caller DELETEs /api/v1/roles/{R} without confirmInUse
  Then  the response is 409, title "Role is in use", body lists G
  When  the caller re-sends with confirmInUse=true
  Then  the response is 204, R.status is INACTIVE, G still references R, G grants nothing
        from R.

P8-AC4  Reactivate restores
  Given a soft-deleted custom role R with permissions [camera.read]
  When  a caller PUTs R with status ACTIVE
  Then  R grants camera.read again to ACTIVE groups on it.
```

---

## P9 — `role.read` permission — recommendation

### Finding
Today all `/api/v1/roles/*` reads and `GET /api/v1/permissions` are gated by **`group.read`**
(`RoleEndpoints.ListAsync`/`GetAsync`, `AccessGroupEndpoints.ListPermissionsAsync`). Writes are
gated by `role.manage` (added in `v1.11`).

### Recommendation: **ratify `group.read`; do not add `role.read`.**

Rationale:
- Roles are **global, unscoped reference data** with no sensitive content — a list of permission
  bundles. Anyone who can read access groups already sees every role code and its permissions
  (`AccessGroupResponse.Permissions`). A separate read permission guards nothing that isn't
  already visible.
- Adding a permission means a `v1.12` seed row **and** re-granting it across every preset that
  should have it (`VIEWER`, `ANALYST`, all admins, machine roles that inspect roles…) — a wide
  blast radius for no security gain. Aligns with `MEMORY.md` "user prefers less machinery".
- `role.manage` already cleanly separates *seeing* roles from *changing* them.

### If the owner still wants separation
- `v1.12.sql`: `INSERT INTO permissions (code,name,category,description) VALUES
  ('role.read','View roles','admin', '...')`.
- Grant to every preset that has `group.read` today (query `role_permissions` for `group.read`
  holders, insert `role.read` for the same role ids) — unconditional insert with a comment,
  per the `v1.11` `customized_at` rule.
- Swap `.RequirePermission("group.read")` → `"role.read"` on the four role/permission read
  routes and `caller.Require("role.read")`.
- **Decision needed from project owner.** Default action if no response: ratify `group.read`,
  add one line to `AUTHORIZATION.md` making it explicit.

### Acceptance criteria (for the ratify path)

```
P9-AC1  group.read governs role reads
  Given a caller holding group.read but not role.manage
  When  they GET /api/v1/roles and /api/v1/roles/{id} and /api/v1/permissions
  Then  all return 200.

P9-AC2  Documented
  Then  AUTHORIZATION.md states that role and permission reads are governed by group.read.
```

---

## P10 — Access-group state `DISABLED` → `INACTIVE`

### Intent
Rename the third access-group lifecycle state for consistency with roles, organizations,
areas, users (all use `INACTIVE`).

### Functional requirements

| Ref | Requirement | MoSCoW |
|---|---|---|
| P10-R1 | `v1.12.sql`: `access_groups.status` CHECK → `('DRAFT','ACTIVE','INACTIVE')`; `DROP`/`ADD` constraint; `UPDATE access_groups SET status='INACTIVE' WHERE status='DISABLED'` (no prod rows, but correct). | Must |
| P10-R2 | `AccessGroupEndpoints`: replace every literal `"DISABLED"` with `"INACTIVE"` (in `DisableAsync`, audit snapshots, `SetStatusAsync` calls). | Must |
| P10-R3 | Route: keep the path **`POST /{id}/disable`** (verb is fine) but its effect sets `INACTIVE`; OR rename to `/deactivate` for full consistency with the hierarchy routes. **Recommend** rename to `POST /{id}/deactivate` — it matches units/areas and reads correctly. Keep `/disable` as a deprecated alias for one release if any client exists (none does). | Should |
| P10-R4 | `RBAC-LOGICAL-FLOW.md` §25 diagram updated: `DRAFT → ACTIVE → INACTIVE`; "A disabled group grants no access" → "An inactive group grants no access". | Must |
| P10-R5 | `AUTHORIZATION.md` §5 wording ("Activating an access group with no organization or no geography scope") unaffected by name; but §7 / any "DISABLED" mention updated. | Must |
| P10-R6 | Authz functions filter `ag.status = 'ACTIVE'` — unaffected by the rename. | Must |
| P10-R7 | `docs/API-REVIEW-FINDINGS.md` mentions of group `DISABLED` — add a note pointing to this change (do not rewrite the review). | Could |

### Error responses
No new ones. `POST /{id}/deactivate` on an unknown/out-of-reach group → 404 (unchanged).

### Audit
- `deactivate`: existing `update`/`access_group` row; `before.status` = prior,
  `after.status = "INACTIVE"`.

### Acceptance criteria

```
P10-AC1  Deactivate sets INACTIVE
  Given an ACTIVE access group G
  When  a group.manage caller POSTs /api/v1/access-groups/{G}/deactivate
  Then  the response is 204
  And   G.status is "INACTIVE"
  And   G's members immediately lose the grant
  And   the audit row shows status ACTIVE -> INACTIVE.

P10-AC2  No DISABLED anywhere
  Then  no API response, OpenAPI enum, or seed data contains the token "DISABLED".

P10-AC3  Reactivate restores roster
  Given an INACTIVE group G with membership rows intact
  When  it is activated (subject to P11/P12)
  Then  the same members hold the grant again.
```

---

## P11 — `POST /access-groups/{id}/activate` rejects a non-ACTIVE role

### Functional requirements

| Ref | Requirement | MoSCoW |
|---|---|---|
| P11-R1 | Activation is refused with **409** when the group's role `status <> 'ACTIVE'` (i.e. `DRAFT`, `INACTIVE`, or `ARCHIVED`). Title `Role is not active`. | Must |
| P11-R2 | The check runs **after** visibility/404 and **after** the escalation guard, **before** the empty-dimension check (P12) — cheapest meaningful failure first is fine, but role-state is a clear precondition. Order: 404 → escalation (403) → role-not-active (409) → empty-dimension (P12, 409). | Should |
| P11-R3 | Message names the role code and its state, e.g. `detail: "This group's role CAMERA_OPERATOR is INACTIVE. Activate the role, or repoint the group, before activating the group."` | Must |
| P11-R4 | A group whose role is later deactivated does **not** auto-deactivate — it just stops granting (authz functions require `r.status = 'ACTIVE'`). P11 only blocks the *activation* transition. | Must |

### Validation & error responses

| Condition | status | title |
|---|---|---|
| Group's role not ACTIVE | 409 | `Role is not active` |

### Audit
- On refusal: no audit row (no mutation).
- On success: unchanged activate audit row.

### Acceptance criteria

```
P11-AC1  Draft role blocks activation
  Given a DRAFT group G whose role R is DRAFT
  When  a caller POSTs /access-groups/{G}/activate
  Then  the response is 409, title "Role is not active"
  And   G stays DRAFT.

P11-AC2  Activating the role unblocks
  Given the same, then R is set ACTIVE
  When  the caller retries activation (scopes permitting)
  Then  the response is 204.

P11-AC3  Order of checks
  Given a group the caller cannot see, on an INACTIVE role
  When  the caller POSTs activate
  Then  the response is 404 (visibility wins over role-state).
```

---

## P12 — `POST /access-groups/{id}/activate` body `{ confirmUnscoped: bool }`

### Intent
Replace the current silent 403 on empty-dimension activation with an explicit two-part gate:
the caller must **both** be unscoped for `group.manage` on that dimension **and** pass
`confirmUnscoped: true`. The confirmation is audited.

### Functional requirements

| Ref | Requirement | MoSCoW |
|---|---|---|
| P12-R1 | Request body (optional): `{ "confirmUnscoped": true }`. Absent ⇒ `false`. `activate` currently takes no body — add an optional `[FromBody] ActivateGroupRequest? request`. | Must |
| P12-R2 | When the group **has** both an ORGANIZATION and a GEOGRAPHY scope → body is ignored; behaviour unchanged (proceed if escalation + P11 pass). | Must |
| P12-R3 | When a dimension's scope set is **empty**: activation succeeds **only if** `caller.IsUnscopedForGeography("group.manage")` (for missing geo) / `caller.IsUnscopedFor("group.manage")` (for missing org) **AND** `confirmUnscoped == true`. | Must |
| P12-R4 | Caller **is** unscoped on the dimension but `confirmUnscoped` is absent/false → **409** `Confirm the estate-wide grant`. Detail names the dimension and what activating implies. | Must |
| P12-R5 | Caller is **not** unscoped on the empty dimension → **409** `Group is unrestricted on a dimension` (changed from **403** — C6), regardless of `confirmUnscoped`. Detail: add a scope first. | Must |
| P12-R6 | If **both** dimensions are empty, both must be satisfied (caller unscoped on org AND geo, single `confirmUnscoped: true` covers both). The 409 detail lists every unconstrained dimension. | Should |
| P12-R7 | The successful activation's audit `after` snapshot records `confirmUnscoped: true` and `unconstrainedDimensions: ["organization"]` (or `["geography"]` / both). | Must |
| P12-R8 | `confirmUnscoped: true` sent when **no** dimension is empty → ignored, not an error (forward-compatible). | Should |
| P12-R9 | Update `AUTHORIZATION.md` §5 last row and the endpoint `WithDescription`. | Must |

### Validation & error responses

| Condition | status | title |
|---|---|---|
| Empty dimension, caller unscoped there, `confirmUnscoped` missing/false | 409 | `Confirm the estate-wide grant` |
| Empty dimension, caller NOT unscoped there | 409 | `Group is unrestricted on a dimension` |
| Body present but malformed | 400 | `Invalid request body` |

### Audit
- Success with a confirmed unscoped dimension: existing `update`/`access_group` activate row,
  `after`: `{ status: "ACTIVE", confirmUnscoped: true, unconstrainedDimensions: [...] }`.
- Refusal (either 409): no audit row.
- **Rationale**: the flag is a deliberate, rare, high-consequence acknowledgement — it must be
  reconstructable who confirmed an estate-wide grant and when (`AUTHORIZATION.md` §6).

### Edge cases

1. Group has two ORGANIZATION scopes and zero GEOGRAPHY scopes → geography is the empty
   dimension; org is fine. Only the geo gate applies.
2. Caller is unscoped for `group.manage` on org but scoped on geo, group missing geo scope →
   409 `Group is unrestricted on a dimension` (the geo gate fails on privilege, `confirmUnscoped`
   cannot rescue it).
3. Group already ACTIVE, re-activated → idempotent 204, no re-confirmation, no audit
   (consistent with P3-R5). *Confirm this is desired — an already-active estate-wide group
   re-activated should arguably still be a no-op.*
4. Activation via a future bulk route → same gate; `confirmUnscoped` per group.

### Acceptance criteria

```
P12-AC1  Unscoped caller must still confirm
  Given a group G with organization scopes but no geography scope,
        and a caller unscoped for group.manage on geography
  When  the caller POSTs /activate with no body
  Then  the response is 409, title "Confirm the estate-wide grant".
  When  the caller re-POSTs with { "confirmUnscoped": true }
  Then  the response is 204
  And   the audit after-snapshot has confirmUnscoped: true and
        unconstrainedDimensions: ["geography"].

P12-AC2  Scoped caller cannot confirm their way in
  Given group G missing an organization scope,
        and a caller NOT unscoped for group.manage on organization
  When  the caller POSTs /activate with { "confirmUnscoped": true }
  Then  the response is 409, title "Group is unrestricted on a dimension"
  And   G is not activated.

P12-AC3  Fully scoped group ignores the flag
  Given group G with both an organization and a geography scope
  When  a caller POSTs /activate with { "confirmUnscoped": true }
  Then  the response is 204 and the audit snapshot does not claim any unconstrained dimension.

P12-AC4  Both dimensions empty
  Given group G with no scopes at all,
        and a caller unscoped for group.manage on both dimensions
  When  the caller POSTs /activate without confirmUnscoped
  Then  409, title "Confirm the estate-wide grant", detail lists organization and geography
  When  re-sent with confirmUnscoped: true
  Then  204, unconstrainedDimensions: ["organization","geography"].
```

---

## P13 — `AccessGroupResponse`: `createdAt` / `updatedAt`; confirm `Permissions`

### Findings

- `access_groups` has `created_at`, `updated_at`, `created_by`, `updated_by` — none surfaced in
  `AccessGroupResponse` today.
- `AccessGroupResponse.Permissions` is populated (in `AccessGroupRepository.LoadAsync`, second
  query) as **`SELECT rp.permission_code FROM access_groups ag JOIN role_permissions rp ON
  rp.role_id = ag.role_id`** — i.e. **the raw permission set of the group's role**. It is **not**
  an "effective" set: it does **not** account for
  - the group's own `status` (a `DRAFT`/`INACTIVE` group grants nothing),
  - the role's `status` (a `DRAFT`/`INACTIVE` role grants nothing),
  - membership expiry,
  - scope (permissions are estate-wide in the list; scope is a separate array).

### Functional requirements

| Ref | Requirement | MoSCoW |
|---|---|---|
| P13-R1 | Add `createdAt: DateTimeOffset` and `updatedAt: DateTimeOffset` to `AccessGroupResponse` and `AccessGroupDetail`, sourced from the columns. `DateTimeOffset`, never `DateTime` (`CLAUDE.md` invariant 6). | Must |
| P13-R2 | Optionally add `createdBy` / `updatedBy` (user ids). **Recommend** include them — cheap, and useful for the maker-checker direction in `F7`. | Should |
| P13-R3 | **Document** `Permissions` precisely: rename the doc/summary to "the permission codes this group's **role** composes" and add a sibling field `grantsEffective: bool` = `(group.status = 'ACTIVE' AND role.status = 'ACTIVE')`. A client can then tell "these permissions are live" from "these are what it *would* grant". | Should |
| P13-R4 | Do **not** try to make `Permissions` scope-aware — scope is orthogonal and already in `Scopes[]`. The pairing (permissions × scopes) is the grant; keep them separate as today. | Must |
| P13-R5 | `updatedAt` must actually move on every write path (`UpsertAsync`, `SetStatusAsync`, `AddScopeAsync`, `RemoveScopeAsync`). Confirm each `UPDATE` sets `updated_at = now()` and `updated_by`. Scope add/remove currently touch `group_scopes`, not `access_groups` — **decide** whether a scope change bumps the group's `updatedAt` (recommend **yes**, it changes what the group confers). | Should |
| P13-R6 | OpenAPI/XML doc on `AccessGroupResponse` updated to state the `Permissions` semantics and the `grantsEffective` meaning. | Must |

### Error responses
None (shape change only).

### Audit
None directly. But P13-R5's decision (scope change bumps `updatedAt`) improves the fidelity of
the existing scope-change audit rows' correlation.

### Acceptance criteria

```
P13-AC1  Timestamps present and correct
  Given a group created at T1 and edited at T2
  When  a caller GETs /api/v1/access-groups/{id}
  Then  createdAt = T1 and updatedAt = T2.

P13-AC2  Permissions is the role's composed set, labelled
  Given a DRAFT group on an ACTIVE role granting [camera.read, camera.update]
  When  a caller GETs the group
  Then  Permissions = [camera.read, camera.update]
  And   grantsEffective = false (because the group is DRAFT)
  And   the field documentation states Permissions is the role's composition, not a live grant.

P13-AC3  Scope change moves updatedAt
  Given an ACTIVE group
  When  a caller adds a scope
  Then  the group's updatedAt advances
  And   (existing) a group_scope audit row is written.

P13-AC4  Effective flag tracks both statuses
  Given an ACTIVE group on a role that is then set INACTIVE
  When  a caller GETs the group
  Then  grantsEffective = false.
```

---

## Cross-cutting requirements

| Ref | Requirement |
|---|---|
| X-1 | **One schema file, `db/versions/v1.12.sql`**, carries: the two CHECK-constraint changes (roles, access_groups), the `assert_org_unit_acyclic` relaxation (P2b — only if approved), and any `role.read` seed (P9 — only if approved). `v1.11` and earlier are never edited. `db/objects/` mirrors get the same changes for review. |
| X-2 | Every raised-exception path from a trigger (`assert_org_unit_acyclic`, `assert_geographic_area_acyclic`) must be **mapped to a specific 400/409 Problem**, never surfaced as a 500 with the raw `RAISE` text. |
| X-3 | Every new/changed write keeps the **mutation + audit in one `UnitOfWork` transaction** (invariant 10). No new "record separately" path. |
| X-4 | Every scoped repository method keeps its **required `CallerContext`** (invariant 11). The new activate methods and the reparent path take it. |
| X-5 | **Sabotage-check** each authorization-relevant test (`MEMORY.md`): break the guard, confirm the test fails (P1-AC4, P2b-AC3, P7-AC4, P11-AC1, P12-AC2). |
| X-6 | Docs updated in the same change (code and doc move together — `CLAUDE.md`): `DEPARTMENT-SCHEMA.md` (P1, P2b, P3), `GEOGRAPHY-SCHEMA.md` (P2a, P3), `RBAC-LOGICAL-FLOW.md` §25 (P10), `AUTHORIZATION.md` §5/§7 (P10, P12), `docs/API-REVIEW-FINDINGS.md` note (P10). |
| X-7 | OpenAPI: no "Model N" jargon in new descriptions (`F1` in `MEMORY.md`). |
| X-8 | Keyset/`updatedAt` fields use `DateTimeOffset` end to end (invariant 6). |

---

## Riskiest decisions & open questions for the project owner

### Riskiest decisions

1. **P2b reverses a documented invariant (C1).** "A child unit must belong to the same
   organization as its parent" and "a unit's organizationId is immutable" are stated rules in
   `DEPARTMENT-SCHEMA.md`. Cross-org move is a genuine re-rooting of a subtree. The
   silent-privilege-shift on access-group scopes (P2b-R3) is the sharp edge: a scope row keeps
   pointing at a unit id whose organization has changed underneath it, so a group that meant
   "Police, Zone 1" now means "Transport, Zone 1". The `confirmScopeImpact` gate makes it a
   visible decision, but it is still a footgun. **Recommend: require project-owner sign-off, and
   restrict P2b to unscoped `organization.manage` callers only.**

2. **P2b and the acyclic trigger.** Relaxing `assert_org_unit_acyclic` to permit a subtree
   `organization_id` rewrite needs either careful statement ordering (parent before children in
   one recursive `UPDATE`) or converting the same-org check to a `DEFERRABLE` constraint
   trigger. Getting this wrong either blocks the legitimate move or opens a mixed-org tree
   (which hangs scope resolution). **Needs the pg/.NET reviewer before implementation.**

3. **P10 is a breaking rename** (`DISABLED` → `INACTIVE`) on a field clients read. Safe now
   (nothing deployed, per `MEMORY.md`), but it must land before any external consumer exists.

4. **P12 changes 403 → 409** for empty-dimension activation (C6). Any client currently handling
   403 on that path will need updating. Also a semantic shift: activation now *always* needs an
   explicit `confirmUnscoped` for an unconstrained dimension, even for a super-admin — slightly
   more friction for the platform-admin group's own lifecycle.

5. **P8 removes the `InUse` hard block on role deletion.** Today a role in use simply cannot be
   deleted. After P8, it can be soft-deleted (with confirmation), instantly de-granting every
   group on it. That is more power in one call than the current model allows.

### Open questions

| # | Question | Needed for | Default if no answer |
|---|---|---|---|
| Q1 | Approve P2b (cross-org subtree move) at all? If yes, unscoped-only? | P2b | **Do not build P2b**; ship P1 (same-org reparent) only. |
| Q2 | For P2b, should historic `federation_event` / `detection_event` rows keep the *old* `organization_id` (freeze at ingest) or follow the live tree? | P2b-R8 | Follow the live tree (no row rewrite); document it. |
| Q3 | P9 — add a `role.read` permission, or ratify `group.read`? | P9 | Ratify `group.read`; one doc line. |
| Q4 | P8 — soft-delete to `INACTIVE`, or add a distinct terminal `ARCHIVED`? | P7/P8 schema | `INACTIVE` (fewer states). |
| Q5 | P10 — rename the route `/disable` → `/deactivate`, or keep `/disable` with new semantics? | P10-R3 | Rename to `/deactivate`, no alias (nothing deployed). |
| Q6 | P7 — should the `roles.status` column **default** change to `DRAFT` (matching the API), or stay `ACTIVE` with the API overriding? | P7-R2 | Change column default to `DRAFT`; v1.12 leaves existing rows `ACTIVE`. |
| Q7 | P2a — geography area writes do not currently run an `authorized_geographic_areas` reach check the way org units do. Add one for parity (P2a-R6)? | P2a | Add it — scoped `geography.manage` holders should not reparent areas outside their reach. |
| Q8 | P6 — filter `usedBy` to caller-visible groups (P6-R6)? | P6 | Yes, filter; `usageCount` stays a true total. |
| Q9 | P12 — should re-activating an already-ACTIVE estate-wide group re-demand `confirmUnscoped`? | P12 edge case 3 | No; idempotent no-op. |
| Q10 | P13 — does a scope add/remove bump `access_groups.updatedAt` (P13-R5)? | P13 | Yes. |
