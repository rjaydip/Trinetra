# API changes since the `d/registry-ui` fork

Everything the `d/registry-ui` frontend (`frontend/src/api/`) must adapt to when it rebases onto
`main`, through **`v1.11`** (drop sites, editable roles, group lifecycle), **`v1.12`** (role
& access-group status lifecycle, cross-organization unit move, `role.read`) and the
**list-pagination wave** (§5). Grouped **breaking → new → additive**. Each change names where
it is documented: the live OpenAPI (`.WithSummary` / `.WithDescription` on every route, served
at `/scalar` and `/openapi/v1.json`) plus the `docs/*.md` reference.

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

### 1.4a Deactivating a missing node now 404s (and no phantom audit row)

Same two `/deactivate` routes:

- A node **id that is unknown or out of your scope** → **`404`** for an unscoped caller
  (`organization.manage` / `geography.manage` held estate-wide); a **scoped** caller still gets
  **`403`**. Previously an unscoped caller got a silent `204` plus a false `ACTIVE → INACTIVE`
  audit row for a node that was never touched.
- A node that is **already `INACTIVE`** stays an **idempotent `204`** — but it no longer writes
  an audit row. Retrying a deactivate after a timeout is still safe.

If `HierarchyPage` treated `204` as "done", that is unchanged; it must additionally treat `404`
on these routes as "already gone", not a hard error.

### 1.4b Validation errors that were `500` are now `400`

- Any string field longer than its column (most visibly `code`) → **`400` "Value too long"**
  (was: `500`).
- **Camera `PATCH`** with an over-long optional string (`manufacturer`, `model`,
  `serialNumber`, `ipAddress`, `protocol`, `streamReference`, `credentialReference`) → **`400`**
  with `"<field> is at most <n> characters."` (was: the value was **silently truncated** and
  saved).
- `POST/PUT` a VMS target whose `endpoint` is not an `http(s)` URL (e.g. `file://…`) → **`400`
  "Invalid endpoint"** (was: accepted). A bare `host` or `host:port` is still fine — it is read
  as `http://host[:port]`.
- `POST /api/v1/access-groups/{id}/scopes` with fields that do not match `scopeType` (an
  `ORGANIZATION` scope with no `organizationUnitId`, a `GEOGRAPHY` scope carrying a
  `resourceId`, etc.) → **`400` "Scope fields do not match its type"** with a specific message
  (was: a generic `400`, or in some shapes a `500`).

### 1.4c `GET /api/v1/vms/{id}/capabilities` — 404 body no longer distinguishes the reason

A `404` is now returned with **no body detail** whether the target is outside your scope or
simply has not been probed by a worker yet. The two are deliberately indistinguishable; do not
show the user "target exists but not probed" copy based on this response.

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

## 5. LIST PAGINATION (additive, but changes response shape when opted in)

**Opt-in.** These list endpoints now accept `?page` (1-based) and `?pageSize` (default 50,
clamped to the endpoint's max):

`GET /users` · `/access-groups` · `/access-groups/{id}/members` · `/organizations` ·
`/organizations/{id}/units` · `/geographic-areas` · `/geographic-areas/{id}/children` ·
`/api-keys` · `/vms` · `/vms/{id}/cameras` · `/watchlist` · `/watchlist/alerts` · `/worker-health`

### Two response shapes

| Request | Body |
|---|---|
| **no `?page`** | the **bare array**, unchanged from today — but capped at a safety maximum (1000; 2000 for `/vms/{id}/cameras`; 5000 for the GIS feed) |
| **with `?page`** | `{ "items": [...], "page": N, "pageSize": M, "total": T, "totalPages": P }` |

### Headers (both shapes)

- **`X-Total-Count`** — the full match count, always set.
- **`X-Result-Capped: true`** — set only on the bare-array form when the safety cap trimmed the
  result. A client that shows a list *must* check this or it will silently display a partial
  set.
- **`X-Page` / `X-Page-Size`** — echoed on the GIS feed only (see below).

> **CORS:** the API now sends `Access-Control-Expose-Headers: X-Total-Count, X-Result-Capped,
> X-Page, X-Page-Size`. A browser `fetch` cannot read these otherwise.

### The GIS feed is the exception

`GET /api/v1/gis/cameras` still returns a GeoJSON `FeatureCollection` (never a wrapper).
Pagination is entirely via `?page` / `?pageSize` + the `X-*` headers above. Without `?page` it
caps at 5000 features and sets `X-Result-Capped`.

### New filters

- **`GET /watchlist`** — `?active` (`true` / `false`, omit for both).
- **`GET /watchlist/alerts`** — `?acknowledged` (`true` / `false`), `?plate` (exact normalised
  plate), `?entryId`, `?severity`, `?from` / `?to` (on the raised time).

### Detection search is *not* paginated

`GET /api/v1/detections` (`15-M2`) stays a bare array. It now enforces a **31-day max window**
(400 otherwise) and caps `limit` at **500** — or **5000** when `?plateNumber` is set.

### Not paginated (bounded by design)

`GET /api/v1/cameras` and `/events` already use keyset cursors — unchanged. `/roles`,
`/permissions`, `/geographic-areas/types` are small reference sets.

- **Docs:** `docs/API-REVIEW-FINDINGS.md` "PAGINATION WAVE"; each route's OpenAPI description
  carries the boilerplate. `frontend/src/api/endpoints.ts` — each of the 13 routes above needs a
  union return type and to branch on "did I send `?page`".

---

## 6. Not changed (frontend already correct)

`GET /api/v1/cameras` paging, `POST /api/v1/vms/{id}/test`, credential `PUT`/`status`,
`GET /api/v1/overview`, camera health/maintenance — all unchanged in shape. The camera list
`geographicAreaId` filter already existed; only `siteId` was removed from it.
