# requirements.md — Model 1 Frontend: Registry & GIS Foundation

## Source Scope

This document defines frontend requirements for **Model 1: Centralised CCTV Registry & GIS Mapping**.

Model 1 is a **CCTV metadata registry and GIS visibility layer**. It must support camera onboarding, GIS-based mapping, metadata management, camera health/maintenance visibility, filtering, export, audit trails, and gap-analysis reporting. It must **not** implement centralized live video streaming or recording in the current frontend scope.

The official Model 1 requirements include:

- Bulk import, manual entry, and API-based camera onboarding.
- Interactive GIS map with department, camera type, status, and coverage layers.
- Camera health and maintenance-status monitoring.
- Gap-analysis reports for uncovered zones and ageing infrastructure.
- Role-based search, filtering, export, and metadata audit trails.
- Working registry portal with GIS map view.
- Sample onboarded camera-metadata dataset.
- Registry API documentation.
- Sample gap-analysis report.

## Current Implementation Context

- Backend is already running at: `http://localhost:5261`
- Database is already populated with camera/registry data.
- Frontend must use the existing backend and seeded database data for real-time UI testing.
- Static mock data must not be used for core flows unless explicitly isolated for component-only development.
- Existing project structure, APIs, types, routes, and implemented backend behavior must be treated as the source of truth.
- Do not invent fields, workflows, permissions, or business rules that are not present in the current implementation or official Model 1 scope.

Open question: the current frontend repository structure and exact API contracts were not provided in this chat.

---

## 1. Core User Problems

1. Departments operate CCTV assets independently with no unified frontend for discovery and visibility.
2. Camera metadata is difficult to search, verify, maintain, and audit across departments/geographies.
3. Operators need a GIS-first view to understand camera distribution, operational status, and coverage.
4. Administrators need manual and bulk onboarding workflows for camera registry data.
5. Decision-makers need reports for uncovered zones, ageing infrastructure, and operational readiness.

---

## 2. Frontend Goals

The frontend must provide a clean, production-ready UI for:

- Viewing authorized cameras on an interactive GIS map.
- Testing all UI screens against the live backend at `http://localhost:5261`.
- Reading actual seeded database records instead of mock camera data.
- Registering cameras manually.
- Importing cameras in bulk if backend support exists.
- Searching, filtering, sorting, and exporting camera records.
- Viewing camera metadata, location, ownership, health, maintenance, storage, and coverage details.
- Viewing Model 1 reports such as gap analysis and ageing infrastructure.
- Respecting role-based access and backend authorization.

---

## 3. Required Screens

### 3.1 Dashboard / GIS Map

Primary landing screen.

Must include:

- Interactive GIS map.
- Camera markers rendered from backend data.
- Marker clustering for dense locations.
- Layer/filter controls for:
  - Department
  - Camera type
  - Status
  - Coverage, if coverage data exists
- Search input.
- Filter panel.
- Camera detail side panel or drawer.
- Map legend.
- Status summary counters.
- Gap/coverage visibility toggle if backend supports the data.

Acceptance:

- Camera markers must come from live API data.
- UI must handle populated database records without hardcoded test objects.

---

### 3.2 Camera Registry List

Tabular/list view of authorized camera records.

Must include:

- Search.
- Sort.
- Filters.
- Pagination or virtualization.
- Export action if backend supports it.
- Row click to open camera detail.
- Status indicators.
- Responsive card layout on mobile.

Acceptance:

- Registry records must be fetched from `http://localhost:5261`.
- Search/filter behavior must match backend-supported fields.
- No frontend-only fake fields should be shown.

---

### 3.3 Camera Detail

Detailed metadata view for one camera.

Must include fields that exist in the backend response, such as:

- Camera identity.
- Department/ownership.
- Location and coordinates.
- Camera type.
- Connectivity status.
- Storage details.
- Health status.
- Maintenance status.
- Coverage/range metadata, if available.
- Audit summary/history, if available.
- Created/updated timestamps, if available.

Open question: exact camera-detail DTO must be confirmed from the running backend.

---

### 3.4 Manual Camera Onboarding

Form for creating a single camera registry entry.

Must include only backend-supported fields.

Expected groups:

- Identity.
- Department/ownership.
- Location.
- GIS coordinates.
- Camera type.
- Connectivity metadata.
- Storage metadata.
- Coverage/range fields if supported.
- Maintenance metadata if supported.

Acceptance:

- Form validation must match backend rules.
- Submit must call the live backend.
- Created camera must appear in the list/map after success.

---

### 3.5 Bulk Import

Bulk onboarding workflow, only if backend supports it.

Must include:

- File upload.
- Supported format guidance.
- Template download if backend provides one.
- Validation preview.
- Row-level errors.
- Import confirmation.
- Import summary.

Open question: supported import file types, validation response shape, and commit endpoint are not confirmed.

---

### 3.6 Reports

Model 1 report views.

Must include, if backend provides data:

- Gap-analysis report.
- Ageing infrastructure report.
- Department-wise summary.
- Status-wise summary.
- Export action.

Acceptance:

- Reports must use backend-computed data.
- Frontend must not invent coverage/gap calculations unless backend explicitly exposes the required geometry fields.

---

### 3.7 Audit Trail

View metadata-change history, if backend supports audit APIs.

Must include:

- Entity changed.
- Field changed.
- Previous/current values if permitted.
- Changed by.
- Timestamp.
- Filters if supported.

Open question: audit API availability and permissions are not confirmed.

---

### 3.8 Admin / RBAC Views

Include only if already implemented or exposed by backend.

Possible views:

- Users.
- Roles.
- Groups.
- Department scopes.
- Geography scopes.
- Permissions.

Acceptance:

- UI must reflect backend permissions.
- Unauthorized actions must be hidden or disabled.
- Backend authorization failures must be handled gracefully.

---

## 4. Core Components

### Map Components

- `CameraMap`
- `CameraMarker`
- `CameraCluster`
- `CoverageLayer`
- `MapLayerControl`
- `MapLegend`
- `CameraDetailDrawer`
- `MapSearchBox`
- `LocationPicker`

### Registry Components

- `CameraTable`
- `CameraListCard`
- `CameraFilters`
- `CameraStatusBadge`
- `CameraTypeBadge`
- `ExportButton`
- `ColumnVisibilityControl`

### Form Components

- `CameraForm`
- `DepartmentSelector`
- `GeographySelector`
- `CoordinateInput`
- `CoverageInput`
- `ConnectivityFields`
- `StorageFields`
- `BulkImportDropzone`
- `ImportValidationTable`

### State / Feedback Components

- `PageLoader`
- `InlineLoader`
- `Skeleton`
- `EmptyState`
- `ErrorState`
- `ValidationMessage`
- `SuccessToast`
- `ConfirmDialog`

### Layout Components

- `AppShell`
- `Sidebar`
- `Topbar`
- `Breadcrumbs`
- `ResponsivePanel`
- `PageHeader`

---

## 5. Primary User Flows

### 5.1 View Cameras on GIS Map

1. User opens dashboard.
2. Frontend requests camera data from `http://localhost:5261`.
3. System renders authorized cameras from the populated database.
4. User filters by department/type/status.
5. Map updates markers and layers.
6. User selects a camera.
7. Detail drawer opens with backend camera data.

Success criteria:

- User can visually locate cameras from the real database.
- Map remains usable with the current seeded dataset.
- No hardcoded camera data is required.

---

### 5.2 View Registry List

1. User opens registry page.
2. Frontend fetches paginated/list data from backend.
3. User searches, sorts, or filters records.
4. User opens camera detail.
5. User exports records if permitted.

Success criteria:

- Data shown in list and map is consistent.
- Filters are based on actual backend-supported fields.
- Export respects active filters and permissions.

---

### 5.3 Register Single Camera

1. User opens manual onboarding.
2. User fills required backend-supported fields.
3. User submits form.
4. Backend validates and creates the record.
5. Frontend shows success state.
6. Camera appears in map/list after cache refresh or invalidation.

Success criteria:

- Invalid data is blocked or clearly rejected.
- API errors are displayed clearly.
- Form state is not lost unnecessarily after recoverable failures.

---

### 5.4 Bulk Import Cameras

1. User uploads import file.
2. Frontend sends file to backend validation endpoint.
3. User reviews valid/invalid rows.
4. User confirms import.
5. Backend imports accepted records.
6. Frontend shows import summary.

Success criteria:

- Failed rows show actionable reasons.
- Imported records appear in registry/map.
- Duplicate/invalid entries follow backend behavior.

---

### 5.5 View Reports

1. User opens reports page.
2. Frontend fetches report data from backend.
3. User applies supported filters.
4. Report updates.
5. User exports report if supported.

Success criteria:

- Loading, empty, and error states are distinct.
- Report numbers match backend response.
- Frontend does not fabricate report metrics.

---

## 6. Information Hierarchy

Priority order:

1. GIS location and distribution.
2. Camera operational status.
3. Department/ownership.
4. Camera type.
5. Coverage/range.
6. Connectivity/storage metadata.
7. Maintenance state.
8. Audit/history.
9. Export/report actions.

Interaction priority:

1. Locate cameras.
2. Filter visible records.
3. Inspect camera details.
4. Add/import camera records.
5. Export/report.

---

## 7. Responsive Behavior

### Desktop

- Full GIS map with persistent navigation.
- Side panel for filters and details.
- Full-width registry table.
- Multi-column forms.

### Tablet

- Collapsible navigation.
- Filter drawer.
- Detail drawer or modal.
- Reduced table columns.
- Touch-friendly map controls.

### Mobile

- Map-first layout.
- Bottom-sheet camera detail.
- Filters as full-screen drawer.
- Registry table converted to cards.
- Forms grouped into short sections.
- No critical interaction should depend on hover.

Open question: whether mobile onboarding is required or only mobile viewing/searching.

---

## 8. UI States

### Loading

- Initial page skeleton.
- Inline loaders for map, filters, reports, and detail panels.
- Non-blocking loading during filter changes where possible.

### Empty

Handle:

- No cameras returned.
- No cameras matching filters.
- No report data.
- No audit records.
- No import rows.
- No authorized data.

Each empty state must explain the next valid action.

### Error

Handle:

- Backend unreachable at `http://localhost:5261`.
- API timeout.
- Unauthorized/forbidden response.
- Validation failure.
- Export failure.
- Map/tile loading failure.
- Import failure.
- Save/update failure.

Errors must be actionable and must not expose secrets or internal stack traces.

### Validation

Manual forms must validate:

- Required fields.
- Coordinate format.
- Numeric fields.
- Enum selections.
- File type/size for bulk import.
- Backend duplicate/conflict errors.

### Success

Use lightweight feedback:

- Toast after create/update/import/export.
- “View camera” action after create.
- Import summary after bulk import.
- Cache refresh or route update after mutation.

---

## 9. Accessibility Expectations

- All core workflows must be keyboard-accessible.
- Visible focus states are required.
- Map controls must have labels.
- Form inputs must have labels and error text.
- Status must not rely on color alone.
- Dialogs/drawers must trap focus and restore focus on close.
- Toasts must not be the only place where critical feedback appears.
- Tables/cards must preserve logical reading order.
- Target WCAG 2.1 AA where practical.

---

## 10. Component and State-Management Guidelines

- Use React with TypeScript.
- Use reusable, composable components.
- Keep UI, data-fetching, validation, and state logic separated.
- Prefer typed API clients.
- Use predictable server-state handling, such as TanStack Query or the existing project equivalent.
- Keep local UI state local unless shared across pages.
- Store filter/search state in URL query params where useful.
- Avoid duplicate map/list filter state.
- Use schema-based form validation if already available in the project.
- Lazy-load heavy map/report/import modules.
- Avoid unnecessary re-renders of map markers and table rows.
- Use subtle animations only for feedback, transitions, and spatial clarity.
- Animations must be smooth, lightweight, interruptible, and maintainable.

---

## 11. API / Data Integration Requirements

### Backend Base URL

Use:

```env
API_BASE_URL=http://localhost:5261
```

or the equivalent environment variable used by the current frontend framework, for example:

```env
VITE_API_BASE_URL=http://localhost:5261
NEXT_PUBLIC_API_BASE_URL=http://localhost:5261
```

The exact variable name must follow the existing project setup.

### Required Integration Behavior

- Frontend must connect to the running backend.
- UI testing must use already-populated database records.
- Map markers, registry rows, details, reports, filters, and statuses must be based on live API responses.
- Mock data must not be used for acceptance testing.
- Frontend must gracefully handle backend unavailable/network errors.

### Frontend Assumes Backend Owns

- Authentication.
- Authorization/RBAC.
- Camera persistence.
- Import validation.
- Health/status values.
- Maintenance data.
- Report generation.
- Audit history.
- Export generation.
- Gap-analysis computation.

### Expected APIs

Confirm exact endpoints from the backend before implementation.

Likely API groups:

- Camera list.
- Camera detail.
- Camera create/update.
- Bulk import validation.
- Bulk import commit.
- Department metadata.
- Geography metadata.
- Camera health/status.
- Reports.
- Audit logs.
- Export.
- Current user/permissions.

Open question: exact REST paths, DTOs, auth mechanism, pagination format, filter contract, and error format are not available in this chat.

---

## 12. Performance Requirements

- Dashboard must load progressively.
- Map must remain responsive with the current seeded camera dataset.
- Use marker clustering or viewport-based rendering for dense data.
- Do not fetch full camera detail payloads for every marker unless backend only exposes that shape.
- Use pagination or virtualization for large registry lists.
- Cache stable reference data such as departments, camera types, and geography.
- Debounce search/filter inputs.
- Lazy-load map-heavy and report-heavy modules.
- Avoid re-rendering all markers on unrelated UI state changes.
- Handle LAN/backend latency gracefully.

Target goals:

- First usable dashboard state: under 3 seconds on normal LAN/dev network.
- Cached filter interaction feedback: under 500 ms.
- Map pan/zoom should remain smooth.
- Registry list should not freeze with seeded data.

Open question: expected production camera count and current seeded DB size must be confirmed.

---

## 13. Acceptance Criteria

### Live Backend Integration

- Frontend reads data from `http://localhost:5261`.
- Seeded database records appear in the UI.
- Core screens work without mock camera data.
- Backend unavailable state is handled clearly.
- API errors are visible and actionable.

### Dashboard / GIS

- User can view authorized cameras on a GIS map.
- User can filter by backend-supported department, camera type, status, and coverage fields.
- User can open camera details from a marker.
- Map remains usable with populated database records.

### Registry List

- User can search, sort, filter, and inspect authorized cameras.
- List data matches backend response.
- Export works if backend supports it.
- Empty/error/loading states are implemented.

### Manual Onboarding

- User can create a camera using backend-supported fields.
- Validation errors are shown inline.
- Successful creation updates the registry/map.
- Backend validation errors are displayed clearly.

### Bulk Import

- User can upload a supported file if endpoint exists.
- User can review validation results.
- User can confirm import.
- System shows imported/failed row counts.

### Reports

- User can view backend-provided gap-analysis and ageing-infrastructure reports.
- Report loading, empty, and error states are handled.
- Export is available if backend supports it.

### Audit

- User can view metadata audit history if endpoint exists.
- Audit entries are timestamped and attributable where permissions allow.

### RBAC

- User sees only permitted cameras, reports, actions, and exports.
- Restricted actions are hidden or disabled.
- Backend authorization failures are handled gracefully.

### Responsive UX

- Desktop, tablet, and mobile layouts are usable.
- Core map and registry workflows remain accessible on small screens.
- No critical action depends only on hover.

### Accessibility

- Core workflows can be completed using keyboard navigation.
- Form errors and statuses are screen-reader accessible.
- Status indicators do not rely only on color.

---

## 14. Assumptions Made

1. Backend is available on the same network at `http://localhost:5261`.
2. Database already contains enough seeded camera data to test dashboard, registry list, and detail flows.
3. Backend is the source of truth for validation, permissions, health, maintenance, reports, exports, and audit logs.
4. Frontend should not use mock data for real acceptance testing.
5. React is the expected frontend stack because the official Model 1 suggested stack includes React.js.
6. Model 1 does not include centralized live streaming or recording.

---

## 15. Open Questions

1. What frontend framework is currently used: Vite React, Next.js, or another setup?
2. What is the existing frontend folder structure?
3. What environment variable convention does the project use for API base URL?
4. What exact API endpoints exist on `http://localhost:5261`?
5. What are the actual camera list/detail DTOs?
6. What auth mechanism is used?
7. What RBAC permissions are exposed to frontend?
8. What fields are mandatory for manual camera onboarding?
9. What file formats are supported for bulk import?
10. Does backend return coverage geometry or raw heading/FOV/range values?
11. Are reports already implemented in backend?
12. Is audit history already implemented?
13. What map provider/tile source should be used?
14. What is the current seeded database size?
15. Is mobile onboarding required or only mobile viewing/searching?

---

## 16. Features Intentionally Excluded

The following are excluded from the current Model 1 frontend scope:

- Centralized live video streaming.
- Centralized video recording.
- Playback UI.
- ANPR.
- Face recognition.
- Vehicle tracking.
- Watchlist matching.
- Real-time alert dashboard.
- Multi-camera video wall.
- VMS federation workflows.
- AI analytics dashboards.
- External database integrations such as VAHAN, SARTHI, eGujCop, AFIS, and NAFIS.
- Video event search.
- Route reconstruction.