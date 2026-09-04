# Registry and Map Remediation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Complete the contract-aligned authentication, registry-form, map-picker, pagination, coverage, and JSON-import review findings in the existing React frontend.

**Architecture:** Keep the Vite React SPA and its typed API client. Add small pure helpers for camera vocabulary, precision, template generation, and conditional validation; add a MapLibre location-picker component that drives React Hook Form values without making the map the sole input path.

**Tech Stack:** React 19, TypeScript, React Router 7, TanStack Query 5, React Hook Form 7, Zod 4, MapLibre GL 5, Vitest, Testing Library.

**Spec:** `docs/superpowers/specs/2026-09-04-registry-admin-remediation-design.md`

## Global Constraints

- Preserve the existing user-owned dirty files; add and commit only files belonging to each task.
- Use the API default configured by the existing frontend; never add mock camera or reference records.
- Send the backend's documented string vocabularies exactly; do not add a JSON enum converter or backend change.
- Round camera coordinates to seven fractional places before they reach a write request.
- Required API fields are camera code, name, organization unit, site, camera type, latitude, and longitude.
- Manual registration additionally requires manufacturer, IP address, port, and protocol; NVR-import validation is deliberately different and belongs to the later VMS plan.
- Keep manufacturer free text because no API exposes a manufacturer catalogue.
- Render server problem details next to the action that failed and preserve valid user-entered values.
- Meet keyboard and screen-reader needs with labels, visible focus, `aria-describedby` validation, `aria-expanded` disclosures, and non-map alternatives for every picker input.

---

## File Structure

| Path | Responsibility |
|---|---|
| `frontend/src/auth/PasswordPage.tsx` | Confirmed password-change form and inline rejection feedback. |
| `frontend/src/auth/PasswordPage.test.tsx` | Password comparison and API-problem regression tests. |
| `frontend/src/features/cameras/cameraVocabulary.ts` | Backend-owned select strings and seven-place coordinate helper. |
| `frontend/src/features/cameras/LocationPicker.tsx` | MapLibre marker and azimuth interaction, with controlled values. |
| `frontend/src/features/cameras/LocationPicker.test.tsx` | Location rounding, field-to-picker, and accessible azimuth tests. |
| `frontend/src/features/cameras/CameraForm.tsx` | Manual camera validation, required markers, progressive disclosure, and picker composition. |
| `frontend/src/features/cameras/CameraForm.test.tsx` | Form validation and write mapping tests. |
| `frontend/src/features/cameras/NewCameraPage.tsx` | Live organization, unit, site, VMS, and map-data queries. |
| `frontend/src/features/cameras/RegistryPage.tsx` | Cursor UX copy and disabled state. |
| `frontend/src/features/cameras/BulkImportPage.tsx` | Downloadable JSON sample. |
| `frontend/src/features/cameras/import.ts` | Sample payload factory and JSON parsing. |
| `frontend/src/features/cameras/import.test.ts` | JSON template contract tests. |
| `frontend/src/features/reports/ReportsPage.tsx` | Only API-backed coverage filter controls. |

### Task 1: Make password change rejection explicit

**Files:**
- Create: `frontend/src/auth/PasswordPage.test.tsx`
- Modify: `frontend/src/auth/PasswordPage.tsx`

**Interfaces:**
- Consumes: `useAuth().changePassword({ currentPassword, newPassword })` and `ApiProblem.detail`.
- Produces: a password form with `currentPassword`, `newPassword`, and `confirmPassword` inputs and a client-side error string.

- [ ] **Step 1: Write failing component tests**

```tsx
it('does not call changePassword when the new password equals the current password', async () => {
  renderPasswordPage();
  await user.type(screen.getByLabelText(/current password/i), 'same-secret');
  await user.type(screen.getByLabelText(/^new password/i), 'same-secret');
  await user.type(screen.getByLabelText(/confirm new password/i), 'same-secret');
  await user.click(screen.getByRole('button', { name: /change password/i }));
  expect(changePassword).not.toHaveBeenCalled();
  expect(screen.getByRole('alert')).toHaveTextContent('must be different');
});

it('renders the API problem detail when the server rejects a password', async () => {
  changePassword.mockRejectedValue(new ApiProblem(400, 'Password unchanged', 'Choose a different password.'));
  renderPasswordPage();
  await completeValidPasswordForm(user);
  await user.click(screen.getByRole('button', { name: /change password/i }));
  expect(await screen.findByRole('alert')).toHaveTextContent('Choose a different password.');
});
```

- [ ] **Step 2: Run the focused test and verify failure**

Run: `npm test -- --run src/auth/PasswordPage.test.tsx`

Expected: FAIL because the page has no confirmation input and submits equal values.

- [ ] **Step 3: Add minimal comparison validation and connected feedback**

```tsx
if (newPassword === currentPassword) {
  setError('Your new password must be different from your current password.');
  return;
}
if (newPassword !== confirmPassword) {
  setError('New password and confirmation must match.');
  return;
}
```

Add `<label htmlFor="confirm-password">Confirm new password</label>` with a required password input. Set `aria-describedby="password-change-error"` on all three password inputs while an error exists; retain the existing `ApiProblem.detail` catch path.

- [ ] **Step 4: Run focused tests and the existing login suite**

Run: `npm test -- --run src/auth/PasswordPage.test.tsx src/auth/LoginPage.test.tsx`

Expected: PASS, including the existing `mustChangePassword` redirect test.

- [ ] **Step 5: Commit the task**

```bash
git add frontend/src/auth/PasswordPage.tsx frontend/src/auth/PasswordPage.test.tsx
git commit -m "fix: validate password change input"
```

### Task 2: Centralize vocabulary, precision, and live selectors

**Files:**
- Create: `frontend/src/features/cameras/cameraVocabulary.ts`
- Create: `frontend/src/features/cameras/cameraVocabulary.test.ts`
- Modify: `frontend/src/api/models.ts`
- Modify: `frontend/src/api/endpoints.ts`
- Modify: `frontend/src/features/cameras/NewCameraPage.tsx`
- Modify: `frontend/src/features/cameras/CameraForm.tsx`
- Modify: `frontend/src/features/cameras/CameraForm.test.tsx`

**Interfaces:**
- Consumes: existing organization, organization-unit, site endpoints and `GET /api/v1/vms`.
- Produces: `roundCoordinate(value: number): number`, vocabulary arrays, `CameraForm` props `organizations`, `organizationUnits`, `sites`, and `vms`.

- [ ] **Step 1: Write failing pure-helper and form tests**

```tsx
it.each([[19.076012345, 19.0760123], [-72.123456789, -72.1234568]])(
  'rounds %s to seven decimal places', (input, expected) => {
    expect(roundCoordinate(input)).toBe(expected);
  },
);

it('requires network details for a manual registration', async () => {
  renderCameraForm();
  await fillApiRequiredFields(user);
  await user.click(screen.getByRole('button', { name: /register camera/i }));
  expect(screen.getByText('Manufacturer is required for manual registration.')).toBeVisible();
  expect(screen.getByText('IP address is required for manual registration.')).toBeVisible();
});
```

- [ ] **Step 2: Run focused tests and verify failure**

Run: `npm test -- --run src/features/cameras/cameraVocabulary.test.ts src/features/cameras/CameraForm.test.tsx`

Expected: FAIL because no helper exists and manual network fields are optional.

- [ ] **Step 3: Define exact vocabulary and API types**

```ts
export const cameraTypes = ['FIXED', 'PTZ', 'DOME', 'BULLET', 'ANPR', 'THERMAL', 'MULTISENSOR', 'OTHER'] as const;
export const protocols = ['RTSP', 'RTSPS', 'ONVIF', 'HTTP', 'HTTPS', 'RTMP', 'SRT', 'OTHER'] as const;
export const operationalStatuses = ['ONLINE', 'OFFLINE', 'DEGRADED', 'UNKNOWN'] as const;
export const connectivityStatuses = ['CONNECTED', 'DISCONNECTED', 'UNKNOWN'] as const;
export const maintenanceStatuses = ['NORMAL', 'REQUIRED', 'UNDER_MAINTENANCE'] as const;
export const roundCoordinate = (value: number) => Number(value.toFixed(7));
```

Add the VMS list response type from `Responses.cs` to `models.ts`, add `api.vms.list()`, and fetch organizations, units after an organization selection, sites, and VMS records in `NewCameraPage`. Do not use a hardcoded organization ID. Disable dependent selects until their data is available and show their API errors through `PageState`.

- [ ] **Step 4: Refactor the form to use selectors and conditional Zod validation**

Move all fixed arrays out of `CameraForm.tsx`. Use a `superRefine` branch for manual fields, preserve API-required fields, and map numeric coordinates through `roundCoordinate` in `toCameraWriteRequest`. Replace the VMS ID text input with a select and retain manufacturer as text input. Pass live selector data through explicit props.

- [ ] **Step 5: Run focused regression tests**

Run: `npm test -- --run src/features/cameras/cameraVocabulary.test.ts src/features/cameras/CameraForm.test.tsx src/features/cameras/NewCameraPage.test.tsx src/api/models.test.ts`

Expected: PASS; the selected VMS ID and seven-place coordinates are serialized correctly.

- [ ] **Step 6: Commit the task**

```bash
git add frontend/src/api/models.ts frontend/src/api/endpoints.ts frontend/src/features/cameras/cameraVocabulary.ts frontend/src/features/cameras/cameraVocabulary.test.ts frontend/src/features/cameras/CameraForm.tsx frontend/src/features/cameras/CameraForm.test.tsx frontend/src/features/cameras/NewCameraPage.tsx
git commit -m "feat: align camera form selectors and validation"
```

### Task 3: Add an accessible map location and azimuth picker

**Files:**
- Create: `frontend/src/features/cameras/LocationPicker.tsx`
- Create: `frontend/src/features/cameras/LocationPicker.test.tsx`
- Modify: `frontend/src/features/cameras/CameraForm.tsx`
- Modify: `frontend/src/features/cameras/NewCameraPage.tsx`
- Modify: `frontend/src/styles.css`

**Interfaces:**
- Consumes: controlled `latitude`, `longitude`, `azimuth`, `onLocationChange`, and `onAzimuthChange`; map data from `api.gis.cameras({ bbox, ... })`.
- Produces: `LocationPicker` whose outputs are valid seven-place `number` coordinates and a 0–359.999 azimuth.

- [ ] **Step 1: Write failing picker tests**

```tsx
it('emits rounded latitude and longitude when the pin moves', () => {
  render(<LocationPicker latitude={19.076} longitude={72.8777} azimuth={null} onLocationChange={onLocationChange} onAzimuthChange={onAzimuthChange} />);
  fireEvent.click(screen.getByRole('button', { name: /set location to 19.076012345/i }));
  expect(onLocationChange).toHaveBeenCalledWith(19.0760123, 72.8777);
});

it('exposes an azimuth number alternative', () => {
  renderPicker();
  expect(screen.getByLabelText(/azimuth/i)).toHaveAttribute('min', '0');
  expect(screen.getByLabelText(/azimuth/i)).toHaveAttribute('max', '359.999');
});
```

- [ ] **Step 2: Run focused tests and verify failure**

Run: `npm test -- --run src/features/cameras/LocationPicker.test.tsx`

Expected: FAIL because `LocationPicker` does not exist.

- [ ] **Step 3: Implement controlled picker behavior**

Use a MapLibre map styled from the existing raster source and add existing GIS features as a non-interactive context layer. Render one draggable marker for a valid form location. Map click and marker drag call `onLocationChange(roundCoordinate(lat), roundCoordinate(lon))`. Place an azimuth handle at a fixed visual distance from the marker; convert drag angle with `((Math.atan2(dx, -dy) * 180 / Math.PI) + 360) % 360`, round to three decimal places, and call `onAzimuthChange`.

For jsdom, expose labelled fallback buttons only in the test environment; production interaction remains the map and drag handle. Keep a numeric azimuth input outside the canvas and route its valid values through the same callback.

- [ ] **Step 4: Wire it into the form without making the map mandatory**

Use `useWatch` and `setValue` in `CameraForm` so text inputs move the marker and picker changes revalidate the same fields. Put the component beside required latitude and longitude inputs. Fetch a bounded GIS context only once form coordinates are valid; do not request an unbounded map source.

- [ ] **Step 5: Run picker and form regression tests**

Run: `npm test -- --run src/features/cameras/LocationPicker.test.tsx src/features/cameras/CameraForm.test.tsx src/features/cameras/NewCameraPage.test.tsx`

Expected: PASS; keyboard entry, map interaction, and rounded write mapping agree.

- [ ] **Step 6: Commit the task**

```bash
git add frontend/src/features/cameras/LocationPicker.tsx frontend/src/features/cameras/LocationPicker.test.tsx frontend/src/features/cameras/CameraForm.tsx frontend/src/features/cameras/NewCameraPage.tsx frontend/src/styles.css
git commit -m "feat: add camera location and azimuth picker"
```

### Task 4: Clarify paging, optional details, coverage filters, and JSON template

**Files:**
- Modify: `frontend/src/features/cameras/CameraForm.tsx`
- Modify: `frontend/src/features/cameras/CameraForm.test.tsx`
- Modify: `frontend/src/features/cameras/RegistryPage.tsx`
- Modify: `frontend/src/features/cameras/RegistryPage.test.tsx`
- Modify: `frontend/src/features/cameras/import.ts`
- Create: `frontend/src/features/cameras/import.test.ts`
- Modify: `frontend/src/features/cameras/BulkImportPage.tsx`
- Modify: `frontend/src/features/cameras/BulkImportPage.test.tsx`
- Modify: `frontend/src/features/reports/ReportsPage.tsx`
- Modify: `frontend/src/features/reports/ReportsPage.test.tsx`

**Interfaces:**
- Consumes: `CameraPage.nextCursor`, `BulkImportRequest`, `CoverageSummaryResponse`, and live organization/geography filter values.
- Produces: `createSampleImport(): BulkImportRequest`, visible `Additional details` disclosure, and an accurately named next-page control.

- [ ] **Step 1: Write failing UI and helper tests**

```tsx
it('disables Next when the API returns no next cursor', () => {
  renderRegistry({ nextCursor: null });
  expect(screen.getByRole('button', { name: /^next$/i })).toBeDisabled();
});

it('creates an insert JSON template containing only contract fields', () => {
  expect(createSampleImport()).toEqual({
    mode: 'insert',
    items: [expect.objectContaining({ cameraCode: 'CAM-EXAMPLE-001', latitude: 19.076, longitude: 72.8777 })],
  });
});

it('keeps optional fields hidden until Additional details is expanded', async () => {
  renderCameraForm();
  expect(screen.queryByLabelText(/mounting height/i)).not.toBeInTheDocument();
  await user.click(screen.getByRole('button', { name: /additional details/i }));
  expect(screen.getByLabelText(/mounting height/i)).toBeVisible();
});
```

- [ ] **Step 2: Run focused tests and verify failure**

Run: `npm test -- --run src/features/cameras/RegistryPage.test.tsx src/features/cameras/import.test.ts src/features/cameras/BulkImportPage.test.tsx src/features/cameras/CameraForm.test.tsx src/features/reports/ReportsPage.test.tsx`

Expected: FAIL because the UI says `Next page`, has no sample factory or disclosure, and coverage filters are not constrained to API parameters.

- [ ] **Step 3: Implement the changes**

Use a button with `aria-expanded` to contain all API-optional fields except the manual network requirements. Place `* Required` once in the form legend and append a visually visible `*` to every required label. Rename the pager button exactly `Next` and preserve the current `cursor` update only when `nextCursor` is not null.

Create the template with one `CameraWriteRequest` object whose six API-required values are valid and whose optional values are absent, not `null` placeholder strings. Build the download with `new Blob([JSON.stringify(createSampleImport(), null, 2)], { type: 'application/json' })`, a temporary object URL, and `download="trinetra-camera-import-sample.json"`; revoke the URL after click. Keep JSON parsing and API row results unchanged.

Remove any free-text coverage filter. If a coverage filter is exposed, bind it only to `organizationUnitId`, `geographicAreaId`, or `bbox` and make it an API-backed selector.

- [ ] **Step 4: Run the full registry/report suite**

Run: `npm test -- --run src/features/cameras src/features/reports`

Expected: PASS, including the existing per-row import-result and coverage-summary tests.

- [ ] **Step 5: Commit the task**

```bash
git add frontend/src/features/cameras/CameraForm.tsx frontend/src/features/cameras/CameraForm.test.tsx frontend/src/features/cameras/RegistryPage.tsx frontend/src/features/cameras/RegistryPage.test.tsx frontend/src/features/cameras/import.ts frontend/src/features/cameras/import.test.ts frontend/src/features/cameras/BulkImportPage.tsx frontend/src/features/cameras/BulkImportPage.test.tsx frontend/src/features/reports/ReportsPage.tsx frontend/src/features/reports/ReportsPage.test.tsx
git commit -m "feat: improve registry onboarding workflows"
```

