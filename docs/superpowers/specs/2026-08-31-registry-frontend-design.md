# Trinetra Registry Frontend Design

## Purpose

Build a production-ready React and TypeScript frontend for Model 1's CCTV registry and GIS visibility layer. The UI uses the existing Trinetra API and its seeded records; it does not invent registry fields, reports, permissions, or coverage calculations.

## Scope

The new `frontend/` single-page application provides:

- Interactive GIS dashboard with live GeoJSON camera markers, clustering, server-backed filters, client-side viewport type/connectivity filtering, optional estimated coverage sectors, summary counters, legend, and an accessible camera detail drawer.
- Registry with the API's opaque cursor paging, supported search and filter fields, responsive table/card layouts, and navigation to camera detail.
- Camera detail, health snapshot/history, and maintenance records where the signed-in user is authorized.
- Manual camera registration based exactly on `CameraWriteRequest`.
- Bulk JSON import based exactly on `BulkImportRequest`; its client preview is parsing/shape validation, while the API result is the source of row outcome.
- Coverage and fleet reporting only from `/api/v1/gis/coverage` and `/api/v1/overview`. The gap-analysis endpoint's documented 501 response is displayed as unavailable; no ageing report, audit trail, server export, or file upload endpoint is represented because the API does not expose those capabilities.
- Username/password login and required password rotation using the existing `/api/v1/auth` endpoints.

## Out of Scope

- Centralized video, recording, or streams.
- Client-side coverage-gap calculations, fake metrics, mock camera records, or fake export/audit workflows.
- APIs or backend schema changes.
- User/role/group administration screens. The backend exposes administrative APIs, but this Model 1 frontend focuses on the explicitly required registry and GIS workflows.

## Architecture

`frontend/` is a Vite-built React SPA. React Router owns application routes; TanStack Query owns remote data and invalidation; React Hook Form and Zod own form state and field validation. A typed fetch client adds a bearer token, serializes query parameters, translates RFC 7807 problem responses into safe user-facing messages, and treats 401/403/network errors as first-class states.

The signed-in token is held in `sessionStorage` for the duration of the browser session. The API remains the authorization authority. The frontend may decode permission claims only to avoid presenting actions which are known to be forbidden; every request remains protected server-side and a 403 refreshes the action state/message.

The dashboard first requests a small live camera page to establish an initial, valid viewport, then requests GeoJSON only for the active map bounds. Because `/api/v1/gis/cameras` limits bounding boxes to 2 degrees per axis, map requests are debounced, bounded, and only issued after a sufficiently zoomed-in viewport is available. Camera markers are clustered in the browser; coverage sectors are requested only when toggled and available.

## API Contract

All authenticated API requests target `VITE_API_BASE_URL`, defaulting to `http://localhost:5261`.

| UI need | Existing API |
|---|---|
| Login / forced password change | `POST /api/v1/auth/login`, `POST /api/v1/auth/password` |
| Camera registry | `GET/POST /api/v1/cameras`, `GET /api/v1/cameras/{id}` |
| Health / maintenance | `GET /api/v1/cameras/{id}/health`, `/health/history`, `/maintenance` |
| Bulk onboarding | `POST /api/v1/cameras/bulk-import` with JSON (1-500 items) |
| GIS data and estimated sectors | `GET /api/v1/gis/cameras`, `GET /api/v1/cameras/{id}/coverage` |
| Coverage report | `GET /api/v1/gis/coverage` |
| Dashboard fleet summary | `GET /api/v1/overview` |
| Reference data | `/api/v1/organizations`, `/api/v1/organizations/{id}/units`, `/api/v1/sites`, `/api/v1/geographic-areas` |

The frontend must not call `/api/v1/gis/gaps` as a report data source: it is intentionally 501 until a future PostGIS-backed slice. It must not offer a server export because no export API exists.

## Routes and Components

| Route | Main components | Data |
|---|---|---|
| `/login` | `LoginForm` | auth login |
| `/password` | `PasswordChangeForm` | auth password change |
| `/dashboard` | `CameraMap`, `MapFilterPanel`, `MapLegend`, `CameraDetailDrawer` | GeoJSON, camera detail, overview |
| `/cameras` | `CameraFilters`, `CameraTable`, `CameraListCard` | cursor-paged cameras |
| `/cameras/new` | `CameraForm` | reference data, camera create |
| `/cameras/import` | `BulkImportDropzone`, `ImportValidationTable` | camera bulk import |
| `/cameras/:id` | `CameraDetail`, health, maintenance | camera detail, health, maintenance |
| `/reports` | `CoverageSummary`, `CoverageUnavailableState` | coverage aggregate, overview |

`AppShell`, `Sidebar`, `Topbar`, page headers, loading skeletons, empty/error states, confirmation dialog, and toast region are shared layouts/state components.

## UX and Accessibility

The design is a compact, high-contrast operations console: blue for primary navigation and actions, orange reserved for key calls to action, and semantic status tokens paired with text and icons. Fira Sans is the body face and Fira Code is used sparingly for camera codes and coordinate values. Components use visible keyboard focus, labelled controls, 44px touch targets where possible, semantic tables/cards, and form labels plus connected error text.

Drawers and dialogs trap focus and restore it on close. Loading and mutation states are announced via a polite live region but critical success/failure is also shown inline. The map controls have accessible labels and alternatives in surrounding filters/details. Motion is limited to short, interruptible 150-250ms transitions and disabled for reduced-motion preferences.

Desktop uses persistent navigation and a side detail panel; tablet converts filters/details to drawers; mobile prioritizes the map and turns registry rows into cards and details into a bottom sheet.

## Operational Requirements

The remote API must be reachable from the browser and configured with the deployed frontend origin in `Auth:AllowedOrigins`; the API intentionally disallows wildcard credentialed origins. During local development the Vite server may proxy `/api` to `VITE_API_BASE_URL` to avoid local CORS friction, but real deployment still requires the explicit allowlist.

Live acceptance testing must be performed with `http://localhost:5261` reachable and valid existing user credentials. The implementation must provide a clear, non-secret-revealing unavailable state if it is not.

## Verification

- Unit tests cover query construction, problem normalization, permission parsing, form mapping, and map bound handling.
- Component tests cover authentication, empty/error/loading states, accessible navigation, filtering, drawer focus behavior, onboarding validation, import results, and the unavailable coverage-gap presentation.
- A production build verifies TypeScript and bundler integrity.
- Manual browser verification uses a real login and seeded API data once the configured backend is reachable.
