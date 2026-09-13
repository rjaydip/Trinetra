# Agent Guidelines for Trinetra Registry Frontend

This document provides architectural, operational, and domain guidance for AI agents working in `trinetra-registry-frontend`. All agents modifying, debugging, or extending this codebase must adhere to the patterns and invariants outlined below.

---

## 1. Authoritative Backend API Reference

> [!IMPORTANT]
> There is no committed copy of the backend's OpenAPI spec in this repo (a prior `v1.yaml` snapshot was removed — it drifts from the real contract with every backend change and nothing keeps the two in sync). For any endpoint route, payload contract, response shape, status code, query parameter, or required RBAC permission, read it live instead:
> - Run the backend (`dotnet run --project src/Trinetra.Federation.Api` from the repo root) and open `/scalar` (or fetch `/openapi/v1.json` directly).
> - Or read the endpoint's own source under `src/Trinetra.Federation.Api/Endpoints/*.cs` in the main repo — each route's `.WithSummary()`/`.WithDescription()` is the same text Scalar renders.

### Key Backend Rules & Contracts
- **Base Path & Origin**: All API routes are rooted at `/api/v1/*`. Default local backend is `http://localhost:5261` (configurable via `VITE_API_BASE_URL`, read through `src/config/env.ts` — see §4).
- **Standardized Error Handling**: Errors adhere to RFC 7807 Problem Details (`application/problem+json`) with properties `{ status, title, detail, errors }`.
- **RBAC & Scoping**:
  - Every route is authenticated, audited, and scoped by organization and geographic area.
  - A user lacking permissions receives `403 Forbidden`.
  - A resource outside a user's scoped hierarchy returns `404 Not Found` rather than `403` to prevent enumeration and ID probing.
- **Media Invariant**: The API **never** transmits video streams or raw video bytes. RTSP stream URLs, snapshot paths, and credentials are exchanged purely as metadata references.
- **No "sites"**: there is no `sites` table on the backend (removed v1.11). Cameras, VMS targets, and events attach directly to a `geographicAreaId` at any level of the operator-defined hierarchy. Don't reintroduce a site concept in the UI.

---

## 2. Core Philosophy & Non-Negotiable Invariants

### 1. Zero Synthetic / Mock Data in Production
The application operates exclusively against real records seeded or managed by the Trinetra Federation API.
- **Never mock cameras, markers, or credentials**: If an API endpoint is unavailable, failing, or unprovisioned, the frontend surfaces that state honestly using `PageState`, a retry affordance, inline alerts, or `StatusBadge`.
- **Do not invent data**: when backend gap-analysis or ageing metrics are unexposed, features render explicit "Unavailable" placeholders (e.g. see `src/features/reports/ReportsPage.tsx`).

### 2. Credential & Secret Isolation
- **No secrets in shared config**: never commit credentials, passwords, or secret tokens in a shared `.env`. `VITE_USERNAME`/`VITE_PASSWORD` (local-dev login prefill only) belong in a gitignored `.env.local`, never `.env` — see `src/config/env.ts`.
- **Session Ephemerality**: Authenticated user sessions are stored strictly in `sessionStorage` (`trinetra.auth.session`).
- **State Wiping**: Forms capturing passwords or tokens (such as `src/features/vms/CredentialPanel.tsx`) must clear plain-text inputs immediately upon submission.
- **Response Sanitization**: All connection test results and diagnostic payloads must be filtered through sanitizer helpers (e.g., `sanitizeTestResult`) to ensure no sensitive credentials or internal secrets leak into the DOM.
- **API Key Creation**: Raw API keys are returned exactly once upon creation (`POST /api/v1/api-keys`) and are never retrievable again.

### 3. Client Permissions are Presentation Hints Only
- `hasPermission(session, permission)` parses the `trinetra:perm` claim inside the unvalidated JWT client-side purely as a presentation hint (to conditionally render nav links, action buttons, or route guards).
- The true security boundary resides entirely on the backend. Never rely on client-side permission checks for security validation.

---

## 3. Technology Stack

| Layer | Technology | Key Dependencies | Notes |
|---|---|---|---|
| **Runtime / UI** | React 19 | `react`, `react-dom` | Strict React 19 patterns; no legacy lifecycle APIs |
| **Routing** | React Router v7 | `react-router-dom` | Data and nested route architecture |
| **Language** | TypeScript 5.9 | `typescript` | Strict mode enabled (`tsconfig.app.json`, `tsc -b`) |
| **Build & Dev Tool** | Vite 7 | `vite`, `@vitejs/plugin-react` | Fast HMR, proxying `/api` requests to backend |
| **Server State** | TanStack Query v5 | `@tanstack/react-query` | Retry disabled (`queries: { retry: false }`); query keys come from `src/api/queryKeys.ts` — see §5.2, never a hand-typed literal array |
| **Forms & Validation**| React Hook Form + Zod 4 | `react-hook-form`, `zod`, `@hookform/resolvers` | Used in `CameraForm`/`VmsForm`; most admin forms are plain uncontrolled `<form>` + `FormData` — see §5.3 for when to use which |
| **Geospatial / GIS** | MapLibre GL v5 | `maplibre-gl` | Interactive maps, clustering, coverage sectors |
| **Styling** | Vanilla CSS | `src/styles.css` | Design token CSS variables; no CSS framework |
| **Linting** | ESLint 9 (flat config) | `eslint.config.js` | `typescript-eslint`, `eslint-plugin-react-hooks`, `eslint-plugin-jsx-a11y`, `eslint-plugin-react-refresh`. `npm run lint` runs `tsc -b` then `eslint .` |
| **Testing** | Vitest 3 | `vitest`, `jsdom`, `@testing-library/*` | 40+ test files, 219+ tests, co-located as `Thing.test.tsx` beside `Thing.tsx` |

---

## 4. Directory & Architecture Map

```
trinetra-registry-frontend/
├── index.html                    # Single Page App root template
├── package.json                  # NPM dependencies and scripts
├── eslint.config.js              # Flat ESLint config
├── tsconfig.json                 # Project references
├── tsconfig.app.json             # App TypeScript compiler options (strict)
├── vite.config.ts                # Vite config (proxy to VITE_API_BASE_URL)
└── src/
    ├── main.tsx                  # Application bootstrap
    ├── App.tsx                   # Route definitions, SessionApplication, QueryClientProvider
    ├── styles.css                # Global design system tokens and component styles
    ├── config/
    │   └── env.ts                # The one place every import.meta.env read lives: apiBaseUrl(), devCredentials()
    ├── api/                      # Backend client & type contracts
    │   ├── client.ts             # Fetch wrapper, Bearer token injection, ApiProblem RFC 7807 parser, errorDetail()
    │   ├── endpoints.ts          # Strongly-typed API calls
    │   ├── queryKeys.ts          # Central React Query key factory — every query/invalidation key comes from here
    │   └── models.ts             # TypeScript interfaces for API requests/responses
    ├── auth/                     # Authentication & session management
    │   ├── AuthProvider.tsx      # React Context for auth state; schedules a refresh ~30s before token expiry
    │   ├── session.ts            # sessionStorage persistence (trinetra.auth.session)
    │   ├── permissions.ts        # Client JWT claim parser for presentation hints
    │   ├── RequireAuth.tsx       # Route guard (redirects unauthenticated or password-expired)
    │   ├── RequirePermission.tsx # Route guard checking specific permission claim
    │   ├── LoginPage.tsx         # User authentication form
    │   └── PasswordPage.tsx      # Mandatory/optional password update form
    ├── components/               # Common shared UI components
    │   ├── AppShell.tsx          # Top navigation bar, brand banner, logout, layout outlet
    │   └── ui.tsx                # Button, StatusBadge (tones), PageState
    ├── test/
    │   └── fixtures.ts           # Shared test fixtures (sessionFixture, cameraFixture, vmsFixture, organizationFixture, geographicAreaFixture) — extend this rather than redefining a fixture object per test file
    └── features/                 # Domain-driven feature modules
        ├── cameras/              # Camera Registry domain
        │   ├── RegistryPage.tsx        # Camera catalog, search, filters, cursor pagination
        │   ├── CameraDetailPage.tsx    # Camera details, live health, maintenance records
        │   ├── CameraDetailSections.tsx
        │   ├── CameraForm.tsx          # New / Edit camera form with Zod schema validation
        │   ├── CameraFilters.tsx / CameraTable.tsx / CameraCards.tsx
        │   ├── LocationPicker.tsx      # Interactive map location & azimuth selector
        │   ├── BulkImportPage.tsx      # CSV / JSON batch import (insert & upsert)
        │   ├── ReconciliationPage.tsx  # Unreconciled-camera backlog: link-to-existing or register-from-federated
        │   ├── CameraHealthHistory.tsx # Health check timeline
        │   └── cameraVocabulary.ts     # Types, statuses, protocols, coordinate helpers
        ├── map/                  # GIS & Map domain
        │   ├── MapPage.tsx / MapFilters.tsx  # Map overview, clusters, bounding-box viewport queries
        │   ├── CameraMap.tsx           # MapLibre GL wrapper
        │   ├── CameraDetailDrawer.tsx  # Map marker side-drawer
        │   ├── DashboardData.tsx       # Dashboard statistics & search shortcuts
        │   └── geo.ts                  # Bounding-box (max 2° bounds) & GeoJSON utilities
        ├── vms/                  # VMS Federation & Onboarding domain
        │   ├── VmsPage.tsx             # Connector target listing, registration, detail
        │   ├── VmsForm.tsx             # VMS registration input fields
        │   ├── VmsManagement.tsx       # State control, delete, edit, health, capabilities panels for an existing target
        │   ├── CredentialPanel.tsx     # Isolated credential storage & connection testing
        │   ├── DiscoveryPage.tsx       # VMS camera discovery & batch registry enrichment
        │   └── discovery.ts            # Discovery validation (max 500 limit) & mapping
        ├── detections/
        │   └── DetectionsPage.tsx      # Read-only detection search (plate/target/time window); ingest is the AI worker's job, not a UI action
        ├── events/
        │   └── EventsPage.tsx          # Hot-window event query (required time range, cursor pagination)
        ├── admin/                # Admin Control Plane domain
        │   ├── AdminPage.tsx           # Admin layout & permission navigation
        │   ├── HierarchyPage.tsx       # Organization + geographic-area hierarchy, edit/deactivate/cross-org move
        │   ├── RolesPage.tsx           # Role catalog & permission catalogue reference
        │   └── AccessGroupsPage.tsx    # Group creation, scoping, and membership
        └── reports/              # Registry & Fleet Reporting
            ├── ReportsPage.tsx         # Fleet summary totals & coverage summary
            └── CoverageSummary.tsx     # Geographic area coverage metric breakdown
```

---

## 5. Domain Guidelines & Implementation Patterns

### 1. API Client & Error Handling (`src/api/`)
- All requests use `request<T>(path, init)` in `src/api/client.ts`.
- Bearer tokens from `readSession()` are automatically appended in headers.
- **401 Eviction**: If any response returns 401 and the bearer token matches the current active session, `clearSession()` is called to log out the expired user.
- **`ApiProblem` Exception**: Non-2xx responses are parsed into an `ApiProblem` instance. Use `isApiProblem(error)` to check if an error is an API response problem.
- **`errorDetail(error, fallback)`** (also in `client.ts`) is the one place a caught error becomes safe, user-facing text — import it rather than redefining a local `errorDetail`/`function errorDetail` in a page. Every page in this codebase does this; don't add a tenth copy.

### 2. State & Cache Lifecycle
- In `src/App.tsx`, `SessionApplication` mounts `QueryClient` keyed by `session?.token ?? 'signed-out'`. When a user logs in or out, the entire query cache is purged cleanly to prevent data leakage between sessions.
- **Every `queryKey` and every `invalidateQueries({ queryKey })` comes from `src/api/queryKeys.ts`.** Never hand-type a literal array (`['vms']`) — a typo there silently creates a second, disconnected cache entry rather than erroring. Add a new key shape to the factory instead of inlining one; when a mutation needs to invalidate every parameterized variant of a query, the factory exposes an `all`/`*All` prefix key for exactly that.

### 3. Forms & Data Validation
Two conventions coexist for a reason — pick based on the form's shape, not habit:
- **`react-hook-form` + `zodResolver(schema)`** (`CameraForm`, `VmsForm`): the two forms with real cross-field validation, numeric range checks, and enough fields to want schema-level validation and typed field errors.
- **Plain uncontrolled `<form>` + `FormData`** (`HierarchyPage`, `RolesPage`, `AccessGroupsPage`, `ReconciliationPage`'s create form): most admin CRUD forms — a handful of simple fields, validated server-side, where a full form library is overhead. This is a known area slated for a closer look (see the open item at the end of this file), not an accident to "fix" unilaterally mid-task.
- Zod schemas (where used) enforce backend constraints: UUIDs (`z.guid()`), coordinate ranges (`[-90, 90]` / `[-180, 180]`), max lengths (camera code 100 chars, name 255 chars), positive/expected numeric ranges.
- Follow cascading selector patterns: e.g., changing Organization must reset and repopulate Organization Unit options.

### 4. GIS & Map Constraints (`src/features/map/`)
- **Zoom Limitation**: In `src/features/map/geo.ts`, `MAX_GIS_BOUNDS_DEGREES = 2`. Map viewport requests wider than 2° in latitude or longitude are intentionally suppressed to avoid straining backend GIS calculations and client memory.
- **Initial Centering**: The map bootstrap targets an actual seeded camera coordinate rather than an arbitrary mid-point.
- **Coverage Disclaimer**: Coverage sectors are estimated planning aids; the UI must always display the standard notice: *"Coverage sectors are estimated planning aids; terrain and obstructions are not modelled."*

### 5. VMS Federation, Discovery & Reconciliation (`src/features/vms/`, `src/features/cameras/ReconciliationPage.tsx`)
- **Step 1 (Register Target)**: `POST /api/v1/vms` creates the connector target row.
- **Step 2 (Provision Credential)**: `PUT /api/v1/vms/{id}/credential` sets the secret username/password/token. The reference is derived from the target itself, never supplied by the client.
- **Step 3 (Connection Test)**: `POST /api/v1/vms/{id}/test` is asynchronous, returning 202 with a `statusUrl`. The client polls until terminal status (`succeeded`, `failed`, or `error`).
- **Step 4 (Discovery & Import)**: `GET /api/v1/vms/{id}/cameras` retrieves native cameras.
  - Selection limit: Maximum 500 cameras selected in a single batch (`validateDiscoveredCameraSelectionCount`).
  - Batch import: Submits enriched cameras to `POST /api/v1/cameras/bulk-import` with `mode: 'upsert'`.
- **Reconciliation** (`ReconciliationPage.tsx`) is the other path a discovered camera reaches the registry through: `GET /cameras/unreconciled` lists the backlog; an operator either links a row to an existing registry camera (`POST /cameras/{id}/reconcile`) or registers a new one from the row's own facts in one call (`POST /cameras/from-federated`).
- **Ongoing management** of an already-registered target (edit config, state transitions, delete, health, capabilities) lives in `VmsManagement.tsx`, rendered from `VmsPage`'s detail view — not duplicated in `VmsForm.tsx`, which is registration-only.

### 6. Admin Hierarchies & Conflict Handling (`src/features/admin/`)
- When deactivating an organization unit or geographic area, the backend may return `409 Conflict` if dependent children exist. `HierarchyPage.tsx` handles this via `DeactivationControl`, presenting radio options to either:
  1. **Cascade**: Deactivate all dependent children.
  2. **Reparent**: Choose a new parent under which to move the children.
- **Cross-organization move** (`MoveUnitControl` in the same file): `POST /organization-units/{id}/move` re-parents a unit's whole subtree into a different organization. A 409 scope-impact conflict (an access group's scope reaches into the moved subtree) requires an explicit "move anyway" confirmation before resending — never auto-confirmed.

---

## 6. Accessibility (a11y) & UX Conventions

- **Semantic HTML**: All forms use `<fieldset>`, `<legend>`, `<label>`, and proper heading hierarchies (`<h1>` per page, followed by `<h2>`, `<h3>`).
- **Accessible Validation**:
  - Inputs specify `aria-required="true"`.
  - Error messages have `role="alert"` and are connected to inputs via `aria-describedby="{field}-error"`.
  - Invalid inputs must have `aria-invalid="true"`.
- **Live Statuses**:
  - Background loading, selector state changes, and poll results must use `aria-live="polite"` or `role="status"`.
- **Keyboard Navigation**: All interactive elements (drawers, location pickers, popups) must be operable via keyboard. A clickable list row is a `<button>` inside the row, not a `<li onClick>` with an ARIA role bolted on — `eslint-plugin-jsx-a11y` (see §7) catches the latter.
- **ESLint enforces this now, not just review**: `npm run lint` runs `eslint-plugin-jsx-a11y` — a new interactive element should pass it, not rely on a human catching the gap later.

---

## 7. Verification & Workflow Commands

Execute these commands from the `frontend/` directory:

```bash
# Install dependencies
npm install

# Start local dev server (default port 5173, proxies /api to http://localhost:5261)
npm run dev -- --host 0.0.0.0

# Run all unit and integration tests
npm test -- --run

# Run a specific test file
npx vitest run src/features/cameras/CameraForm.test.tsx

# Typecheck + lint (ESLint: typescript-eslint, react-hooks, jsx-a11y, react-refresh)
npm run lint

# Typecheck only
npm run typecheck

# Build production bundle
npm run build
```

### Full Verification Pipeline
Before submitting any changes, always run the full verification command:
```bash
npm test -- --run && npm run lint && npm run build
```
All tests must pass, lint must report zero errors (warnings are advisory — see the open items below), and the build must succeed.

### Known open items (not yet acted on, tracked here rather than silently left undiscovered)
- `RegistryPage.tsx` and `CameraMap.tsx` each have one `react-hooks/exhaustive-deps` warning — deliberately not auto-fixed, since adding the missing dependency could change behavior (re-running an effect more often); needs a deliberate look, not a mechanical fix.
- Several files (`AuthProvider.tsx`, `AdminPage.tsx`, `CameraForm.tsx`, `VmsForm.tsx`) export a non-component alongside a component and get a `react-refresh/only-export-components` warning — cosmetic (only affects HMR granularity in dev), not a correctness issue.
- `HierarchyPage.tsx` (~1,100 lines, Organizations + Geography in one file) is a known candidate for splitting into sub-components — deferred as a design/layout decision, not a mechanical refactor.
- The `react-hook-form`+`zod` vs. plain-`FormData` form split (§5.3) is deliberate for now but worth a single documented convention eventually rather than two live patterns.
