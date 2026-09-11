# Agent Guidelines for Trinetra Registry Frontend

This document provides architectural, operational, and domain guidance for AI agents working in `trinetra-registry-frontend`. All agents modifying, debugging, or extending this codebase must adhere to the patterns and invariants outlined below.

---

## 1. Authoritative Backend API Reference (`v1.yaml`)

> [!IMPORTANT]
> The authoritative OpenAPI 3.1.1 specification for the backend is located in this repository at:
> **[`./v1.yaml`](file:///Users/darkoreo/work/Trinetra/frontend/v1.yaml)** (`/Users/darkoreo/work/Trinetra/frontend/v1.yaml`).
>
> **For ANY backend query, endpoint route, payload contract, response shape, status code, query parameter, or required RBAC permission, ALWAYS refer directly to [`v1.yaml`](file:///Users/darkoreo/work/Trinetra/frontend/v1.yaml).**

### Key Backend Rules & Contracts
- **Base Path & Origin**: All API routes are rooted at `/api/v1/*`. Default local backend is `http://localhost:5261` (configurable via `VITE_API_BASE_URL`).
- **Standardized Error Handling**: Errors adhere to RFC 7807 Problem Details (`application/problem+json`) with properties `{ status, title, detail, errors }`.
- **RBAC & Scoping**:
  - Every route is authenticated, audited, and scoped by organization and geographic area.
  - A user lacking permissions receives `403 Forbidden`.
  - A resource outside a user's scoped hierarchy returns `404 Not Found` rather than `403` to prevent enumeration and ID probing.
- **Media Invariant**: The API **never** transmits video streams or raw video bytes. RTSP stream URLs, snapshot paths, and credentials are exchanged purely as metadata references.

---

## 2. Core Philosophy & Non-Negotiable Invariants

### 1. Zero Synthetic / Mock Data in Production
The application operates exclusively against real records seeded or managed by the Trinetra Federation API.
- **Never mock cameras, markers, or credentials**: If an API endpoint is unavailable, failing, or unprovisioned, the frontend surfaces that state honestly using `PageState`, `ReferenceError`, inline alerts, or `StatusBadge`.
- **Do not invent data**: For example, when backend gap-analysis or ageing metrics are unexposed, features render explicit "Unavailable" placeholders (e.g. see [`src/features/reports/ReportsPage.tsx`](file:///Users/darkoreo/work/Trinetra/frontend/src/features/reports/ReportsPage.tsx)).

### 2. Credential & Secret Isolation
- **No Secrets in Config**: Never store credentials, passwords, or secret tokens in Vite `.env` files or `localStorage`.
- **Session Ephemerality**: Authenticated user sessions are stored strictly in `sessionStorage` (`trinetra.auth.session`).
- **State Wiping**: Forms capturing passwords or tokens (such as [`src/features/vms/CredentialPanel.tsx`](file:///Users/darkoreo/work/Trinetra/frontend/src/features/vms/CredentialPanel.tsx)) must clear plain-text inputs immediately upon submission.
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
| **Server State** | TanStack Query v5 | `@tanstack/react-query` | Retry disabled (`queries: { retry: false }`), cache per session key |
| **Forms & Validation**| React Hook Form + Zod 4 | `react-hook-form`, `zod`, `@hookform/resolvers` | Strict schemas, field-level accessibility |
| **Geospatial / GIS** | MapLibre GL v5 | `maplibre-gl` | Interactive maps, clustering, coverage sectors |
| **Styling** | Vanilla CSS | `src/styles.css` | Design token CSS variables; NO TailwindCSS |
| **Testing** | Vitest 3 | `vitest`, `jsdom`, `@testing-library/*` | 30+ test suites, 170+ unit & integration tests |

---

## 4. Directory & Architecture Map

```
trinetra-registry-frontend/
├── v1.yaml                       # AUTHORITATIVE OpenAPI 3.1.1 backend spec
├── index.html                    # Single Page App root template
├── package.json                  # NPM dependencies and scripts
├── tsconfig.json                 # Project references
├── tsconfig.app.json             # App TypeScript compiler options (strict)
├── vite.config.ts                # Vite config (proxy to VITE_API_BASE_URL)
└── src/
    ├── main.tsx                  # Application bootstrap
    ├── App.tsx                   # Route definitions, SessionApplication, QueryClientProvider
    ├── styles.css                # Global design system tokens and component styles
    ├── api/                      # Backend client & type contracts
    │   ├── client.ts             # Fetch wrapper, Bearer token injection, ApiProblem RFC 7807 parser
    │   ├── endpoints.ts          # Strongly-typed API calls matching v1.yaml
    │   └── models.ts             # TypeScript interfaces for API requests/responses
    ├── auth/                     # Authentication & session management
    │   ├── AuthProvider.tsx      # React Context for auth state
    │   ├── session.ts            # sessionStorage persistence (trinetra.auth.session)
    │   ├── permissions.ts        # Client JWT claim parser for presentation hints
    │   ├── RequireAuth.tsx       # Route guard (redirects unauthenticated or password-expired)
    │   ├── RequirePermission.tsx # Route guard checking specific permission claim
    │   ├── LoginPage.tsx         # User authentication form
    │   └── PasswordPage.tsx      # Mandatory/optional password update form
    ├── components/               # Common shared UI components
    │   ├── AppShell.tsx          # Top navigation bar, brand banner, layout outlet
    │   └── ui.tsx                # Button, StatusBadge (tones), PageState
    ├── features/                 # Domain-driven feature modules
    │   ├── cameras/              # Camera Registry domain
    │   │   ├── RegistryPage.tsx        # Camera catalog, search, filters, cursor pagination
    │   │   ├── CameraDetailPage.tsx    # Camera details, live health, maintenance records
    │   │   ├── CameraForm.tsx          # New / Edit camera form with Zod schema validation
    │   │   ├── LocationPicker.tsx      # Interactive map location & azimuth selector
    │   │   ├── BulkImportPage.tsx      # CSV / JSON batch import (insert & upsert)
    │   │   ├── CameraHealthHistory.tsx # Health check timeline
    │   │   └── cameraVocabulary.ts     # Types, statuses, protocols, coordinate helpers
    │   ├── map/                  # GIS & Map domain
    │   │   ├── MapPage.tsx             # Map overview, clusters, bounding-box viewport queries
    │   │   ├── CameraMap.tsx           # MapLibre GL wrapper
    │   │   ├── CameraDetailDrawer.tsx  # Map marker side-drawer
    │   │   ├── DashboardData.tsx       # Dashboard statistics & search shortcuts
    │   │   └── geo.ts                  # Bounding-box (max 2° bounds) & GeoJSON utilities
    │   ├── vms/                  # VMS Federation & Onboarding domain
    │   │   ├── VmsPage.tsx             # Connector target listing & registration form
    │   │   ├── VmsForm.tsx             # VMS configuration input fields
    │   │   ├── CredentialPanel.tsx     # Isolated credential storage & connection testing
    │   │   ├── DiscoveryPage.tsx       # VMS camera discovery & batch registry enrichment
    │   │   └── discovery.ts            # Discovery validation (max 500 limit) & mapping
    │   ├── admin/                # Admin Control Plane domain
    │   │   ├── AdminPage.tsx           # Admin layout & permission navigation
    │   │   ├── HierarchyPage.tsx       # Org / Unit / GeoArea / Site hierarchy & deactivation
    │   │   ├── RolesPage.tsx           # Role catalog & permission catalogue reference
    │   │   └── AccessGroupsPage.tsx    # Group creation, scoping, and membership
    │   └── reports/              # Registry & Fleet Reporting
    │       ├── ReportsPage.tsx         # Fleet summary totals & coverage summary
    │       └── CoverageSummary.tsx     # Geographic area coverage metric breakdown
    └── test/                     # Test utilities
        └── fixtures.ts           # Mock data fixtures for unit tests
```

---

## 5. Domain Guidelines & Implementation Patterns

### 1. API Client & Error Handling (`src/api/`)
- All requests use `request<T>(path, init)` in [`src/api/client.ts`](file:///Users/darkoreo/work/Trinetra/frontend/src/api/client.ts).
- Bearer tokens from `readSession()` are automatically appended in headers.
- **401 Eviction**: If any response returns 401 and the bearer token matches the current active session, `clearSession()` is called to log out the expired user.
- **`ApiProblem` Exception**: Non-2xx responses are parsed into an `ApiProblem` instance. Use `isApiProblem(error)` to check if an error is an API response problem and extract `error.detail` or `error.errors`.

### 2. State & Cache Lifecycle
- In [`src/App.tsx`](file:///Users/darkoreo/work/Trinetra/frontend/src/App.tsx), `SessionApplication` mounts `QueryClient` keyed by `session?.token ?? 'signed-out'`. When a user logs in or out, the entire query cache is purged cleanly to prevent data leakage between sessions.
- Invalidate queries after mutations using `queryClient.invalidateQueries({ queryKey: [...] })`.

### 3. Forms & Data Validation
- Build forms with `useForm<T>()` from `react-hook-form` backed by `zodResolver(schema)`.
- Use Zod schemas to enforce backend constraints:
  - UUIDs: `z.guid()`
  - Coordinate ranges: Latitude in `[-90, 90]`, Longitude in `[-180, 180]`
  - Maximum lengths: Camera code (100 chars), Name (255 chars)
  - Numeric fields: Always validate positive/expected ranges
- Follow cascading selector patterns: e.g., changing Organization must reset and repopulate Organization Unit options.

### 4. GIS & Map Constraints (`src/features/map/`)
- **Zoom Limitation**: In [`src/features/map/geo.ts`](file:///Users/darkoreo/work/Trinetra/frontend/src/features/map/geo.ts), `MAX_GIS_BOUNDS_DEGREES = 2`. Map viewport requests wider than 2° in latitude or longitude are intentionally suppressed to avoid straining backend GIS calculations and client memory.
- **Initial Centering**: The map bootstrap targets an actual seeded camera coordinate rather than an arbitrary mid-point.
- **Coverage Disclaimer**: Coverage sectors are estimated planning aids; the UI must always display the standard notice: *"Coverage sectors are estimated planning aids; terrain and obstructions are not modelled."*

### 5. VMS Federation & Discovery (`src/features/vms/`)
- **Step 1 (Register Target)**: `POST /api/v1/vms` creates the connector target row.
- **Step 2 (Provision Credential)**: `PUT /api/v1/vms/{id}/credential` sets the secret username/password/token. The reference is derived from the target itself, never supplied by the client.
- **Step 3 (Connection Test)**: `POST /api/v1/vms/{id}/test` is asynchronous, returning 202 with a `statusUrl`. The client polls until terminal status (`succeeded`, `failed`, or `error`).
- **Step 4 (Discovery & Import)**: `GET /api/v1/vms/{id}/cameras` retrieves native cameras.
  - Selection limit: Maximum 500 cameras selected in a single batch (`validateDiscoveredCameraSelectionCount`).
  - Batch import: Submits enriched cameras to `POST /api/v1/cameras/bulk-import` with `mode: 'upsert'`.

### 6. Admin Hierarchies & Conflict Handling (`src/features/admin/`)
- When deactivating an organization unit or geographic area, the backend may return `409 Conflict` if dependent children exist.
- [`src/features/admin/HierarchyPage.tsx`](file:///Users/darkoreo/work/Trinetra/frontend/src/features/admin/HierarchyPage.tsx) handles this via `DeactivationControl`, presenting radio options to either:
  1. **Cascade**: Deactivate all dependent children.
  2. **Reparent**: Choose a new parent under which to move the children.

---

## 6. Accessibility (a11y) & UX Conventions

- **Semantic HTML**: All forms use `<fieldset>`, `<legend>`, `<label>`, and proper heading hierarchies (`<h1>` per page, followed by `<h2>`, `<h3>`).
- **Accessible Validation**:
  - Inputs specify `aria-required="true"`.
  - Error messages have `role="alert"` and are connected to inputs via `aria-describedby="{field}-error"`.
  - Invalid inputs must have `aria-invalid="true"`.
- **Live Statuses**:
  - Background loading, selector state changes, and poll results must use `aria-live="polite"` or `role="status"`.
- **Keyboard Navigation**: All interactive elements (drawers, location pickers, popups) must be operable via keyboard.

---

## 7. Verification & Workflow Commands

Execute these commands from `/Users/darkoreo/work/Trinetra/frontend`:

```bash
# Install dependencies
npm install

# Start local dev server (default port 5173, proxies /api to http://localhost:5261)
npm run dev -- --host 0.0.0.0

# Run all unit and integration tests
npm test -- --run

# Run a specific test file
npx vitest run src/features/cameras/CameraForm.test.tsx

# Typecheck TypeScript files
npm run typecheck

# Build production bundle
npm run build
```

### Full Verification Pipeline
Before submitting any changes, always run the full verification command:
```bash
npm test -- --run && npm run typecheck && npm run build
```
All tests must pass and typecheck must yield zero errors.
