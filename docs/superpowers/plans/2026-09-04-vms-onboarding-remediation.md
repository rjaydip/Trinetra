# VMS Onboarding Remediation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add permission-aware VMS creation, credential handling, connection testing, discovery enrichment, and bulk-import onboarding to the existing React frontend.

**Architecture:** Add typed VMS and credential endpoints to the existing API boundary. Feature-local forms map VMS-discovery rows plus human-entered placement data into `CameraWriteRequest` values, then use the existing bulk-import endpoint in `upsert` mode; secret inputs live only in component state and are never cached.

**Tech Stack:** React 19, TypeScript, React Router 7, TanStack Query 5, React Hook Form 7, Zod 4, Vitest, Testing Library.

**Spec:** `docs/superpowers/specs/2026-09-04-registry-admin-remediation-design.md`

## Global Constraints

- Preserve unrelated dirty files and commit only task files.
- Require backend permissions in routes and still rely on server authorization for every write.
- Never put passwords, tokens, or raw API secrets in URLs, query keys, logs, fixtures, persisted session data, or React Query data.
- Clear credential values immediately after a successful credential mutation and after page unmount.
- Use `GET /api/v1/vms/{id}/cameras` only as discovery input; selected rows are persisted through the approved `POST /api/v1/cameras/bulk-import` `upsert` workflow.
- Keep VMS and direct-camera credential capability separate; do not imply that `CredentialReference` stores a direct-camera secret.
- Display `BulkImportResult.rows` rather than claiming an all-or-nothing import outcome.

---

## File Structure

| Path | Responsibility |
|---|---|
| `frontend/src/api/models.ts` | VMS, credential, connection-test, and federated-camera types. |
| `frontend/src/api/endpoints.ts` | Typed VMS, credential, test, and discovery calls. |
| `frontend/src/features/vms/VmsPage.tsx` | Target list, creation and selection entry point. |
| `frontend/src/features/vms/VmsForm.tsx` | VMS configuration form with backend-safe fields. |
| `frontend/src/features/vms/CredentialPanel.tsx` | Secret input, status badge, and connection-test feedback. |
| `frontend/src/features/vms/DiscoveryPage.tsx` | Selection, per-row enrichment, and upsert import submission. |
| `frontend/src/features/vms/discovery.ts` | Pure discovery-row to camera-write mapping and validation. |
| `frontend/src/features/vms/*.test.tsx` | Component/API behavior and secret-safety regressions. |
| `frontend/src/App.tsx`, `frontend/src/components/AppShell.tsx` | Permission-aware VMS routes and navigation. |

### Task 1: Add typed VMS, credential, and connection-test client methods

**Files:**
- Modify: `frontend/src/api/models.ts`
- Modify: `frontend/src/api/endpoints.ts`
- Create: `frontend/src/api/vms.test.ts`

**Interfaces:**
- Consumes: `ConnectorTargetRequest`, `CredentialRequest`, `CredentialExistsResponse`, `ConnectionTestAccepted`, `ConnectionTestResult`, and `FederatedCameraResponse` from the API contracts.
- Produces: `api.vms.list/create/get/discoveredCameras`, `api.credentials.status/save`, and `api.connectionTests.create/get`.

- [ ] **Step 1: Write failing request-shape tests**

```ts
it('writes a VMS credential to the VMS-scoped endpoint', async () => {
  await api.credentials.save('11111111-1111-4111-8111-111111111111', { username: 'operator', password: 'secret' });
  expect(fetch).toHaveBeenCalledWith(expect.stringContaining('/api/v1/vms/11111111-1111-4111-8111-111111111111/credential'), expect.objectContaining({ method: 'PUT' }));
});

it('lists discovered cameras from the selected VMS', async () => {
  await api.vms.discoveredCameras('11111111-1111-4111-8111-111111111111');
  expect(fetch).toHaveBeenCalledWith(expect.stringContaining('/api/v1/vms/11111111-1111-4111-8111-111111111111/cameras'), expect.anything());
});
```

- [ ] **Step 2: Run the focused client tests and verify failure**

Run: `npm test -- --run src/api/vms.test.ts`

Expected: FAIL because the typed endpoint groups do not exist.

- [ ] **Step 3: Add contract interfaces and methods**

Add exact camelCase TypeScript interfaces matching `Responses.cs` and `Contracts.cs`. Use `PUT` for `api.credentials.save(id, body)`, `GET` for status, and the API's asynchronous connection-test create/status URLs. Keep `json()` post-only; use explicit request init for `PUT`.

- [ ] **Step 4: Run API-client tests**

Run: `npm test -- --run src/api/client.test.ts src/api/models.test.ts src/api/vms.test.ts`

Expected: PASS; all VMS requests include the existing bearer authorization behavior.

- [ ] **Step 5: Commit the task**

```bash
git add frontend/src/api/models.ts frontend/src/api/endpoints.ts frontend/src/api/vms.test.ts
git commit -m "feat: add vms api client"
```

### Task 2: Build VMS registration and secure credential controls

**Files:**
- Create: `frontend/src/features/vms/VmsPage.tsx`
- Create: `frontend/src/features/vms/VmsForm.tsx`
- Create: `frontend/src/features/vms/CredentialPanel.tsx`
- Create: `frontend/src/features/vms/VmsPage.test.tsx`
- Create: `frontend/src/features/vms/CredentialPanel.test.tsx`
- Modify: `frontend/src/App.tsx`
- Modify: `frontend/src/components/AppShell.tsx`
- Modify: `frontend/src/styles.css`

**Interfaces:**
- Consumes: Task 1 `api.vms`, `api.credentials`, and `api.connectionTests` plus `hasPermission`.
- Produces: `/vms`, `/vms/:vmsId`, and a credential status label that says only `Credential set` or `Credential not set`.

- [ ] **Step 1: Write failing flow tests**

```tsx
it('does not render VMS navigation for a user without vms.read', () => {
  renderShell({ permissions: [] });
  expect(screen.queryByRole('link', { name: /vms/i })).not.toBeInTheDocument();
});

it('clears the password after credential save', async () => {
  renderCredentialPanel();
  await user.type(screen.getByLabelText(/password/i), 'never-render-again');
  await user.click(screen.getByRole('button', { name: /save credential/i }));
  expect(await screen.findByText(/credential set/i)).toBeVisible();
  expect(screen.getByLabelText(/password/i)).toHaveValue('');
  expect(screen.queryByText('never-render-again')).not.toBeInTheDocument();
});
```

- [ ] **Step 2: Run focused tests and verify failure**

Run: `npm test -- --run src/features/vms/VmsPage.test.tsx src/features/vms/CredentialPanel.test.tsx`

Expected: FAIL because VMS UI and routes do not exist.

- [ ] **Step 3: Implement configuration and credential forms**

Create the VMS form with code, organization unit, site, display name, vendor, endpoint, credential reference, TLS, and optional rate/poll fields matching `ConnectorTargetRequest`. Use live organization/unit/site selectors from the registry form rather than duplicated hardcoded data. In `CredentialPanel`, validate that password or token is non-empty, call the VMS-scoped save endpoint, replace values with empty strings in `finally`, invalidate credential status, and show the returned `updatedAt` without the secret.

Implement `Test connection` by creating the test, polling its returned status URL with a bounded refetch interval while status is pending, then rendering its result/error. Do not record credentials in the test mutation key or result.

- [ ] **Step 4: Add guarded routes and navigation**

Wrap create/update pages in existing `RequirePermission` gates; hide navigation using `hasPermission(session, 'vms.read')`. Retain direct route protection so a hidden link is not the authorization mechanism.

- [ ] **Step 5: Run VMS UI tests**

Run: `npm test -- --run src/features/vms/VmsPage.test.tsx src/features/vms/CredentialPanel.test.tsx src/auth/permissions.test.tsx`

Expected: PASS; secrets do not appear after save and non-read users cannot navigate to the feature.

- [ ] **Step 6: Commit the task**

```bash
git add frontend/src/features/vms frontend/src/App.tsx frontend/src/components/AppShell.tsx frontend/src/styles.css
git commit -m "feat: add vms credential management"
```

### Task 3: Enrich discovered cameras and submit an upsert import

**Files:**
- Create: `frontend/src/features/vms/discovery.ts`
- Create: `frontend/src/features/vms/discovery.test.ts`
- Create: `frontend/src/features/vms/DiscoveryPage.tsx`
- Create: `frontend/src/features/vms/DiscoveryPage.test.tsx`
- Modify: `frontend/src/App.tsx`
- Modify: `frontend/src/features/vms/VmsPage.tsx`

**Interfaces:**
- Consumes: `FederatedCameraResponse[]`, the selected VMS id, live reference selectors, and `api.cameras.bulkImport({ mode: 'upsert', items })`.
- Produces: `toDiscoveredCameraWriteRequest(row, enrichment): CameraWriteRequest` and `/vms/:vmsId/discovery`.

- [ ] **Step 1: Write failing mapper and component tests**

```ts
it('maps a selected discovered camera to an upsert-ready write request', () => {
  expect(toDiscoveredCameraWriteRequest(discovered, enrichment)).toMatchObject({
    cameraCode: 'NVR-001-CAM-07', vmsId: discovered.targetId, streamReference: 'rtsp://stream/7', latitude: 19.0760123,
  });
});

it('rejects an incomplete selected row before calling bulk import', async () => {
  renderDiscoveryPage();
  await user.click(screen.getByRole('checkbox', { name: /gate 7/i }));
  await user.click(screen.getByRole('button', { name: /import selected/i }));
  expect(api.cameras.bulkImport).not.toHaveBeenCalled();
  expect(screen.getByText(/latitude is required/i)).toBeVisible();
});
```

- [ ] **Step 2: Run focused tests and verify failure**

Run: `npm test -- --run src/features/vms/discovery.test.ts src/features/vms/DiscoveryPage.test.tsx`

Expected: FAIL because the mapper and discovery route do not exist.

- [ ] **Step 3: Implement pure mapping and selected-row enrichment**

Use the native camera id to generate a clearly editable proposed camera code (`${vmsCode}-${nativeCameraId}`), copy name/model/stream references when available, set `vmsId` to the selected target, and require organization unit, site, camera type, latitude, and longitude. Use `roundCoordinate` from the registry vocabulary helper. Do not require manual-only manufacturer/IP/port/protocol fields.

Render the discovered list as a semantic table with a checkbox, read-only vendor facts, and expandable enrichment fields. Fetch GIS map context only for valid selected coordinates. Keep values for rows the user has not selected but do not include them in the import body.

- [ ] **Step 4: Submit and render authoritative row results**

Build `{ mode: 'upsert', items: selectedRows.map(toDiscoveredCameraWriteRequest) }`, call bulk import, invalidate camera and map queries, and render created, updated, failed totals plus every `result.rows` entry. A failed row remains visible with its API error; no success toast claims all selected cameras were persisted.

- [ ] **Step 5: Run VMS discovery tests**

Run: `npm test -- --run src/features/vms/discovery.test.ts src/features/vms/DiscoveryPage.test.tsx src/features/cameras/import.test.ts`

Expected: PASS; selected enriched rows become an upsert request and API row errors are visible.

- [ ] **Step 6: Commit the task**

```bash
git add frontend/src/features/vms/discovery.ts frontend/src/features/vms/discovery.test.ts frontend/src/features/vms/DiscoveryPage.tsx frontend/src/features/vms/DiscoveryPage.test.tsx frontend/src/features/vms/VmsPage.tsx frontend/src/App.tsx
git commit -m "feat: import discovered vms cameras"
```

