# Implementation notes — hierarchy & RBAC lifecycle wave

Design for the build sequence
`P3 → P1 → P2a → P11 → P12 → P8 → P5 → P6 → P13 → P7 → P10 → P2b`.
**Status: shipped in `v1.12`** (2026-09-08). See "As-built deviations" at the end for where the
code differs from this design.

Scope: organization-unit / geographic-area edit + lifecycle, role lifecycle + DRAFT,
access-group activation guard + status rename, cross-org unit move.

Verification at ship: `dotnet build` clean (analyzers + warnings-as-errors); new
`HierarchyRbacLifecycleTests` (23) green plus adjusted `RoleCrudTests` / `AuthorizationTests` /
`HierarchyScopeTests` / `GeographyScopeTests` / `UnscopedReadScopeTests`; sabotage-checked the
P2b unscoped guard and the P1 reparent-to-root guard. The 18 pre-existing `IntegrationTests`
failures (camera `vendor_kind`, one apikey `DateTime` cast) are unrelated and predate this wave.

## 0. Shared facts the design leans on

- Every authorization function (`has_permission`, `authorized_org_units`,
  `authorized_geographic_areas`, `unscoped_permissions`, `has_unscoped_geography`,
  `principal_permissions`, `user_effective_access` view) already gates
  `roles.status = 'ACTIVE'` and `access_groups.status = 'ACTIVE'`. Adding new
  non-ACTIVE role/group statuses therefore grants nothing automatically — no authz
  function body needs to change for `DRAFT` / `ARCHIVED` / renamed `INACTIVE`.
- `db/versions/v1.sql` defines two overload families of the authz functions (a
  legacy `p_user_id`-only set around lines 656/768/795, and the principal-based set
  the repositories actually call, around 1495/1600/2029). Both gate `r.status = 'ACTIVE'`.
  A status CHECK widen touches neither.
- `assert_org_unit_acyclic()` fires `BEFORE INSERT OR UPDATE OF parent_unit_id, organization_id`
  and enforces: not-self-parent, **same organization as parent**, no cycle.
- `assert_geographic_area_acyclic()` (rewritten in v1.11) fires on `parent_area_id, area_type`
  and enforces: not-self-parent, no cycle, level-order containment vs parent and vs
  direct children. Its `RAISE EXCEPTION` maps to **400** via
  `ConstraintViolationExceptionHandler` (`PostgresErrorCodes.RaiseException` →
  `pg.MessageText` passed through).
- Test DB is built by replaying every `db/versions/*.sql` in order
  (`tests/Trinetra.IntegrationTests/PostgresFixture.cs`). A new version file that
  fails to apply fails the suite.
- Only **one** new SQL version file is needed for the whole wave: **`db/versions/v1.12.sql`**
  (covers P7, P8, P10, P2b). P3/P1/P2a/P11/P12/P5/P6/P13 are code-only.
- `db/objects/` must be updated in lockstep for every object v1.12 changes:
  `db/objects/tables/roles.sql`, `db/objects/tables/access_groups.sql`,
  `db/objects/functions/assert_org_unit_acyclic.sql`.

---

## P3 — reject PUT on an INACTIVE hierarchy node; add `/activate`

### Behaviour
- `PUT /api/v1/organization-units/{id}` and `PUT /api/v1/geographic-areas/{id}`:
  if the stored row's `status = 'INACTIVE'`, return **409** ("Reactivate before
  editing") and make no change. (Today they happily edit an inactive row.)
- New `POST /api/v1/organization-units/{id}/activate` and
  `POST /api/v1/geographic-areas/{id}/activate`: set `status = 'ACTIVE'`.
  Refuse with **409** if the node's parent is `INACTIVE` (activating a child under a
  dead parent recreates the 5-M8 orphan). Root nodes (no parent) always allowed.
  Scoped-caller reach check identical to the other write paths
  (`RequireReachAsync` / `RequireAreaAsync` on the node id; a scoped caller
  activating a root is refused, same as create).

### Files
| File | Change |
|---|---|
| `src/Trinetra.Federation.Api/Endpoints/HierarchyEndpoints.cs` | INACTIVE-guard in `UpdateUnitAsync` / `UpdateAreaAsync`; new `ActivateUnitAsync` / `ActivateAreaAsync` handlers + route registration |
| `src/Trinetra.Federation.Storage/Repositories/OrganizationRepository.cs` | `ActivateUnitAsync` |
| `src/Trinetra.Federation.Storage/Repositories/GeographyRepository.cs` | `ActivateAreaAsync` |

### Signatures
```csharp
// OrganizationRepository
public async Task<ActivateResult> ActivateUnitAsync(
    Guid unitId, CallerContext caller, UnitOfWork work, CancellationToken ct);

// GeographyRepository
public async Task<ActivateResult> ActivateAreaAsync(
    Guid areaId, CallerContext caller, UnitOfWork work, CancellationToken ct);

// Storage/Repositories/Records.cs — new
public enum ActivateResult { Activated, NotFound, ParentInactive, AlreadyActive }
```
- Repo takes the tree advisory lock (`pg_advisory_xact_lock(hashtext('federation.organization_units.deactivate'))`
  / the geo equivalent) so activate is ordered against a concurrent cascade-deactivate.
- Repo does `SELECT status, parent_unit_id ... FOR UPDATE`, checks parent status,
  `UPDATE ... SET status='ACTIVE', updated_at=now()`.
- Endpoint writes the audit row (`before {status:'INACTIVE'}` / `after {status:'ACTIVE'}`)
  on the same `UnitOfWork`, then commits. Maps `ParentInactive` → 409, `NotFound` → 404,
  `AlreadyActive` → 204 (idempotent).

### Contract additions
`docs/MODEL-1-API-PLAN.md` + OpenAPI descriptions: document the two new routes and
the new 409 on PUT.

---

## P1 — allow re-parent through `PUT /organization-units/{id}`

### Behaviour
- Remove the "Cannot re-parent through this route" 400 block in `UpdateUnitAsync`.
- When `request.ParentUnitId != prior.ParentUnitId`:
  - **400** if new parent `== id` or new parent `∈ org_unit_descendants(id)`
    (pre-check in the repo, in-transaction).
  - Otherwise call `UpsertUnitAsync(..., parentIsChanging: true)` — which already:
    - asserts the new parent exists and is `ACTIVE` (`RequireActiveParentAsync`);
    - for a scoped caller, asserts reach over **both** the moved unit and the new
      parent (`RequireReachAsync` ×2) — so a scoped caller can only re-parent
      within their own reach, 403 otherwise;
    - lets `trg_org_unit_acyclic` catch same-org / cycle (→ 400 via handler).
- Audit `before {parentUnitId: old}` / `after {parentUnitId: new}` (the endpoint
  already snapshots `prior`; keep the full `ToResponse` before/after).

### New concern — serialize against cascade-deactivate
`DeactivateUnitAsync` takes `pg_advisory_xact_lock(hashtext('federation.organization_units.deactivate'))`
to order tree mutations. A PUT re-parent is also a tree mutation and today takes no
such lock. **`UpsertUnitAsync` must take the same advisory lock when
`parentIsChanging` and `unit.Id != Guid.Empty`**, then re-read the moved row and the
new parent `FOR UPDATE` before the descendant/active checks. Without it: concurrent
"cascade-deactivate ancestor A" + "re-parent live subtree under a node inside A"
reintroduces 5-H2 (active node under inactive ancestor) or detaches the subtree.

### Files
| File | Change |
|---|---|
| `HierarchyEndpoints.cs` | delete the 400 guard in `UpdateUnitAsync`; pass `parentIsChanging: request.ParentUnitId != prior.ParentUnitId`; keep NotFound + INACTIVE(P3) guards first |
| `OrganizationRepository.cs` | `UpsertUnitAsync`: take tree advisory lock + `FOR UPDATE` re-read when moving; add self / descendant pre-check → new `ReparentError` or reuse `InvalidOperationException` (handler already maps that to 400 in the deactivate helper — but `UpdateUnitAsync` is not that helper, so return a typed result instead) |

### Signature change
```csharp
// OrganizationRepository — replace bool return with a typed result so the self/cycle
// pre-check has somewhere to land without throwing.
public async Task<UnitWriteResult> UpsertUnitAsync(
    OrganizationUnit unit, CallerContext caller, UnitOfWork work, CancellationToken ct,
    bool parentIsChanging = true);

public enum UnitWriteError { None, ReparentUnderSelfOrDescendant }
public readonly record struct UnitWriteResult(UnitWriteError Error, Guid Id);
```
`CreateUnitAsync` / `DeactivateUnitAsync` call sites updated for the new return type
(they can treat anything but `None` as impossible and read `.Id`).

---

## P2a — allow re-parent through `PUT /geographic-areas/{id}`

Same shape as P1, but the DB does more of the work:
- Remove the 400 re-parent block in `UpdateAreaAsync`.
- `parentIsChanging: request.ParentAreaId != prior.ParentAreaId`.
- `UpsertAreaAsync` already: parent reach check (scoped), `RequireActiveAreaAsync`
  when `parentIsChanging`. `trg_geo_area_acyclic` enforces cycle **and** level-order
  containment (child finer than new parent, and area still finer than its own
  children) — all surface as **400** through the existing handler
  (`RaiseException` → `MessageText`). Confirmed: `ConstraintViolationExceptionHandler`
  line 55-58 passes `pg.MessageText` for `PostgresErrorCodes.RaiseException`.
- Add the same **self / `geographic_area_descendants(id)`** pre-check the trigger's
  cycle rule already covers — the trigger is sufficient here, so this is optional
  belt-and-braces; recommend relying on the trigger to avoid a second code path.
- **Advisory-lock concern is identical** — `UpsertAreaAsync` should take
  `pg_advisory_xact_lock(hashtext('federation.geographic_areas.deactivate'))` when
  moving a parent, to order against `DeactivateAreaAsync`.

### Files
`HierarchyEndpoints.cs` (`UpdateAreaAsync`), `GeographyRepository.cs` (`UpsertAreaAsync`
lock + FOR UPDATE re-read). No new SQL — trigger already does containment/cycle.

---

## P11 — refuse group activation when its role is not ACTIVE

### Behaviour
`AccessGroupEndpoints.ActivateAsync`: after the visibility/NotFound check and before
the scope-completeness checks, if the group's role `status != 'ACTIVE'` return
**409** ("Role is not active — a group on an inactive/draft/archived role would grant
nothing"). This stops an operator "activating" a group that is silently inert.

### Files
| File | Change |
|---|---|
| `src/Trinetra.Federation.Storage/Repositories/AccessGroupRepository.cs` | add `r.status` to the first result-set of `LoadAsync`; add `string RoleStatus` to `AccessGroupDetail` |
| `src/Trinetra.Federation.Storage/Repositories/RoleRepository.cs` | (alt) `public async Task<string?> GetStatusAsync(Guid roleId, CancellationToken ct)` — simpler, no record churn |
| `src/Trinetra.Federation.Api/Endpoints/AccessGroupEndpoints.cs` | `ActivateAsync` 409 branch |

**Decision:** prefer the `RoleRepository.GetStatusAsync` one-liner over widening
`AccessGroupDetail` (that record flows into `AccessGroupResponse` via `ToResponse` and
is used by list + get + update; adding a positional field ripples). If P6/P13 end up
wanting role status in the response anyway, revisit.

---

## P12 — explicit confirmation for unrestricted-dimension activation

### Behaviour
`ActivateAsync` gains an optional body:
```csharp
public sealed record ActivateGroupRequest(bool ConfirmUnscoped = false);  // Contracts.cs
```
Current logic (lines 304-313) returns **403** when a dimension has no scope and the
caller is not unscoped on it. New logic when a dimension has no scope:
- caller **not** unscoped on that dimension → **409** (unchanged intent, status
  changes 403→409; "add a scope, or an unscoped admin must confirm").
- caller **is** unscoped on that dimension → require `ConfirmUnscoped == true`;
  if false → **409** ("this activates an estate-wide grant; resend with
  `confirmUnscoped: true`"). If true → proceed.
- Audit `after` gains `confirmUnscoped: true` and which dimension(s) were unrestricted.

### Files
`AccessGroupEndpoints.cs` (`ActivateAsync` signature `+ [FromBody] ActivateGroupRequest? request`,
rework the two `UnscopedActivationProblem` branches, extend the audit `after` object),
`Contracts.cs` (`ActivateGroupRequest`). No storage change.

**Decision:** confirm the status code — plan says 409 throughout; current code is 403.
409 is defensible ("conflict: group not in an activatable shape"). Going with 409.

---

## P7 — `roles.status` gains `DRAFT` (+ `ARCHIVED` for P8)

### v1.12.sql fragment
```sql
SET search_path TO federation, public;

-- Roles: DRAFT (composed, not yet granting) + ARCHIVED (soft-deleted, see P8).
ALTER TABLE roles DROP CONSTRAINT IF EXISTS roles_status_check;
ALTER TABLE roles ADD CONSTRAINT roles_status_check
    CHECK (status IN ('DRAFT', 'ACTIVE', 'INACTIVE', 'ARCHIVED'));
-- No authz-function change: every one already requires roles.status = 'ACTIVE',
-- so DRAFT / INACTIVE / ARCHIVED all grant nothing.
```

### Behaviour / code
- `RoleRepository.CreateAsync`: new custom roles default **`DRAFT`**, not `ACTIVE`
  (`VALUES (..., COALESCE(@status, 'DRAFT'))`). An operator composes then flips to
  `ACTIVE` via `PUT` (`status` in `RoleWriteRequest`).
- Presets stay `ACTIVE` (seeded in v1.sql; unaffected).
- `RoleWriteRequest.Status` validation: accept `DRAFT` / `ACTIVE` / `INACTIVE` only
  on write; `ARCHIVED` is reachable only through `DELETE` (P8) — reject `ARCHIVED`
  in a PUT body with `RoleWriteError.UnknownPermission`-style 400 (add
  `RoleWriteError.InvalidStatus`).
- `AccessGroupRepository.ListRolesAsync` (the group-create role picker) already
  filters `status = 'ACTIVE'` — correct, DRAFT/ARCHIVED roles are not pickable.
- `RoleRepository.ListAsync` currently returns **all** roles — see P5, which adds
  the `includeInactive` filter; a DRAFT role is "not active" for that filter.

### Files
`db/versions/v1.12.sql`, `db/objects/tables/roles.sql`,
`RoleRepository.cs` (default status, PUT status whitelist), `Records.cs`
(`RoleWriteError.InvalidStatus`), `RoleEndpoints.cs` (`Problem` mapping),
`docs/AUTHORIZATION.md` §5 + `docs/RBAC-LOGICAL-FLOW.md` (note DRAFT roles),
OpenAPI text on `POST/PUT /roles`.

---

## P8 — soft-delete roles

### Behaviour
`DELETE /api/v1/roles/{id}` stops hard-deleting. `RoleRepository.DeleteAsync` becomes
a status update to **`ARCHIVED`**:
- preset (`is_system`) → **409** `PresetShapeFixed` (unchanged).
- role still referenced by an access group → **409** `InUse` (unchanged — keep the
  guard; an archived role on a live group would silently inert the group, and P11's
  activation guard only bites at activate-time).
- otherwise `UPDATE roles SET status='ARCHIVED', updated_at=now() WHERE id=@id`.
  `role_permissions` rows are **kept** (needed for audit `before` snapshots and any
  future un-archive). Response stays **204**.
- Escalation guard: `DeleteAsync` currently takes no `CallerContext`. Archiving a
  role a scoped caller couldn't otherwise touch is not an escalation (it removes
  capability), but for symmetry with `UpdateAsync`'s preset rule, keep the existing
  `is_system` refusal and add nothing else. **Decision:** leave `DeleteAsync`
  context-free (matches today).

### Decision — `ARCHIVED` vs `INACTIVE`
Use **`ARCHIVED`**, distinct from operator-set `INACTIVE`:
- `INACTIVE` = "temporarily disabled, will re-enable" (reachable via PUT).
- `ARCHIVED` = "deleted" — hidden from `ListAsync` even with `includeInactive` unless
  a future `includeArchived` is added; not settable via PUT.
Keeps the audit trail honest about *why* a role stopped granting.

### Files
`RoleRepository.cs` (`DeleteAsync` body → status update; `LockAsync` already loads
the row), `RoleEndpoints.cs` (`DeleteAsync` — the audit call already logs
`action: "delete"`; keep it, or change to `"archive"` — **recommend `"archive"`**),
`db/versions/v1.12.sql` (the CHECK widen from P7 already includes `ARCHIVED`),
OpenAPI text.

---

## P5 — `includeInactive` on role list

### Behaviour
`GET /api/v1/roles?includeInactive=false` (default). Default returns
`status = 'ACTIVE'` only. `includeInactive=true` returns `ACTIVE` + `DRAFT` +
`INACTIVE` (still **not** `ARCHIVED`).

### Files
| File | Change |
|---|---|
| `RoleRepository.cs` | `ListAsync(bool includeInactive, CancellationToken ct)` — `WHERE status = 'ACTIVE' OR (@includeInactive AND status <> 'ARCHIVED')` |
| `RoleEndpoints.cs` | `ListAsync(bool? includeInactive, ...)` query param, pass through |

**Decision:** does `includeInactive=true` include `ARCHIVED`? Recommend **no** —
add a separate `includeArchived` later if an "undelete" UI needs it. Keeps the
default list clean and archived-means-gone.

---

## P6 — richer `RoleResponse`

### New response shape
```csharp
// Responses.cs
public sealed record RolePermissionDetail(
    string Code, string Name, string Category, string? Description);

public sealed record RoleUsingGroup(Guid Id, string Code, string Name, string Status);

public sealed record RoleResponse(
    Guid Id, string Code, string Name, string? Description, bool IsSystem, string Status,
    bool Customized,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? CustomizedAt,
    int UsageCount,
    IReadOnlyList<RoleUsingGroup> UsingGroups,
    IReadOnlyList<RolePermissionDetail> Permissions);   // was IReadOnlyList<string>
```
`UsageCount` = count of `access_groups` on the role (any status); `UsingGroups` =
those groups (id/code/name/status). **Decision:** on the *list* endpoint, returning
`UsingGroups` per role is an N-groups payload — return `UsageCount` on the list and
the full `UsingGroups` only on `GET /{id}`. Two `RoleDetail` shapes, or a nullable
list. Recommend: `UsingGroups` populated on `GET /{id}` only, empty on list.

### Storage
```csharp
// RoleRepository — RoleDetail record grows the same fields (minus DTO-only concerns)
public sealed record RoleDetail(
    Guid Id, string Code, string Name, string? Description, bool IsSystem, string Status,
    bool Customized,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? CustomizedAt,
    int UsageCount,
    IReadOnlyList<RoleUsingGroupRow> UsingGroups,
    IReadOnlyList<RolePermissionRow> Permissions);
```
- `ListAsync` / `GetAsync` / `LoadAsync` / `LockAsync`: SELECT adds
  `created_at, updated_at, customized_at`.
- Permission metadata: `LoadAsync` joins `role_permissions rp JOIN permissions p
  ON p.code = rp.permission_code` for `name, category, description`.
  `LockAsync` (write path) can keep the bare-code list — it only needs codes for the
  escalation check; don't pay for the join there.
- `UsageCount` + `UsingGroups`: `GetAsync` runs a third result-set
  `SELECT id, code, name, status FROM access_groups WHERE role_id = @id`.
  `ListAsync` runs `SELECT role_id, count(*) FROM access_groups GROUP BY role_id`
  as one extra result-set and maps to a lookup.
- `Escalation()` and `RoleRepository.GetPermissionsAsync` / `AccessGroupRepository.GetRolePermissionsAsync`
  stay code-only — do not touch.

### Files
`RoleRepository.cs`, `RoleEndpoints.cs` (`ToResponse` rewrite; two variants or a
flag), `Responses.cs`, OpenAPI schema examples, `docs/MODEL-1-API-PLAN.md` /
whichever doc lists the role contract.

**Risk:** `RoleResponse` is used in `RoleEndpoints.UpdateAsync`/`DeleteAsync` audit
`before`/`after`. Growing it means the audit snapshot grows too — fine (snapshots are
meant to be full), but the `after: request` in `UpdateAsync` is a `RoleWriteRequest`,
asymmetric with `before: ToResponse(prior)`. Leave that as-is (pre-existing).

---

## P13 — `AccessGroupResponse` gains `CreatedAt` / `UpdatedAt`

### Behaviour
```csharp
// Responses.cs
public sealed record AccessGroupResponse(
    Guid Id, string Code, string Name, string? Description, string Status,
    string RoleCode, IReadOnlyList<string> Permissions,
    IReadOnlyList<ScopeResponse> Scopes, int MemberCount,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
```
- `AccessGroupDetail` + `GroupRow` + `LoadAsync` first SELECT add `ag.created_at,
  ag.updated_at`.
- Confirm `Permissions` is the **effective** set: `LoadAsync`'s second result-set is
  `access_groups ag JOIN role_permissions rp ON rp.role_id = ag.role_id` — that is
  the role's full permission list, unfiltered by role status. **Finding:** if the
  role is `DRAFT`/`INACTIVE`/`ARCHIVED`, `Permissions` here still lists them, while
  the group actually grants nothing. Either (a) leave it (it shows "what this group
  *would* grant") and document, or (b) filter `AND EXISTS (SELECT 1 FROM roles r
  WHERE r.id = ag.role_id AND r.status='ACTIVE')`. **Decision:** leave the list as
  "role's composition" and instead surface role status (see P11 note) so the reader
  can tell. Document in the endpoint description.

### Files
`AccessGroupRepository.cs`, `AccessGroupEndpoints.cs` (`ToResponse`), `Responses.cs`,
OpenAPI.

---

## P10 — rename access-group status `DISABLED` → `INACTIVE`

### v1.12.sql fragment
```sql
-- Order matters: migrate rows, THEN swap the constraint.
ALTER TABLE access_groups DROP CONSTRAINT IF EXISTS access_groups_status_check;
UPDATE access_groups SET status = 'INACTIVE' WHERE status = 'DISABLED';
ALTER TABLE access_groups ADD CONSTRAINT access_groups_status_check
    CHECK (status IN ('DRAFT', 'ACTIVE', 'INACTIVE'));
```
No authz function references the literal `'DISABLED'` (verified: functions only test
`= 'ACTIVE'`). `user_groups.status` keeps its own `('ACTIVE','REVOKED')` domain —
untouched.

### Code
- `AccessGroupEndpoints.DisableAsync`: `SetStatusAsync(id, "INACTIVE", ...)`, audit
  `after { status = "INACTIVE" }`, all doc strings `DISABLED` → `INACTIVE`.
  **Decision:** keep the route path `/disable` (verb, not status) — or rename to
  `/deactivate` for consistency with hierarchy nodes. Recommend keeping `/disable`
  to avoid a client break; note the divergence.
- `ActivateAsync` description: "Moves a `DRAFT` or `INACTIVE` group to `ACTIVE`".
- `AccessGroupRepository.SetStatusAsync` XML doc + any `"DISABLED"` string.
- Grep the whole solution for `"DISABLED"` / `DISABLED` in `.cs`, `.md`, OpenAPI —
  including `docs/AUTHORIZATION.md` §5 table row and `docs/RBAC-LOGICAL-FLOW.md`.

### Files
`db/versions/v1.12.sql`, `db/objects/tables/access_groups.sql`,
`AccessGroupEndpoints.cs`, `AccessGroupRepository.cs`, `docs/AUTHORIZATION.md`,
`docs/RBAC-LOGICAL-FLOW.md`, `docs/MODEL-1-API-PLAN.md`, OpenAPI text.

---

## P2b — cross-organization unit move

The genuinely hard one. `PUT /organization-units/{id}` (or a dedicated
`POST /{id}/move-organization`) allows a re-parent where the new parent sits in a
different `organization_id`; the whole subtree's `organization_id` is rewritten in
the same transaction.

### DB change — relax `assert_org_unit_acyclic()`
Today the trigger raises if
`(SELECT organization_id FROM organization_units WHERE id = NEW.parent_unit_id) IS DISTINCT FROM NEW.organization_id`.
A single `UPDATE ... WHERE id IN (SELECT id FROM org_unit_descendants(@root))` fires
the row trigger per row in arbitrary order, so a child can be updated before its
parent and trip the check. Triggers are not deferrable.

**Decision required — pick one:**
1. **Drop the same-organization assertion from the trigger entirely** (keep
   self-parent + cycle). Enforce "a unit's organization always equals its parent's,
   and a subtree moves atomically" in `OrganizationRepository`. Simplest; the
   invariant becomes app-enforced (like cycle-prevention's comment says app checks
   are bypassable — but this one is only violable by a deliberate bad migration).
   `CREATE OR REPLACE FUNCTION` is correct (body-only, `RETURNS TRIGGER` unchanged —
   invariant 13 is about *signature* changes).
2. Keep the assertion but make it tolerant: skip it when
   `NEW.organization_id IS DISTINCT FROM OLD.organization_id` **and** the parent is
   also mid-move (hard to express per-row). Not recommended.
3. `SET session_replication_role = replica` around the batch update to suppress
   triggers — a footgun, needs superuser, suppresses *all* triggers. Rejected.

Recommend **option 1**, with a strong comment in the function and `db/objects`.

```sql
CREATE OR REPLACE FUNCTION assert_org_unit_acyclic() RETURNS TRIGGER
LANGUAGE plpgsql SET search_path = federation, public AS $$
BEGIN
    IF NEW.parent_unit_id IS NULL THEN RETURN NEW; END IF;
    IF NEW.parent_unit_id = NEW.id THEN
        RAISE EXCEPTION 'Organization unit % cannot be its own parent.', NEW.id;
    END IF;
    -- Same-organization-as-parent is now enforced in OrganizationRepository, which
    -- rewrites organization_id across a whole subtree atomically on a cross-org
    -- move (P2b). A per-row trigger check cannot see a half-applied batch and
    -- rejected the legitimate move. Cycle prevention stays here.
    IF EXISTS ( ...unchanged recursive ancestors check... ) THEN
        RAISE EXCEPTION 'Organization unit % would create a circular parent relationship.', NEW.id;
    END IF;
    RETURN NEW;
END $$;
```
Trigger definition unchanged (still `BEFORE INSERT OR UPDATE OF parent_unit_id,
organization_id`).

### Repository
```csharp
// OrganizationRepository
public async Task<MoveOrgResult> MoveUnitToOrganizationAsync(
    Guid unitId, Guid newParentUnitId, CallerContext caller, UnitOfWork work,
    CancellationToken ct);

public enum MoveOrgError {
    None, UnitNotFound, ParentNotFound, ParentInactive,
    ReparentUnderSelfOrDescendant, CallerCannotReachSource, CallerCannotReachTarget }
public readonly record struct MoveOrgResult(MoveOrgError Error, Guid FromOrg, Guid ToOrg);
```
Steps, all in the one `UnitOfWork` transaction:
1. Tree advisory lock (`pg_advisory_xact_lock(hashtext('federation.organization_units.deactivate'))`).
2. `SELECT ... FOR UPDATE` the moved unit; 404 if missing.
3. `SELECT organization_id, status FROM organization_units WHERE id = @newParentUnitId FOR UPDATE`;
   `ParentNotFound` / `ParentInactive`.
4. `newParentUnitId ∉ org_unit_descendants(unitId)` and `!= unitId` →
   `ReparentUnderSelfOrDescendant`.
5. **Scoped caller:** must be unscoped for `organization.manage`, OR reach **both**
   the source subtree root and the destination parent
   (`authorized_org_units(...,'organization.manage')`). A scoped caller moving a
   unit into an org they can't administer is escalation; a scoped caller moving a
   unit *out* of their own org loses oversight of it — refuse both unless unscoped.
   **Decision:** require **unscoped `organization.manage`** for a cross-org move,
   full stop. Simpler and safer than a two-ended reach check; matches "a scoped
   caller may not create an organization / root".
6. `UPDATE organization_units SET parent_unit_id = @newParent, updated_at = now()
   WHERE id = @unitId;` then
   `UPDATE organization_units SET organization_id = @toOrg, updated_at = now()
   WHERE id IN (SELECT id FROM org_unit_descendants(@unitId));`
   (parent set first so the moved root's row is consistent; the descendant batch
   only touches `organization_id`, which the relaxed trigger no longer cross-checks;
   cycle check still runs but the tree shape is unchanged by the second statement).
7. Endpoint audits `before {organizationId: fromOrg, parentUnitId: old}` /
   `after {organizationId: toOrg, parentUnitId: new, subtreeUnitsMoved: n}`.

### Blast radius — assessed
- `authorized_org_units` / `authorized_organizations` / `has_permission` /
  `user_effective_access`: all walk the **live** tree (`org_unit_descendants` on
  `parent_unit_id`; `authorized_organizations` joins `organization_units.organization_id`).
  After the move they immediately reflect the new structure — **no function change
  needed**. Effect: a `scopes` row naming an ancestor in the *old* org stops
  covering the moved subtree; a scope in the *new* org's tree starts covering it.
  This is the intended semantics of a move, but it is a **silent grant change** for
  every group scoped anywhere near either attachment point.
- Denormalised `organization_unit_id` columns (`connector_target`,
  `federated_camera`, `detection_event`, `federation_event`, `camera_status_history`,
  `cameras`): store the **unit id**, not the org id. They keep pointing at the same
  (now-moved) unit, and `authorized_organizations`/`has_permission` resolve org via
  the live join — so they stay consistent automatically. No backfill.
- `cameras.geographic_area_id` (v1.11): geography dimension, untouched by an org move.
- Anything caching resolved scope: the API-key ~45s grant cache and the user access
  token (~15 min, `tokenver`) — a moved subtree's access changes apply on cache
  lapse / next refresh, same as any other scope edit. Acceptable; note in
  `docs/OPERATIONS.md`.
- **Reporting / audit:** historic `config_audit` rows for units in the subtree now
  describe a unit whose org differs from "today". Snapshots, so they stay truthful
  about the time they were written. Fine.

### Files
`db/versions/v1.12.sql` (function relax),
`db/objects/functions/assert_org_unit_acyclic.sql`,
`OrganizationRepository.cs`, `HierarchyEndpoints.cs` (new route or extend
`UpdateUnitAsync` — **recommend a dedicated `POST /api/v1/organization-units/{id}/move`**
so the destructive, unscoped-only operation is not hidden inside a general PUT),
`Contracts.cs` (`MoveUnitRequest(Guid NewParentUnitId)`),
`docs/DEPARTMENT-SCHEMA.md`, `docs/AUTHORIZATION.md`, `docs/MODEL-1-API-PLAN.md`,
`docs/OPERATIONS.md` (cache-lapse note), OpenAPI.

---

## Consolidated file list

### New
- `db/versions/v1.12.sql` — roles.status CHECK (`+DRAFT +ARCHIVED`);
  access_groups.status data-migrate `DISABLED→INACTIVE` + CHECK; relax
  `assert_org_unit_acyclic()`.

### db/objects (keep in sync with v1.12)
- `db/objects/tables/roles.sql`
- `db/objects/tables/access_groups.sql`
- `db/objects/functions/assert_org_unit_acyclic.sql`

### Storage
- `Repositories/OrganizationRepository.cs` — `ActivateUnitAsync`; `UpsertUnitAsync`
  advisory-lock + typed result + self/descendant pre-check; `MoveUnitToOrganizationAsync`
- `Repositories/GeographyRepository.cs` — `ActivateAreaAsync`; `UpsertAreaAsync`
  advisory-lock on re-parent
- `Repositories/RoleRepository.cs` — DRAFT default; `DeleteAsync` → `ARCHIVED` status
  update; `ListAsync(bool includeInactive, …)`; `RoleDetail` + queries enriched
  (timestamps, usage, permission metadata, using-groups); `GetStatusAsync`
- `Repositories/AccessGroupRepository.cs` — `created_at/updated_at` in `LoadAsync`
  + `AccessGroupDetail`/`GroupRow`
- `Repositories/Records.cs` — `ActivateResult`, `UnitWriteResult`/`UnitWriteError`,
  `MoveOrgResult`/`MoveOrgError`, `RoleWriteError.InvalidStatus`

### Api
- `Endpoints/HierarchyEndpoints.cs` — INACTIVE-guard on both PUTs; `/activate` ×2;
  remove re-parent 400 ×2; `/organization-units/{id}/move`
- `Endpoints/RoleEndpoints.cs` — `includeInactive` param; `ToResponse` rewrite;
  `DeleteAsync` audit verb; `Problem` mapping for `InvalidStatus`
- `Endpoints/AccessGroupEndpoints.cs` — P11 role-status 409; P12 `ConfirmUnscoped`
  body + 403→409; `DISABLED`→`INACTIVE` strings
- `Contracts/Contracts.cs` — `ActivateGroupRequest`, `MoveUnitRequest`
- `Contracts/Responses.cs` — `RoleResponse` (+ `RolePermissionDetail`,
  `RoleUsingGroup`); `AccessGroupResponse` (+ timestamps)

### Docs
`docs/MODEL-1-API-PLAN.md`, `docs/AUTHORIZATION.md`, `docs/RBAC-LOGICAL-FLOW.md`,
`docs/DEPARTMENT-SCHEMA.md`, `docs/GEOGRAPHY-SCHEMA.md`, `docs/OPERATIONS.md`,
`CLAUDE.md` (Repository State + Non-Negotiables note on cross-org move / DRAFT roles).

### New SQL version
**`db/versions/v1.12.sql`** — "role & access-group status lifecycle; cross-org unit move".

---

## The 4 riskiest spots

1. **P2b cross-org batch update vs. the per-row trigger.** A one-statement
   `UPDATE ... WHERE id IN (descendants)` fires `assert_org_unit_acyclic` per row in
   unspecified order and trips the same-organization check on a half-applied batch.
   The plan is to *relax the trigger* (drop that one assertion) and enforce the
   invariant in the repository. That moves a data-integrity guarantee from the DB to
   app code — needs explicit sign-off. Alternative (ordered top-down recursive
   update) is more code and still races with concurrent inserts into the subtree.

2. **P2b silent authorization shift.** Moving a subtree between orgs silently
   changes which `scopes` rows cover every unit in it — groups scoped near the old
   or new attachment point gain or lose access with no per-group audit row (only the
   move itself is audited). At 80k cameras a mis-move is a large, quiet exposure.
   Mitigation: unscoped-`organization.manage`-only, dedicated `/move` route (not a
   silent PUT), audit `subtreeUnitsMoved` count, and a follow-up "what changed"
   report is worth considering.

3. **Re-parent (P1/P2a) races with cascade-deactivate.** `DeactivateUnitAsync` /
   `DeactivateAreaAsync` serialize on a tree-wide advisory lock; the PUT re-parent
   path currently takes no lock. Without adding the same lock + `FOR UPDATE` re-read
   to `UpsertUnitAsync` / `UpsertAreaAsync`, concurrent "deactivate ancestor" +
   "re-parent live subtree under a node in that ancestor" reintroduces finding 5-H2
   (active node under inactive ancestor) or detaches a subtree. Easy to miss because
   the single-threaded tests pass.

4. **`roles.status` / `access_groups.status` CHECK swap ordering in v1.12.sql.**
   The migration must `DROP CONSTRAINT` → `UPDATE` existing rows
   (`DISABLED`→`INACTIVE`) → `ADD CONSTRAINT`. Get the order wrong and the file
   fails to apply, which (per `PostgresFixture`) breaks the entire integration
   suite, not just one test. Also verify nothing in `db/objects` or a later version
   file re-asserts the old CHECK text. Lower-probability but repo-wide blast radius.

### Secondary decisions to confirm
- P8 soft-delete target: `ARCHIVED` (recommended) vs `INACTIVE`.
- P5 `includeInactive=true` includes `ARCHIVED`? (recommended: no).
- P12 status code: 409 (recommended) vs keep 403.
- P11: `RoleRepository.GetStatusAsync` one-liner (recommended) vs widen `AccessGroupDetail`.
- P2b surface: dedicated `POST /{id}/move` (recommended) vs overloaded PUT.
- P10: keep route `/disable` (recommended) vs rename `/deactivate`.
- P6 list payload: `UsingGroups` on `GET /{id}` only (recommended) vs on list too.
- Cross-org move authority: unscoped-only (recommended) vs two-ended reach check.

---

## As-built deviations (v1.12 ship, 2026-09-08)

1. **Same-organization invariant → deferrable constraint trigger**, not "drop the assertion,
   enforce in the repository" (design option 1). New `assert_org_unit_same_org()` +
   `CREATE CONSTRAINT TRIGGER trg_org_unit_same_org ... DEFERRABLE INITIALLY IMMEDIATE`.
   `assert_org_unit_acyclic()` keeps self-parent + cycle only. Reason: acceptance criterion
   **P2b-AC5** requires the DB to still reject a malformed direct single-row `UPDATE` (org
   changed, parent left behind); option 1 could not. `MoveUnitToOrganizationAsync` issues
   `SET CONSTRAINTS ALL DEFERRED` (the named form doesn't resolve — the repo connection's
   `search_path` isn't `federation`; the only other statement in the transaction is the audit
   insert). `CREATE CONSTRAINT TRIGGER` takes no `UPDATE OF` column list, so it fires on every
   `organization_units` row update — the function no-ops immediately for a root and is one
   indexed lookup otherwise.

2. **Out-of-reach re-parent destination returns 403, not 404** (design P1-R7 / P2a-R6 asked for
   404). `OrganizationRepository` / `GeographyRepository` already throw `ForbiddenException`
   (→403) for every out-of-reach write and existing tests assert it; splitting just the
   re-parent path to 404 would break the convention.

3. **Structural re-parent failures share one Problem title.** Self / descendant / different-org
   / wrong-route all return **400 `Re-parent rejected`** with the specific reason in `detail`
   (the string contains "its own parent" / "beneath itself" / "between organizations"), rather
   than a distinct title per case. X-2 (never a raw 500) holds.

4. **P6 permission metadata is additive.** `RoleResponse.permissions` stays the string-code
   list (no client/test break); a new `permissionDetails: [{code,name,category,description}]`
   was added alongside, rather than replacing `permissions` with objects + a `permissionCodes`.

5. **P2b audit `before.parentUnitId` is `null`** — the repository result doesn't surface the
   pre-move parent id. `before.organizationId` and every `after.*` field
   (`subtreeUnitsMoved`, `affectedGroups`, `camerasFollowing`, `targetsFollowing`,
   `confirmScopeImpact`) are accurate.

6. **P11 / P12 endpoint-level 409 shaping and Problem Details are not covered by HTTP tests** —
   the integration suite has no `WebApplicationFactory` (pre-existing, documented gap).
   Repository-observable behaviour (`AccessGroupDetail.RoleStatus` / `GrantsEffective`, the
   scope-presence helpers, the `updated_at` bump, role soft-delete semantics) is covered.

7. **P8 has no `confirmInUse` gate.** Per the project-owner decision, a role in use is
   soft-deleted with no confirmation flag and no change to the referencing access groups; they
   keep their `role_id` and grant nothing while the role is `INACTIVE`.
