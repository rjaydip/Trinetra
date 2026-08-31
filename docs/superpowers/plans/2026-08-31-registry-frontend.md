# Trinetra Registry Frontend Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a live-data React frontend for Model 1 camera registry, GIS visibility, onboarding, bulk import, and available coverage reporting.

**Architecture:** A Vite React SPA under `frontend/` uses a typed API client and TanStack Query for backend data, React Router for routes, React Hook Form + Zod for forms, and MapLibre for the GIS dashboard. The backend remains the source of truth for permission enforcement, record validation, scope, coverage calculations, and import results.

**Tech Stack:** React, TypeScript, Vite, React Router, TanStack Query, React Hook Form, Zod, MapLibre GL, Vitest, Testing Library.

**Spec:** `docs/superpowers/specs/2026-08-31-registry-frontend-design.md`

## Global Constraints

- Target `VITE_API_BASE_URL`, defaulting to `http://192.168.1.16:5261`; never add mock camera data.
- Authenticate with the existing JSON `/api/v1/auth/login` bearer-token contract and route `mustChangePassword` users to `/password`.
- Use only documented Model 1 APIs and fields; do not manufacture gap/ageing/audit/export capabilities.
- GIS requests require a bounded `bbox` at most 2° by 2°; coverage sectors are estimates and must be labelled as such.
- Keep authorization server-enforced; client permission decoding is presentation-only.
- Meet WCAG 2.1 AA where practical: visible focus, labels, keyboard navigation, text/icon status signals, reduced motion, and focus-managed overlays.
- Treat `docs/FRONTEND-REQUIREMENT.md` as user-owned/untracked unless the user explicitly asks to commit it.

---

## File Structure

| Path | Responsibility |
|---|---|
| `frontend/package.json`, `vite.config.ts`, `tsconfig*.json` | Vite project, scripts, type/build configuration, API dev proxy |
| `frontend/src/main.tsx`, `App.tsx`, `styles.css` | app bootstrap, provider/router shell, global semantic design tokens |
| `frontend/src/api/*` | typed HTTP client, API models, endpoint functions, query-string builders |
| `frontend/src/auth/*` | session persistence, JWT claim decoding, authentication guards/forms |
| `frontend/src/components/*` | shared shell, status, data-state, dialog/drawer, filters, table/form UI |
| `frontend/src/features/map/*` | MapLibre map, GeoJSON transform, clustering, viewport/filter queries |
| `frontend/src/features/cameras/*` | registry, detail, camera creation and bulk JSON import |
| `frontend/src/features/reports/*` | coverage and fleet summaries, supported-feature unavailable state |
| `frontend/src/test/*` | browser API mocks, render helpers, focused component/API tests |

### Task 1: Scaffold the React application and shared design foundation

**Files:**
- Create: `frontend/package.json`, `frontend/index.html`, `frontend/vite.config.ts`, `frontend/tsconfig.json`, `frontend/tsconfig.app.json`, `frontend/src/main.tsx`, `frontend/src/App.tsx`, `frontend/src/styles.css`
- Create: `frontend/src/components/AppShell.tsx`, `frontend/src/components/ui.tsx`
- Test: `frontend/src/App.test.tsx`

**Interfaces:**
- Produces `App`, `AppShell`, and `Button`, `StatusBadge`, `PageState` shared UI primitives.
- Consumes no prior task output.

- [ ] **Step 1: Write the failing smoke test**

```tsx
it('renders the sign-in route', () => {
  renderApp('/login');
  expect(screen.getByRole('heading', { name: /sign in/i })).toBeVisible();
});
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `npm test -- --run src/App.test.tsx`

Expected: FAIL because the Vite application and test command do not exist.

- [ ] **Step 3: Create the Vite application and semantic UI primitives**

```tsx
export function Button({ children, ...props }: ButtonHTMLAttributes<HTMLButtonElement>) {
  return <button className="button" {...props}>{children}</button>;
}
```

Add the production build, test, lint/typecheck scripts, `@vitejs/plugin-react`, React, routing, query, form, validation, map, and test dependencies. Set CSS semantic tokens, visible `:focus-visible`, responsive shell breakpoints, and a reduced-motion media query.

- [ ] **Step 4: Run the smoke test and production build**

Run: `npm test -- --run src/App.test.tsx && npm run build`

Expected: PASS and a generated `dist/` build.

- [ ] **Step 5: Commit**

```bash
git add frontend
git commit -m "feat: scaffold registry frontend"
```

### Task 2: Add typed API, problem handling, and authenticated session

**Files:**
- Create: `frontend/src/api/client.ts`, `frontend/src/api/models.ts`, `frontend/src/api/endpoints.ts`
- Create: `frontend/src/auth/session.ts`, `frontend/src/auth/AuthProvider.tsx`, `frontend/src/auth/LoginPage.tsx`, `frontend/src/auth/PasswordPage.tsx`, `frontend/src/auth/RequireAuth.tsx`
- Test: `frontend/src/api/client.test.ts`, `frontend/src/auth/LoginPage.test.tsx`

**Interfaces:**
- Produces `api`, `ApiProblem`, `useAuth`, `RequireAuth`, `LoginPage`, and `PasswordPage`.
- Consumes `Button` and app routes from Task 1.

- [ ] **Step 1: Write failing API and login tests**

```ts
it('turns a Problem Details response into an ApiProblem', async () => {
  server.use(http.get('/api/v1/cameras', () => HttpResponse.json(
    { title: 'Forbidden', detail: 'No camera.read permission.' }, { status: 403 })));
  await expect(api.cameras.list({})).rejects.toMatchObject({ status: 403, title: 'Forbidden' });
});
```

```tsx
it('stores the returned token after a successful login', async () => {
  renderApp('/login');
  await userEvent.type(screen.getByLabelText(/username/i), 'admin');
  await userEvent.type(screen.getByLabelText(/^password/i), 'valid password');
  await userEvent.click(screen.getByRole('button', { name: /sign in/i }));
  expect(await screen.findByRole('heading', { name: /camera map/i })).toBeVisible();
});
```

- [ ] **Step 2: Run the focused tests to verify they fail**

Run: `npm test -- --run src/api/client.test.ts src/auth/LoginPage.test.tsx`

Expected: FAIL because API/session modules do not exist.

- [ ] **Step 3: Implement API and auth contracts**

```ts
export async function request<T>(path: string, init: RequestInit = {}): Promise<T> {
  const response = await fetch(`${baseUrl}${path}`, withBearerToken(init));
  if (!response.ok) throw await problemFrom(response);
  return response.status === 204 ? undefined as T : response.json() as Promise<T>;
}
```

Implement login, password change, sessionStorage persistence, expired-session clearing, `mustChangePassword` routing, and a safe inline error summary.

- [ ] **Step 4: Run focused tests and typecheck**

Run: `npm test -- --run src/api/client.test.ts src/auth/LoginPage.test.tsx && npm run typecheck`

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add frontend
git commit -m "feat: add authenticated API client"
```

### Task 3: Implement dashboard GIS data, map, filters, and camera drawer

**Files:**
- Create: `frontend/src/features/map/geo.ts`, `MapPage.tsx`, `CameraMap.tsx`, `MapFilters.tsx`, `CameraDetailDrawer.tsx`
- Create: `frontend/src/features/cameras/CameraDetailSections.tsx`
- Test: `frontend/src/features/map/geo.test.ts`, `frontend/src/features/map/MapPage.test.tsx`

**Interfaces:**
- Consumes `api.cameras.list`, `api.gis.cameras`, typed `CameraResponse`, `GeoJsonFeatureCollection`, and `StatusBadge`.
- Produces `MapPage`, `CameraMap`, and `buildMapRequest(bounds, filters)`.

- [ ] **Step 1: Write failing bounds and UI state tests**

```ts
it('does not request a GIS feed wider than the API maximum', () => {
  expect(buildMapRequest([-76, 39, -70, 45], {})).toBeNull();
});
```

```tsx
it('shows an actionable unavailable state when the map request fails', async () => {
  server.use(http.get('/api/v1/gis/cameras', () => HttpResponse.error()));
  renderApp('/dashboard');
  expect(await screen.findByText(/couldn't load map cameras/i)).toBeVisible();
});
```

- [ ] **Step 2: Run map tests to verify they fail**

Run: `npm test -- --run src/features/map/geo.test.ts src/features/map/MapPage.test.tsx`

Expected: FAIL because map modules do not exist.

- [ ] **Step 3: Implement viewport-driven mapping**

```ts
export function buildMapRequest(bounds: Bounds, filters: MapFilters): URLSearchParams | null {
  if (bounds.east - bounds.west > 2 || bounds.north - bounds.south > 2) return null;
  return query({ bbox: `${bounds.west},${bounds.south},${bounds.east},${bounds.north}`,
    organizationUnitId: filters.organizationUnitId,
    operationalStatus: filters.operationalStatus,
    maintenanceStatus: filters.maintenanceStatus,
    includeSectors: filters.coverage });
}
```

Fit the initial map to live registry coordinates, debounce map move events, cluster marker features, apply client-side camera-type/connectivity filtering only after GeoJSON arrives, and open a focus-managed drawer that fetches one camera record. Label coverage as estimated.

- [ ] **Step 4: Run map tests and build**

Run: `npm test -- --run src/features/map/geo.test.ts src/features/map/MapPage.test.tsx && npm run build`

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add frontend
git commit -m "feat: add live GIS dashboard"
```

### Task 4: Implement the registry list and camera detail views

**Files:**
- Create: `frontend/src/features/cameras/RegistryPage.tsx`, `CameraFilters.tsx`, `CameraTable.tsx`, `CameraCards.tsx`, `CameraDetailPage.tsx`
- Test: `frontend/src/features/cameras/RegistryPage.test.tsx`, `CameraDetailPage.test.tsx`

**Interfaces:**
- Consumes `api.cameras.list`, `api.cameras.get`, health and maintenance endpoint methods.
- Produces cursor-aware `RegistryPage` and `CameraDetailPage`.

- [ ] **Step 1: Write failing registry tests**

```tsx
it('uses the backend next cursor for the next page', async () => {
  renderApp('/cameras');
  await userEvent.click(await screen.findByRole('button', { name: /next page/i }));
  expect(lastCameraRequest.searchParams.get('cursor')).toBe('next-page-token');
});
```

- [ ] **Step 2: Run the registry tests to verify they fail**

Run: `npm test -- --run src/features/cameras/RegistryPage.test.tsx src/features/cameras/CameraDetailPage.test.tsx`

Expected: FAIL because registry components do not exist.

- [ ] **Step 3: Implement live list/detail views**

Use URL query parameters for `q`, statuses, camera type, organization unit, site, geographic area, and `includeRetired`; pass only supported names to the API. Render an accessible table above the mobile card breakpoint and cards below it. Detail uses only API response properties and conditionally fetches health/maintenance sections, showing a permission-aware unavailable state on 403.

- [ ] **Step 4: Run registry tests and typecheck**

Run: `npm test -- --run src/features/cameras/RegistryPage.test.tsx src/features/cameras/CameraDetailPage.test.tsx && npm run typecheck`

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add frontend
git commit -m "feat: add camera registry and detail views"
```

### Task 5: Implement manual onboarding and JSON bulk import

**Files:**
- Create: `frontend/src/features/cameras/CameraForm.tsx`, `NewCameraPage.tsx`, `BulkImportPage.tsx`, `import.ts`
- Test: `frontend/src/features/cameras/CameraForm.test.tsx`, `BulkImportPage.test.tsx`

**Interfaces:**
- Consumes `CameraWriteRequest`, `api.cameras.create`, `api.cameras.bulkImport`, QueryClient invalidation.
- Produces `toCameraWriteRequest(formValues)` and import preview/mutation UI.

- [ ] **Step 1: Write failing form and import tests**

```ts
it('does not include an empty optional port in the request', () => {
  expect(toCameraWriteRequest(validForm({ port: '' })).port).toBeUndefined();
});
```

```tsx
it('shows row errors supplied by the bulk import result', async () => {
  renderApp('/cameras/import');
  await uploadJson('cameras.json', [{ cameraCode: 'BAD' }]);
  expect(await screen.findByText(/cameraType is required/i)).toBeVisible();
});
```

- [ ] **Step 2: Run onboarding tests to verify they fail**

Run: `npm test -- --run src/features/cameras/CameraForm.test.tsx src/features/cameras/BulkImportPage.test.tsx`

Expected: FAIL because onboarding modules do not exist.

- [ ] **Step 3: Implement only the documented write flow**

Use Zod to require camera code, name, organization unit, site, type, latitude, and longitude; validate numeric optional fields/ranges before submit, while surfacing API validation verbatim as safe form feedback. Parse only `.json`, validate a `BulkImportRequest` shape of 1..500 rows, show a read-only preview, submit with `insert`/`upsert`, then invalidate camera and GIS queries after successful rows.

- [ ] **Step 4: Run onboarding tests and build**

Run: `npm test -- --run src/features/cameras/CameraForm.test.tsx src/features/cameras/BulkImportPage.test.tsx && npm run build`

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add frontend
git commit -m "feat: add camera onboarding workflows"
```

### Task 6: Implement available reports and verify accessibility/state coverage

**Files:**
- Create: `frontend/src/features/reports/ReportsPage.tsx`, `CoverageSummary.tsx`
- Modify: `frontend/src/App.tsx`, `frontend/src/components/AppShell.tsx`
- Test: `frontend/src/features/reports/ReportsPage.test.tsx`, `frontend/src/App.test.tsx`

**Interfaces:**
- Consumes `api.gis.coverage`, `api.overview` and `ApiProblem`.
- Produces `ReportsPage` with data, empty/error/loading, and unavailable states.

- [ ] **Step 1: Write failing report tests**

```tsx
it('does not present unavailable coverage gaps as zero', async () => {
  renderApp('/reports');
  expect(await screen.findByText(/coverage-gap analysis is not available yet/i)).toBeVisible();
  expect(screen.queryByText(/^0 gaps$/i)).not.toBeInTheDocument();
});
```

- [ ] **Step 2: Run report test to verify it fails**

Run: `npm test -- --run src/features/reports/ReportsPage.test.tsx`

Expected: FAIL because report components do not exist.

- [ ] **Step 3: Implement coverage/fleet reports and complete keyboard paths**

Render only backend coverage buckets and fleet total/unreachable counts. Include an explicit unavailable card for gaps and ageing infrastructure, without calling an unimplemented endpoint as a data source. Verify all navigation, filters, drawers, dialogs, and form controls have label/focus behavior; add stable status text beside each color treatment.

- [ ] **Step 4: Run full frontend verification**

Run: `npm test -- --run && npm run typecheck && npm run build`

Expected: PASS with no TypeScript or build errors.

- [ ] **Step 5: Commit**

```bash
git add frontend
git commit -m "feat: add live coverage reports"
```

### Task 7: Validate the live integration and document configuration

**Files:**
- Create: `frontend/.env.example`, `frontend/README.md`
- Modify: `README.md`

**Interfaces:**
- Consumes completed frontend build and the deployed API configuration.
- Produces deployment and live test instructions.

- [ ] **Step 1: Write configuration documentation assertions**

Document `VITE_API_BASE_URL`, the required `Auth:AllowedOrigins` API configuration, valid login flow, and the exact verification command. The documentation must state that a failed live connection is an environment state, not a mock-data fallback.

- [ ] **Step 2: Run the frontend locally**

Run: `npm run dev -- --host 0.0.0.0`

Expected: application starts and shows its sign-in screen.

- [ ] **Step 3: Perform live verification when the backend is reachable**

Run: `curl --connect-timeout 3 http://192.168.1.16:5261/health`

Expected: successful health response, then validate login, seeded map markers, registry row/details, and coverage summary in the browser using valid credentials. If unavailable, capture the connection error in the final handoff without substituting mock data.

- [ ] **Step 4: Run final test/build verification**

Run: `npm test -- --run && npm run typecheck && npm run build`

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add frontend README.md
git commit -m "docs: document registry frontend setup"
```

## Plan Self-Review

- **Spec coverage:** Tasks 1-7 cover app setup, authentication, GIS map, registry/detail, onboarding/import, available reports, accessibility, docs, and backend-live validation. The specified but unsupported audit, server export, ageing, and gap functionality are deliberately represented as unavailable rather than fabricated.
- **Placeholder scan:** No open-ended implementation, validation, or test instructions remain; exact APIs, module boundaries, assertions, and commands are provided.
- **Type consistency:** `CameraWriteRequest`, `GeoJsonFeatureCollection`, `ApiProblem`, `buildMapRequest`, `toCameraWriteRequest`, and route paths are defined consistently across producing and consuming tasks.
