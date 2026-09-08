# API changes since the `d/registry-ui` fork

Everything the `d/registry-ui` frontend (`frontend/src/api/`) must adapt to when it rebases onto
`main` + the uncommitted v1.11 work. Grouped **breaking → new → additive**. Each change names
where it is documented: the live OpenAPI (`.WithSummary` / `.WithDescription` on every route,
served at `/scalar` and `/openapi/v1.json`) plus the `docs/*.md` reference.

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
+ RoleResponse { id, code, name, description, isSystem, status, customized, permissions[] }
```

`GET /api/v1/roles` still works (same path) but is now served by the new roles router and
returns **every** role (active or not), each with its `permissions` array. `RolesPage` can drop
its separate per-role permission fetch.

- **Docs:** OpenAPI `GET /api/v1/roles`; `docs/RBAC-LOGICAL-FLOW.md` §9 / §15.

---

## 2. NEW ENDPOINTS the UI now needs

### 2.1 Organization / geography editing

| Route | Purpose |
|---|---|
| `GET /api/v1/organization-units/{id}` | read one unit (was list-only) |
| `PUT /api/v1/organizations/{id}` | edit `code` / `name` / `organizationType` / `description`. Unscoped `organization.manage` only |
| `PUT /api/v1/organization-units/{id}` | edit `code` / `name` / `unitType` / `description` / `geographicAreaId`. **Refuses a `parentUnitId` change with 400** — reparent via `/deactivate` `reparent` |
| `PUT /api/v1/geographic-areas/{id}` | edit `code` / `name` / `areaType` / `description`. Same **400 on `parentAreaId` change** |

`status` is never set through these — use `/deactivate`.

- **Docs:** OpenAPI on each; `docs/DEPARTMENT-SCHEMA.md` (API Examples), `docs/GEOGRAPHY-SCHEMA.md`.

### 2.2 Access-group lifecycle + edit

| Route | Purpose |
|---|---|
| `PUT /api/v1/access-groups/{id}` | edit `code` / `name` / `description` / `roleId`. Re-runs the escalation guard. `status` untouched |
| `POST /api/v1/access-groups/{id}/activate` | `DRAFT` / `DISABLED` → `ACTIVE`. **403** if the group has no ORGANIZATION *or* no GEOGRAPHY scope and the caller is not unscoped for `group.manage` on that dimension |
| `POST /api/v1/access-groups/{id}/disable` | → `DISABLED`; the grant stops immediately |

`GET /api/v1/roles` **moved off** the access-groups router into the roles router — same path,
same `group.read` permission, no client change needed beyond the response-shape note in 1.5.

- **Docs:** OpenAPI on each; `docs/RBAC-LOGICAL-FLOW.md`, `docs/AUTHORIZATION.md` §5.

### 2.3 Role CRUD (custom + editable presets)

| Route | Purpose |
|---|---|
| `GET /api/v1/roles/{id}` | one role + its permission codes |
| `POST /api/v1/roles` | `RoleWriteRequest { code, name, permissions[], description?, status? }` → creates a custom role |
| `PUT /api/v1/roles/{id}` | replaces `name` / `description` / `status` and the **entire** permission set. `code` in the body is ignored |
| `DELETE /api/v1/roles/{id}` | custom roles only |

Gated on the new **`role.manage`** permission. Rules the UI should surface:
- `SUPER_ADMIN` → `409` on any edit/delete.
- Editing/disabling/deleting a preset (`isSystem`) → `409` unless the caller holds `role.manage`
  **unscoped**. Scoped `role.manage` holders get custom roles only.
- A permission the caller does not hold themselves → `403` naming the excess.
- Deleting a role a group still uses → `409`.
- Duplicate `code` → `409`.

- **Docs:** OpenAPI on each route (the 409/403 reasons are in the descriptions);
  `docs/RBAC-LOGICAL-FLOW.md` §9, `docs/AUTHORIZATION.md` §5, `docs/API-REVIEW-FINDINGS.md` F7.

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
