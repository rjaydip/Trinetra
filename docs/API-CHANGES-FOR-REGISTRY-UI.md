# API changes since the `d/registry-ui` fork

Everything the `d/registry-ui` frontend (`frontend/src/api/`) must adapt to when it rebases onto
`main`, through **`v1.11`** (drop sites, editable roles, group lifecycle) and **`v1.12`** (role
& access-group status lifecycle, cross-organization unit move, `role.read`). Grouped
**breaking → new → additive**. Each change names where it is documented: the live OpenAPI
(`.WithSummary` / `.WithDescription` on every route, served at `/scalar` and
`/openapi/v1.json`) plus the `docs/*.md` reference.

The frontend client of record is `frontend/src/api/endpoints.ts` + `frontend/src/api/models.ts`.

---

## 1. BREAKING

### 1.1 Auth is now an access + refresh token pair

| Route | Before | After |
|---|---|---|
| `POST /api/v1/auth/login` | `LoginResponse { token, expiresAt, mustChangePassword }` | `AuthTokenResponse { accessToken, accessExpiresIn, refreshToken, refreshExpiresIn, mustChangePassword }` |
| `POST /api/v1/auth/password` | `LoginResponse` | `AuthTokenResponse` |
| `POST /api/v1/auth/refresh` | — | **new.** Body `{ refreshToken }` → `AuthTokenResponse`. Each call **rotates** the refresh token |
| `POST /api/v1/auth/logout` | — | **new.** Body `{ refreshToken }` → `204`. Revokes the refresh token |

- `accessToken` is a short-lived (~15 min) JWT, still sent as `Authorization: Bearer`.
- `refreshToken` is an opaque ~8 h sliding secret. Store it; call `/auth/refresh` before the
  access token expires; replace **both** tokens from every response.
- Frontend work: `frontend/src/auth/` token store + the `request()` wrapper's 401 handling.
- **Docs:** OpenAPI on all four routes; `docs/AUTHORIZATION.md` §1 (Principals).

### 1.2 `sites` are removed

The `sites` table and every `/api/v1/sites` route are gone. A camera / VMS target / event
attaches directly to a `geographic_area_id` **at any level** of the geographic hierarchy.

| Removed | Replacement |
|---|---|
| `GET /api/v1/sites`, `GET /api/v1/sites/{id}`, `POST /api/v1/sites`, `PUT /api/v1/sites/{id}` | none — pick a `geographicAreaId` from `GET /api/v1/geographic-areas` |
| `GET /api/v1/geographic-areas/{id}/sites` | none |
| `SiteResponse`, `SiteRequest` types | deleted |
| frontend `api.reference.sites`, `api.admin.geography.listSites` / `createSite` | delete; use area endpoints |

- The camera "site picker" step in onboarding becomes an **area picker** (any level).
- A camera keeps its own `latitude` / `longitude`, so the map loses nothing.
- **Docs:** `docs/GEOGRAPHY-SCHEMA.md` (rewritten), `docs/CAMERA-SCHEMA.md`,
  `docs/MODEL-1-API-PLAN.md`, `docs/TECHNICAL-DESIGN.md` (API surface list).

### 1.3 `siteId` → `geographicAreaId` on every contract

| Contract | Field |
|---|---|
| `CameraWriteRequest` (`POST` / `PUT /cameras`) | `siteId` → **`geographicAreaId`** (required, `Guid`) |
| `CameraResponse` | `siteId` → **`geographicAreaId`** |
| `CameraPatchRequest` JSON key | `"siteId"` → **`"geographicAreaId"`** |
| `GET /api/v1/cameras` query | `siteId` param **removed**; `geographicAreaId` param now matches an area **and its descendants** |
| `UnreconciledCameraResponse` | `siteId` → **`geographicAreaId`** (nullable) |
| `CreateFromFederatedRequest` (`POST /cameras/from-federated`) | `siteId` → **`geographicAreaId`** (nullable) |
| `ConnectorTargetRequest` (`POST` / `PUT /vms`) | `siteId` → **`geographicAreaId`** (nullable) |
| `VmsResponse` | `siteId` → **`geographicAreaId`** |
| `NormalisedEvent` / event feed | `siteId` → **`geographicAreaId`** (nullable) |

- **Docs:** OpenAPI on `POST /cameras`, `GET /cameras`, `PATCH /cameras/{id}`, `POST /vms`,
  reconciliation routes; `docs/CAMERA-SCHEMA.md`, `docs/MODEL-1-API-PLAN.md`.

### 1.4 Deactivation-conflict 409 body changed

`POST /api/v1/geographic-areas/{id}/deactivate` and `.../organization-units/{id}/deactivate`,
when refused with `409`:

```diff
- { "affectedAreas": [...], "affectedSites": [...], "resolutions": ["cascade","reparent"] }
+ { "affectedChildren": [...], "resolutions": ["cascade","reparent"] }
```

`reparent` no longer takes `newAreaIdForSites` — only `newParentId`. The frontend's
"lock hierarchy strategy after conflict" logic (`HierarchyPage`) parses this body.

- **Docs:** OpenAPI on both `/deactivate` routes; `docs/GEOGRAPHY-SCHEMA.md` (Deactivation).

### 1.5 `RoleResponse` shape changed

```diff
- RoleResponse { id, code, name, description, isSystem }
+ RoleResponse { id, code, name, description, isSystem, status, customized, permissions[],
+                permissionDetails?, usedBy? }
```

- `status` is `DRAFT | ACTIVE | INACTIVE`. **A custom role is created `DRAFT`** and grants
  nothing to any group until it is `PUT` to `ACTIVE`. Presets are seeded `ACTIVE`.
- `permissionDetails` (name/category per code) and `usedBy` (the access groups on this role,
  filtered to what the caller can see) are populated on `GET /api/v1/roles/{id}` only.
- `GET /api/v1/roles` is now served by the roles router and returns **every** role, each with
  its `permissions` array — `RolesPage` can drop its separate per-role permission fetch.

- **Docs:** OpenAPI `GET /api/v1/roles`; `docs/RBAC-LOGICAL-FLOW.md` §9 / §15.

### 1.6 `role.read` is a new permission — `group.read` no longer covers roles/permissions

`GET /api/v1/roles`, `GET /api/v1/roles/{id}` and `GET /api/v1/permissions` now require
**`role.read`**, not `group.read`. `v1.12` grants `role.read` to every role that already had
`group.read` (STATE_ADMIN, DEPARTMENT_ADMIN, …) plus SUPER_ADMIN, so seeded groups are
unaffected — but a **custom** group built on `group.read` alone will 403 on the role picker
until `role.read` is added.

- **Docs:** OpenAPI on the three routes; `docs/AUTHORIZATION.md` §4.

### 1.7 Access-group status: `DISABLED` → `INACTIVE`

`v1.12` renames the disabled state for consistency with roles / units / areas / users.

```diff
- access group status ∈ { DRAFT, ACTIVE, DISABLED }
+ access group status ∈ { DRAFT, ACTIVE, INACTIVE }
```

The route is still `POST /api/v1/access-groups/{id}/disable`; it now writes `INACTIVE`. Any UI
that string-matches `"DISABLED"` in a status badge/filter must switch to `"INACTIVE"`.

- **Docs:** OpenAPI `/disable`; `docs/RBAC-LOGICAL-FLOW.md` §25.

---

## 2. NEW ENDPOINTS the UI now needs

### 2.1 Organization / geography editing + lifecycle

| Route | Purpose |
|---|---|
| `GET /api/v1/organization-units/{id}` | read one unit (was list-only) |
| `PUT /api/v1/organizations/{id}` | edit `code` / `name` / `organizationType` / `description`. Unscoped `organization.manage` only |
| `PUT /api/v1/organization-units/{id}` | edit `code` / `name` / `unitType` / `description` / `geographicAreaId`. **`parentUnitId` MAY change** — the unit + its whole subtree re-parent within the same organization. Refused: parent = self or a descendant (400), inactive new parent (400), different-organization parent (400 — use `/move`), re-parent to root i.e. `parentUnitId: null` needs unscoped `organization.manage` (403). Editing an `INACTIVE` unit → 409, activate first |
| `POST /api/v1/organization-units/{id}/activate` | `INACTIVE` → `ACTIVE`. 409 if the parent is itself `INACTIVE`. Does not cascade |
| `POST /api/v1/organization-units/{id}/move` | Move a unit under a parent in a **different** organization; rewrites the whole subtree's organization. `MoveUnitRequest { newParentUnitId, confirmScopeImpact }` → `MoveUnitResponse { fromOrganizationId, toOrganizationId, subtreeSize, camerasFollowing, targetsFollowing, affectedGroups[] }`. Unscoped `organization.manage` only (403). If any access group's org scope points into the subtree, 409 with `affectedGroups` until re-sent with `confirmScopeImpact: true` |
| `PUT /api/v1/geographic-areas/{id}` | edit `code` / `name` / `areaType` / `description`. **`parentAreaId` MAY change** (advisory-locked, same guard family: self/descendant 400, inactive parent 400, level-order containment 400) |

`status` on the PUTs is left as-is — use `/activate` and `/deactivate`.

- **Docs:** OpenAPI on each; `docs/API-PLAN-HIERARCHY-RBAC.md` (the P1/P2a/P2b contract),
  `docs/IMPL-NOTES-HIERARCHY-RBAC.md` (as-built), `docs/DEPARTMENT-SCHEMA.md`,
  `docs/GEOGRAPHY-SCHEMA.md`.

### 2.2 Access-group lifecycle + edit

| Route | Purpose |
|---|---|
| `PUT /api/v1/access-groups/{id}` | edit `code` / `name` / `description` / `roleId`. Re-runs the escalation guard. `status` untouched |
| `POST /api/v1/access-groups/{id}/activate` | `DRAFT` / `INACTIVE` → `ACTIVE`. **403** if the group has no ORGANIZATION *or* no GEOGRAPHY scope and the caller is not unscoped for `group.manage` on that dimension. Re-activating an already-`ACTIVE` group is a 204 no-op |
| `POST /api/v1/access-groups/{id}/disable` | → `INACTIVE` (was `DISABLED` — see 1.7); the grant stops immediately |

Adding or removing a scope now also bumps `updatedAt`. `AccessGroupResponse` gained
`grantsEffective` (bool — true only when the group is `ACTIVE` **and** its role is `ACTIVE`, so a
UI can show "assembled but not yet granting"), plus `createdAt` / `updatedAt`.

- **Docs:** OpenAPI on each; `docs/RBAC-LOGICAL-FLOW.md`, `docs/AUTHORIZATION.md` §5.

### 2.3 Role CRUD (custom + editable presets)

| Route | Purpose |
|---|---|
| `GET /api/v1/roles/{id}` | one role + permission codes + `permissionDetails` + `usedBy` |
| `POST /api/v1/roles` | `RoleWriteRequest { code, name, permissions[], description?, status? }` → creates a custom role, **status `DRAFT`** unless overridden |
| `PUT /api/v1/roles/{id}` | replaces `name` / `description` / `status` and the **entire** permission set. `code` in the body is ignored. Flip `status` to `ACTIVE` here to make the role grant |
| `DELETE /api/v1/roles/{id}` | **soft-delete → `INACTIVE`** (preserves history). A preset cannot be deleted (409). A role in use is *not* blocked — it goes `INACTIVE` and its groups simply grant nothing onward |

Read routes need `role.read`; writes need **`role.manage`**. Rules the UI should surface:
- `SUPER_ADMIN` → `409` on any edit/delete.
- Editing/disabling a preset (`isSystem`) → `409` unless the caller holds `role.manage`
  **unscoped**. Scoped `role.manage` holders get custom roles only.
- A permission the caller does not hold themselves → `403` naming the excess.
- Duplicate `code` → `409`.

- **Docs:** OpenAPI on each route (the 409/403 reasons are in the descriptions);
  `docs/RBAC-LOGICAL-FLOW.md` §9, `docs/AUTHORIZATION.md` §5, `docs/API-PLAN-HIERARCHY-RBAC.md`,
  `docs/API-REVIEW-FINDINGS.md` F7.

### 2.4 Auth audit (optional for the UI)

`GET /api/v1/auth-audit` — login/lockout/token history. `authaudit.read` (SUPER_ADMIN only).
Not required for the registry UI unless an audit view is wanted.

- **Docs:** OpenAPI; `docs/AUTHORIZATION.md` §6.

---

## 3. ADDITIVE (no client change required, but new data available)

| Contract | New field(s) |
|---|---|
| `OrganizationUnitRequest` | `description?`, `geographicAreaId?` (descriptive "home area" — **never** an auth input) |
| `OrganizationUnitResponse` | `description`, `geographicAreaId` |
| `GeographicAreaRequest` / `GeographicAreaResponse` | `description` |
| `AreaTypeResponse` | unchanged shape, but `GET /geographic-areas/types` is now backed by a seeded registry and `area_type` is a required FK into it — populate the picker from here, not hard-coded. `levelOrder` also governs nesting: creating/editing an area whose level is not strictly finer than its parent's (or coarser than a child's) → **400** |

- `geographic_areas.code` is now unique **per parent**, not globally — `WARD-1` can recur under
  different districts. A UI that resolves an area by bare code must handle ambiguity.
- **Docs:** `docs/GEOGRAPHY-SCHEMA.md`, `docs/DEPARTMENT-SCHEMA.md`.

---

## 4. Not changed (frontend already correct)

`GET /api/v1/cameras` paging, `GET /api/v1/gis/*`, `POST /api/v1/vms/{id}/test`, credential
`PUT`/`status`, watchlist, api-keys, `GET /api/v1/overview`, camera health/maintenance — all
unchanged in shape. The camera list `geographicAreaId` filter already existed; only `siteId`
was removed from it.
