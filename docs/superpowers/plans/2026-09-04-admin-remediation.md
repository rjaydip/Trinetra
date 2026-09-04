# Admin Remediation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add permission-aware administration pages for existing APIs and document the API operations that remain unavailable.

**Architecture:** Extend the typed API client first, then mount feature-local admin pages under a single guarded Admin route. Each page offers only server-supported operations; unavailable operations appear as explicit capability messages, never optimistic local state.

**Tech Stack:** React 19, TypeScript, React Router 7, TanStack Query 5, React Hook Form 7, Zod 4, Vitest, Testing Library.

**Spec:** `docs/superpowers/specs/2026-09-04-registry-admin-remediation-design.md`

## Global Constraints

- Preserve unrelated dirty files and commit only files named by each task.
- Keep server permissions authoritative and hide navigation only as a presentation aid.
- Use exact contracts from `Contracts.cs` and `Responses.cs`; do not invent role, member, person-watchlist, vehicle-watchlist, or direct-camera-credential mutations.
- Keep API key raw material in local component state only, show it once, copy through the Clipboard API, and erase it when its disclosure closes or unmounts.
- Treat deactivation conflicts as actionable API problems; never default a hierarchy child strategy.
- Create backend work-item documentation instead of an external ticket because no ticketing integration is in scope.

---

## File Structure

| Path | Responsibility |
|---|---|
| `frontend/src/api/models.ts`, `frontend/src/api/endpoints.ts` | Admin request, response, and typed endpoint groups. |
| `frontend/src/features/admin/AdminPage.tsx` | Admin route layout and capability-aware subnavigation. |
| `frontend/src/features/admin/HierarchyPage.tsx` | Organization, unit, geographic-area, and site operations. |
| `frontend/src/features/admin/AccessGroupsPage.tsx` | Read/create/scope group operations. |
| `frontend/src/features/admin/RolesPage.tsx` | Read-only roles and permission catalogue. |
| `frontend/src/features/admin/WatchlistPage.tsx` | Plate watchlist and alert acknowledgement with unavailable tabs. |
| `frontend/src/features/admin/ApiKeysPage.tsx` | Key lifecycle and one-time secret disclosure. |
| `frontend/src/features/admin/*.test.tsx` | Permission, mutation, unavailable-state, and secret handling tests. |
| `docs/API-WORK-ITEMS.md` | Missing APIs required to complete deferred admin functions. |

### Task 1: Add typed admin contracts and endpoint methods

**Files:**
- Modify: `frontend/src/api/models.ts`
- Modify: `frontend/src/api/endpoints.ts`
- Create: `frontend/src/api/admin.test.ts`

**Interfaces:**
- Consumes: organization, geography, site, access-group, role, permission, watchlist, alert, and API-key contracts.
- Produces: `api.admin.organizations`, `api.admin.geography`, `api.admin.groups`, `api.admin.roles`, `api.admin.watchlist`, and `api.admin.apiKeys` endpoint groups.

- [ ] **Step 1: Write failing endpoint tests**

```ts
it('creates an API key with the selected group and expiry', async () => {
  await api.admin.apiKeys.create({ displayName: 'AI worker', groupId, expiresAt: '2027-01-01T00:00:00Z' });
  expect(fetch).toHaveBeenCalledWith(expect.stringContaining('/api/v1/api-keys'), expect.objectContaining({ method: 'POST' }));
});

it('deactivates a watchlist entry instead of deleting local state', async () => {
  await api.admin.watchlist.deactivate(entryId);
  expect(fetch).toHaveBeenCalledWith(expect.stringContaining(`/api/v1/watchlist/${entryId}`), expect.objectContaining({ method: 'DELETE' }));
});
```

- [ ] **Step 2: Run focused tests and verify failure**

Run: `npm test -- --run src/api/admin.test.ts`

Expected: FAIL because the admin endpoint groups are absent.

- [ ] **Step 3: Implement exact client groups**

Add types and client calls for only routes mapped by `HierarchyEndpoints`, `AccessGroupEndpoints`, `WatchlistEndpoints`, and `ApiKeyEndpoints`. Add role/permission list reads, but no role mutation methods. Model deactivation requests as `{ childStrategy?: 'cascade' | 'reparent'; newParentId?: string }`; do not automatically populate either value.

- [ ] **Step 4: Run API regression tests**

Run: `npm test -- --run src/api/admin.test.ts src/api/client.test.ts src/api/models.test.ts`

Expected: PASS; all request methods, bodies, and paths match the contracts.

- [ ] **Step 5: Commit the task**

```bash
git add frontend/src/api/models.ts frontend/src/api/endpoints.ts frontend/src/api/admin.test.ts
git commit -m "feat: add admin api client"
```

### Task 2: Add the guarded admin shell, hierarchy, roles, and access-group pages

**Files:**
- Create: `frontend/src/features/admin/AdminPage.tsx`
- Create: `frontend/src/features/admin/HierarchyPage.tsx`
- Create: `frontend/src/features/admin/RolesPage.tsx`
- Create: `frontend/src/features/admin/AccessGroupsPage.tsx`
- Create: `frontend/src/features/admin/AdminPage.test.tsx`
- Create: `frontend/src/features/admin/HierarchyPage.test.tsx`
- Create: `frontend/src/features/admin/AccessGroupsPage.test.tsx`
- Modify: `frontend/src/App.tsx`
- Modify: `frontend/src/components/AppShell.tsx`
- Modify: `frontend/src/styles.css`

**Interfaces:**
- Consumes: Task 1 `api.admin` methods and `hasPermission`.
- Produces: `/admin`, `/admin/hierarchy`, `/admin/roles`, and `/admin/access-groups` routes with permission-aware actions.

- [ ] **Step 1: Write failing UI tests**

```tsx
it('shows create organization only to organization.manage users', () => {
  renderHierarchy({ permissions: ['organization.read'] });
  expect(screen.queryByRole('button', { name: /create organization/i })).not.toBeInTheDocument();
});

it('requires an explicit child strategy after a hierarchy deactivation conflict', async () => {
  deactivate.mockRejectedValue(new ApiProblem(409, 'Children must be handled', 'Choose cascade or reparent.'));
  renderHierarchy({ permissions: ['geography.manage'] });
  await user.click(screen.getByRole('button', { name: /deactivate area/i }));
  expect(await screen.findByText(/choose cascade or reparent/i)).toBeVisible();
  expect(screen.getByRole('radio', { name: /cascade/i })).toBeVisible();
});
```

- [ ] **Step 2: Run focused tests and verify failure**

Run: `npm test -- --run src/features/admin/AdminPage.test.tsx src/features/admin/HierarchyPage.test.tsx src/features/admin/AccessGroupsPage.test.tsx`

Expected: FAIL because the routes and components do not exist.

- [ ] **Step 3: Implement the admin shell and supported hierarchy actions**

Render Admin navigation only when the user has at least one supported admin-read permission and guard every route with its exact read permission. Build create forms for organizations, units, geographic areas, and sites using their API reference lists. On a 409 deactivation problem, show the server detail and reveal explicit `cascade` / `reparent` controls; only enable a second attempt once the user chooses a strategy, and require `newParentId` for reparent.

Build `RolesPage` as a table of roles and permission categories with a plain statement that role editing is not available. Build `AccessGroupsPage` with list/detail/create, members, and add/remove scope actions. Do not add group deletion, update, or member mutation controls.

- [ ] **Step 4: Run hierarchy/access test suite**

Run: `npm test -- --run src/features/admin/AdminPage.test.tsx src/features/admin/HierarchyPage.test.tsx src/features/admin/AccessGroupsPage.test.tsx src/auth/permissions.test.tsx`

Expected: PASS; unavailable mutations are absent and conflicts require deliberate choices.

- [ ] **Step 5: Commit the task**

```bash
git add frontend/src/features/admin/AdminPage.tsx frontend/src/features/admin/HierarchyPage.tsx frontend/src/features/admin/RolesPage.tsx frontend/src/features/admin/AccessGroupsPage.tsx frontend/src/features/admin/AdminPage.test.tsx frontend/src/features/admin/HierarchyPage.test.tsx frontend/src/features/admin/AccessGroupsPage.test.tsx frontend/src/App.tsx frontend/src/components/AppShell.tsx frontend/src/styles.css
git commit -m "feat: add hierarchy and access group admin pages"
```

### Task 3: Build watchlist and one-time API-key pages

**Files:**
- Create: `frontend/src/features/admin/WatchlistPage.tsx`
- Create: `frontend/src/features/admin/WatchlistPage.test.tsx`
- Create: `frontend/src/features/admin/ApiKeysPage.tsx`
- Create: `frontend/src/features/admin/ApiKeysPage.test.tsx`
- Modify: `frontend/src/features/admin/AdminPage.tsx`

**Interfaces:**
- Consumes: Task 1 watchlist, alert, API-key, and access-group methods.
- Produces: `/admin/watchlist` and `/admin/api-keys`, plus `OneTimeSecret` local state behavior.

- [ ] **Step 1: Write failing safety and capability tests**

```tsx
it('renders the raw key once and removes it when dismissed', async () => {
  createKey.mockResolvedValue({ id: keyId, keyId: 'ak_123', rawKey: 'raw-key-once' });
  renderApiKeys({ permissions: ['apikey.manage'] });
  await submitKeyForm(user);
  expect(await screen.findByText('raw-key-once')).toBeVisible();
  await user.click(screen.getByRole('button', { name: /i saved this key/i }));
  expect(screen.queryByText('raw-key-once')).not.toBeInTheDocument();
});

it('states that person and vehicle watchlists are unavailable', () => {
  renderWatchlist();
  expect(screen.getByRole('tab', { name: /person/i })).toHaveAttribute('aria-disabled', 'true');
  expect(screen.getByRole('tab', { name: /vehicle/i })).toHaveAttribute('aria-disabled', 'true');
});
```

- [ ] **Step 2: Run focused tests and verify failure**

Run: `npm test -- --run src/features/admin/WatchlistPage.test.tsx src/features/admin/ApiKeysPage.test.tsx`

Expected: FAIL because neither page exists.

- [ ] **Step 3: Implement supported watchlist features**

Render the Number plate tab with list/create/deactivate controls and an Alerts subview with acknowledge. Normalize nothing in the client; send the operator's plate input to the API and display the API response. Render Person and Vehicle as disabled tabs with text that their backend contracts are not available. Invalidate only watchlist/alert queries after mutations.

- [ ] **Step 4: Implement the API-key lifecycle with secret erasure**

List keys with revoked/expiry state. The create form selects a live access group, takes display name and optional ISO datetime expiry, and renders `rawKey` only inside a modal/dialog state. Copy with `navigator.clipboard.writeText(rawKey)` from a labelled button; clear the local raw-key state on close and with a cleanup effect. Revoke requires an explicit confirmation and invalidates the list after `204`.

- [ ] **Step 5: Run watchlist/key tests**

Run: `npm test -- --run src/features/admin/WatchlistPage.test.tsx src/features/admin/ApiKeysPage.test.tsx`

Expected: PASS; raw keys never remain after dismissal, and unavailable watchlist types have no mutation form.

- [ ] **Step 6: Commit the task**

```bash
git add frontend/src/features/admin/WatchlistPage.tsx frontend/src/features/admin/WatchlistPage.test.tsx frontend/src/features/admin/ApiKeysPage.tsx frontend/src/features/admin/ApiKeysPage.test.tsx frontend/src/features/admin/AdminPage.tsx
git commit -m "feat: add watchlist and api key administration"
```

### Task 4: Document backend work items and verify the complete frontend

**Files:**
- Create: `docs/API-WORK-ITEMS.md`
- Modify: `frontend/README.md`

**Interfaces:**
- Consumes: the exact unavailable capabilities named in the approved spec.
- Produces: a maintainer-facing record of missing API work and frontend verification instructions.

- [ ] **Step 1: Write a failing documentation assertion**

```ts
it('documents every unavailable admin capability', async () => {
  const workItems = await readFile('../../docs/API-WORK-ITEMS.md', 'utf8');
  expect(workItems).toContain('camera-scoped credential write');
  expect(workItems).toContain('role CRUD and permission assignment');
  expect(workItems).toContain('person and vehicle watchlists');
});
```

Place this assertion in `frontend/src/features/admin/workItems.test.ts` and use Node's `fs/promises` only in the Vitest node environment.

- [ ] **Step 2: Run the documentation assertion and verify failure**

Run: `npm test -- --run src/features/admin/workItems.test.ts`

Expected: FAIL because the work-items document does not exist.

- [ ] **Step 3: Write the work items and frontend operator notes**

Document missing organization/geography update/delete/reactivate, organization-unit update/reactivate, role CRUD and permission assignment, access-group update/delete/member mutation, watchlist update/person/vehicle support, direct camera credential write, manufacturer reference data, and CSV import. For each item include the current user-visible limitation, the required API operation, and the affected UI page. Update `frontend/README.md` with VMS credential and one-time API-key verification notes, without credentials or example secrets.

- [ ] **Step 4: Run full automated verification**

Run: `npm test -- --run && npm run typecheck && npm run build`

Expected: PASS with no TypeScript errors and a generated `frontend/dist/` build.

- [ ] **Step 5: Perform live acceptance when the configured API is available**

Run: `curl --connect-timeout 3 http://localhost:5261/health`

Expected: a successful health response, then verify a real login, camera location update, VMS credential status/save/test, selected discovery-row import, API-key create/copy/dismiss/revoke, and plate-alert acknowledgement. If the API is unavailable, record the connection failure and do not introduce mock substitutes.

- [ ] **Step 6: Commit the task**

```bash
git add docs/API-WORK-ITEMS.md frontend/README.md frontend/src/features/admin/workItems.test.ts
git commit -m "docs: record frontend api work items"
```
