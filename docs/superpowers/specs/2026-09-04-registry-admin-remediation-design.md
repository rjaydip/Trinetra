# Trinetra Registry and Admin Remediation Design

## Purpose

Extend the existing React registry application to address the Camera Registry and Admin UI review without inventing backend capabilities. The work is incremental: each slice adds a complete, testable user flow while retaining the current application routes, query client, authentication model, and uncommitted local configuration changes.

## Scope and Delivery Order

### Slice 1: Registry and map usability

The existing camera registry and manual-registration page gain the review fixes that are supported by the current API:

- The password-change form displays RFC 7807 details, refuses a new password equal to the current value, and refuses a confirmation mismatch. The existing login redirect based on `mustChangePassword` remains in place.
- The cursor-paged list keeps the API's opaque `cursor` query parameter, resets it when filters change, labels the control `Next`, and disables it when `nextCursor` is null or a replacement request is pending.
- The registration form visibly marks API-required fields: camera code, name, organization unit, site, camera type, latitude, and longitude. Other API-optional fields move into an `Additional details` disclosure.
- The manual path adds local requirements for manufacturer, IP address, port, and protocol. This is intentionally a UI rule for directly connected cameras, not a change to `CameraWriteRequest` and not a rule applied to NVR-imported rows.
- Camera type, protocol, and all three status axes remain selects containing the backend vocabulary values. Organization unit and site use live reference endpoints. The VMS selector uses the live VMS list endpoint. Manufacturer remains a labelled free-text input because the backend supplies no manufacturer catalogue; no fake reference list or misleading typeahead is introduced.
- A MapLibre location picker uses the existing GIS camera GeoJSON as its background source. Click and pin drag update latitude and longitude; manual coordinates move the pin. Values emitted to form state are rounded to seven fractional places. A visible, keyboard-accessible azimuth control accompanies the picker; dragging its directional handle updates azimuth, with a numeric input as its accessible alternative.
- Bulk import offers a locally generated sample JSON download containing one valid-shaped item and empty optional values. JSON remains the only upload format. The existing API-provided per-row import result stays the authoritative report.
- Coverage summary keeps no free-text input because `GET /api/v1/gis/coverage` exposes no general text parameter. Any supported organization or geographic filtering uses the same live selectors as the registry rather than an unbound text field.

### Slice 2: NVR onboarding and credentials

The app gains VMS/NVR routes guarded by the backend permissions. A user can create or select a VMS, inspect its credential status, save a username plus password or token, and run the existing connection test. Passwords and tokens are cleared from form state after submission and are never placed in a query cache, URL, list, or success message.

The discovery route requests `GET /api/v1/vms/{id}/cameras`, lets the user select returned rows, and collects their required registry placement and optional coverage metadata. It maps selected enriched rows into `CameraWriteRequest` objects and submits them with `POST /api/v1/cameras/bulk-import` in `upsert` mode. The import result is presented per row. This deliberately follows the approved bulk-import workflow even though the backend also exposes reconciliation routes; the user selected bulk import for this interface.

The UI does not claim it can store a direct camera secret. `CredentialReference` remains a non-secret reference field because the API has no camera-scoped credential write endpoint. The form explains the limitation and links to no unsupported action.

### Slice 3: API-backed administration

The Admin navigation is permission-aware and contains only operations the present API supports:

- Organizations and organization units: list, create organization, list/create units, and deactivate units where allowed.
- Geography and sites: list, traverse hierarchy, create, and deactivate where the existing endpoints support it.
- Roles and permissions: read-only catalogue.
- Access groups: list/detail, create, list members, add scopes, and remove scopes. No group update, deletion, or member-management action is offered.
- Watchlists: number-plate entries and alerts, create/deactivate entries, and acknowledge alerts. Person and vehicle tabs identify their API as unavailable instead of presenting non-functional forms.
- API keys: list, create, copy the raw secret from a one-time disclosure, and revoke. The secret is removed from component state when the disclosure closes or the route unmounts.

Update, delete, reactivate, role-assignment, and access-group membership APIs that do not exist are recorded as backend work items in the repository documentation. They are not simulated through client-side state.

## Architecture

The application remains a Vite React SPA using React Router, TanStack Query, React Hook Form, Zod, and MapLibre. Existing API-client error normalization is extended with typed models and endpoint functions for VMS, credentials, connection tests, and the admin APIs. Mutations invalidate only their related query keys so registry, map, and admin views refresh after a successful change without clearing unrelated data.

Feature code is organized by user flow: `features/cameras` owns registration, import, and location-picker form integration; `features/vms` owns VMS, credential, discovery, and import mapping; and `features/admin` owns independent API-backed administration pages. Shared selector, disclosure, error, one-time-secret, and map-picker primitives remain focused components under `components`.

## Data and Validation Rules

All writes use backend names and types. The API-required camera fields are always validated locally. Latitude and longitude use the database/API ranges and round to seven decimal places before write. The fixed strings are exactly:

- Camera types: `FIXED`, `PTZ`, `DOME`, `BULLET`, `ANPR`, `THERMAL`, `MULTISENSOR`, `OTHER`.
- Protocols: `RTSP`, `RTSPS`, `ONVIF`, `HTTP`, `HTTPS`, `RTMP`, `SRT`, `OTHER`.
- Operational status: `ONLINE`, `OFFLINE`, `DEGRADED`, `UNKNOWN`.
- Connectivity status: `CONNECTED`, `DISCONNECTED`, `UNKNOWN`.
- Maintenance status: `NORMAL`, `REQUIRED`, `UNDER_MAINTENANCE`; `RETIRED` is display-only for the create form.

Manual registration adds the product-level network-field requirement described above. NVR import rows require only the registry contract's non-nullable fields after enrichment. Each API problem is rendered near the action that caused it; a successful mutation receives clear status text and query invalidation.

## UX and Accessibility

The current compact operations-console language remains intact. Forms pair every control with a visible label, required mark and explanatory text; validation messages use `aria-describedby` and are announced without relying on colour. Disclosures use native buttons and `aria-expanded`. Picker interactions have keyboard/numeric alternatives, including an azimuth number field. Map markers and existing-camera context remain supplementary; entering valid coordinates never depends on interacting with the map.

Credential and API-key flows use explicit safety copy. A stored-credential badge comes only from the API's status response. A one-time API-key secret is visually and semantically distinct, provides an accessible copy action, warns that it cannot be recovered, and is cleared when dismissed. Loading, empty, permission-denied, and mutation-error states are actionable and preserve the user's entries where safe.

## API Boundaries and Work Items

The frontend will not create fields, options, endpoints, CSV parsing, or access-control rules that the backend has not exposed. In particular:

- There is no manufacturer reference endpoint, so manufacturer is not represented as API-backed autocomplete.
- There is no direct-camera credential endpoint, so secrets cannot be saved for a manually registered camera.
- Role CRUD/permission assignment, organization/geography update/delete/reactivate, access-group update/delete/member mutation, watchlist editing, and person/vehicle watchlist contracts are API work items.
- CSV import is an API work item. The frontend supports the existing JSON-only endpoint and sample JSON template.

The backend-work-items document records the exact missing API operations and their affected UI pages. It does not state or imply a delivery date.

## Testing and Verification

Each slice follows test-first development. Unit tests cover contract mapping, seven-place coordinate rounding, azimuth conversion, conditional manual/NVR validation, cursor transitions, JSON template generation, and API request shapes. Component tests cover password rejection and problem rendering, required markers/disclosure behaviour, map-picker synchronization, next-button state, import row errors, secret handling, permission gates, and unavailable-admin states. Existing application tests must remain green.

The final verification runs `npm test -- --run`, `npm run typecheck`, and `npm run build` in `frontend/`. When the configured backend is available, manual verification uses a real account and seeded data for a map pin drag, manual camera submission, NVR credential/status/test, NVR bulk import, one-time API-key creation, and revocation. No mocked core data substitutes for failed live connectivity.
