# Frontend Findings — Camera Registry & Admin UI

Review notes on the current frontend. Each item is a defect or a gap to close.
None are implemented yet. File references point at the backend contracts the UI
has to line up with.

The frontend source is not in this repo yet (`frontend/` holds only
`node_modules/`), so the references below are to the API side.

---

## 1. Change-password: "new == old" error is swallowed

**What happens:** `POST /auth/password` validates the request, and if the new
password equals the current one it returns **400**. The frontend does not show
this — the user gets no feedback and assumes the change worked.

**Backend:** `src/Trinetra.Federation.Api/Endpoints/AuthEndpoints.cs`
(`MapPost("/password", ChangePasswordAsync)` — summary: *"rejects a new value
equal to the old one"*). Response also carries `mustChangePassword`.

**Fix:**
- Render the 400 problem detail on the change-password form.
- Add client-side check: block submit when new == current, and when
  new != confirm.
- Honour `mustChangePassword` in the login response
  (`LoginResponse.MustChangePassword`) — route the user straight to the
  change-password screen.

---

## 2. Latitude / longitude: use a real map picker

**What happens:** lat/long are free-text number inputs. Error-prone, and users
can't tell if the point is right.

**Backend:** `CameraWriteRequest.Latitude` / `.Longitude` (required `double`) —
`src/Trinetra.Federation.Api/Contracts/CameraContracts.cs`. DB stores
`DECIMAL(10,7)` with range CHECKs (`db/versions/v1.6.sql`), so the map must emit
≤7 decimal places.

**Fix:**
- Embed a map (Leaflet / MapLibre). Click or drag a pin → two-way bind to the
  lat/long fields; manual entry still allowed and moves the pin.
- Same picker should also help set `Azimuth` (drag a direction handle) since
  that drives coverage.
- Reuse the GIS map source already exposed at `GET /api/v1/gis/cameras`
  (GeoJSON `FeatureCollection`) as the base layer showing existing cameras.

---

## 3. Camera registry form: dropdowns instead of free text

**What happens:** every field is a text input, including ones with a fixed value
set.

**Should be dropdowns (values come from the API / reference data):**

| Field | Source |
|---|---|
| `CameraType` | reference list (fixed set) |
| `Protocol` | fixed set (RTSP / ONVIF / HTTP / …) |
| `Manufacturer` | reference / typeahead |
| `OperationalStatus`, `ConnectivityStatus`, `MaintenanceStatus` | fixed enums — see `CameraResponse` |
| `OrganizationUnitId` | `GET /api/v1/organizations/{id}/units` |
| `SiteId` | `GET /api/v1/sites` |
| `VmsId` | `GET /api/v1/vms` |

**Backend:** `CameraContracts.cs` (`CameraWriteRequest`). Note enums are
serialized as **strings** on the way out but the host registers no
`JsonStringEnumConverter` — confirm the exact accepted string values with the
API team before hard-coding dropdown options.

---

## 4. "Next page" on the registry page — meaning unclear

**What it is:** the registry list is **cursor-paged**. `GET /api/v1/cameras`
returns `CameraPage { Items, NextCursor }`; a null `NextCursor` means end of
results (`CameraContracts.cs`).

**Fix:**
- It is pagination, not a wizard step — label it "Next" / "Load more" and
  disable it when `NextCursor` is null.
- Pass the returned cursor back as the query param on the next call (confirm
  param name with `CameraEndpoints.cs` `ListAsync`).
- Consider infinite-scroll or numbered pages only if the API also returns a
  total count (it currently does not).

---

## 5. Coverage summary: input box not needed

**What it is:** `GET /api/v1/gis/coverage` returns `CoverageSummaryResponse`
— just bucketed counts (`Buckets`), no free-text parameter.

**Backend:** `src/Trinetra.Federation.Api/Endpoints/GisEndpoints.cs`,
`CoverageSummaryResponse` in `CameraContracts.cs`.

**Fix:** remove the input box unless it is a filter that maps to a real query
param on that endpoint. If it is meant to be a filter (org / site / area),
make it a dropdown bound to the same sources as item 3.

---

## 6. Bulk import: ship a sample file

**What happens:** no template is offered, so users don't know the shape.

**Backend today:** `POST /api/v1/cameras/bulk-import` accepts **JSON only** —
`BulkImportRequest { string Mode, CameraWriteRequest[] Items }`
(`CameraEndpoints.cs:187`, `CameraContracts.cs:175`). Returns
`BulkImportResult { created, updated, failed, results[] }`. `Mode` selects
insert-only vs. upsert — confirm allowed values.

**Fix (two options):**
- **Minimum:** add a "Download sample JSON" link on the import screen,
  generated from `CameraWriteRequest` (required fields filled, optionals shown
  empty), plus a one-row example.
- **Preferred (needs backend work):** accept a **CSV** upload. User downloads a
  CSV template (header row = `CameraWriteRequest` field names, required columns
  marked), fills it, uploads. Frontend parses CSV → `Items[]`, or a new
  `text/csv` branch is added to the endpoint. Surface `BulkImportResult.results`
  per-row so failures are visible.

---

## 7. Registry form: mark required vs. optional

**What happens:** the form doesn't indicate which fields are mandatory.

**Actually required by the API** (non-nullable in `CameraWriteRequest`):
`CameraCode`, `Name`, `OrganizationUnitId`, `SiteId`, `CameraType`, `Latitude`,
`Longitude`.

**Optional:** `Manufacturer`, `Model`, `SerialNumber`, `Altitude`,
`MountingHeight`, `Azimuth`, `Tilt`, `HorizontalFov`, `VerticalFov`,
`EffectiveRange`, `IpAddress`, `Port`, `Protocol`, `VmsId`, `StreamReference`,
`CredentialReference`, `InstallationDate`, and the three status fields.

> **Discrepancy to resolve with BA / API team:** the finding expects
> `IpAddress`, `Port`, `Protocol`, `Manufacturer` to be **required**, but the
> current contract makes them optional (a camera discovered via NVR may not
> have a directly reachable IP). Decide whether these are:
> - required only for **manually added** cameras, not NVR-discovered ones
>   (see item 8), or
> - always required (contract change in `CameraWriteRequest` + `v1.7` CHECKs).

**Fix:** mark required fields (`*`), group optional fields under a collapsible
"Additional details" section, and validate client-side against the list above.

---

## 8. NVR-based registration flow

**What happens:** most cameras are added by connecting an NVR/VMS, which
auto-discovers its cameras. The UI has no flow for this, and no way to enrich a
discovered camera with the details the NVR can't know.

**Backend available:**
- `GET /api/v1/vms/{id}/cameras` — lists cameras the connected VMS/NVR reports
  (`VmsEndpoints.cs`).
- `POST /api/v1/vms` / `PUT /api/v1/vms/{id}` / `POST /api/v1/vms/{id}/state` —
  register and manage the NVR connection.
- `POST /api/v1/cameras/bulk-import` (`Mode` = upsert) to persist the discovered
  set into the registry.

**Fix — proposed flow:**
1. "Add via NVR" → create/select the VMS connection, store its credential
   (item 8b below).
2. Fetch `GET /api/v1/vms/{id}/cameras`, show the discovered list.
3. For each camera, the NVR gives identity + stream; the UI collects the rest:
   `Latitude`, `Longitude`, `Altitude`, `MountingHeight`, `Azimuth`, `Tilt`,
   FOV, `EffectiveRange`, `OrganizationUnitId`, `SiteId`.
4. Submit as `bulk-import` (upsert). Show per-row result.
5. "Add manually" stays as the single-camera path where IP/port/protocol are
   required.

---

## 8b. Credentials: add a save-credential option

**What happens:** every camera and NVR needs credentials to connect; the UI has
no place to enter/store them.

**Backend available (already built):**
- `PUT /api/v1/vms/{id}/credential` — store the credential a connector uses for
  this target. Body: `{ Username, Password?, Token? }` (needs `Password` **or**
  `Token`). Permission: `credential.write`. Sealed application-side
  (AES-256-GCM). — `src/Trinetra.Federation.Api/Endpoints/CredentialEndpoints.cs`
- `GET /api/v1/vms/{id}/credential/status` — whether a credential is stored
  (`vms.read`). Use this to show a "credential set / not set" badge.
- The secret is never returned to the UI; `/credential/resolve` is worker-only
  (`credential.resolve`).

**Gaps:**
- The credential endpoint is **VMS-scoped only**. Cameras registered directly
  (not behind a VMS) reference `CredentialReference` on `CameraWriteRequest` but
  there is **no `PUT /api/v1/cameras/{id}/credential`**. Raise with the API team
  if direct-camera credentials are in scope.

**Fix:**
- On the VMS/NVR form, a "Credentials" section → `PUT .../credential`, with a
  "Test connection" button (`POST /api/v1/connection-tests` —
  `ConnectionTestEndpoints.cs`).
- Show the stored/not-stored badge from `/credential/status`.
- Never echo the password back; show only "credential on file, updated {date}".

---

## 9. Missing CRUD / admin pages

Current endpoint coverage (from `src/Trinetra.Federation.Api/Endpoints/`):

| Entity | Endpoints today | Missing |
|---|---|---|
| **Organization** | `GET /` `GET /{id}` `POST /` ; units: `GET`/`POST` `.../units`, `POST /organization-units/{id}/deactivate` | update, delete/reactivate, unit update — see also `api-review-findings` F5 |
| **Geography** (`/geographic-areas`, `/sites`) | `GET` list/one/children/ancestors/types, `POST /`, `POST /{id}/deactivate` | update, delete/reactivate |
| **Role** | `GET /api/v1/roles`, `GET /api/v1/permissions` (read-only) | create / update / delete role, assign permissions to role |
| **Access group** | `GET /` `GET /{id}` `GET /{id}/members` `POST /` `POST /{id}/scopes` `DELETE /{id}/scopes/{scopeId}` | update group, delete group, add/remove members |
| **Watchlist** | `GET /` `POST /` `DELETE /{id}` `GET /alerts` `POST /alerts/{id}/acknowledge` | update an entry; per-type UIs for number plate / person / vehicle (`src/Trinetra.Federation.Core/Model/Watchlist.cs`) |
| **API key** | `GET /api/v1/api-keys` `POST /` `DELETE /{id}` | UI page only — endpoints exist (`ApiKeyEndpoints.cs`, perms `apikey.read` / `apikey.manage`) |

**Fix:**
- Build admin list + form pages for organization, geography, role, access
  group, and watchlist (with a sub-tab per watchlist type).
- Build the **API-key page**: list (`GET`), create (`POST` — show the secret
  **once**, on creation only), revoke (`DELETE`). Endpoints are ready.
- For the genuinely missing endpoints (update / delete / reactivate on
  organization, geography, role, access group), file API work items — these are
  already partly tracked in the `api-review-findings` memo (F5).

---

## Cross-references

- API review already in progress — see the `api-review-findings` notes
  (F1 OpenAPI jargon, F2 forgot-password, F4 auth gaps, F5 org/geography CRUD).
- Schema review — `schema-review-decisions` notes cover
  `credential_access_log` and `connection_test`.
- Contracts: `src/Trinetra.Federation.Api/Contracts/CameraContracts.cs`,
  `.../Contracts/Contracts.cs`, `.../Contracts/Responses.cs`.
- Plan of record for the registry/GIS surface:
  `docs/MODEL-1-API-PLAN.md`.
