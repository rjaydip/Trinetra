import { request, requestBlob } from './client';
import type {
  CameraConnectionTestAccepted,
  CameraConnectionTestRequest,
  CameraConnectionTestResult,
  CameraCredentialTestAccepted,
  CameraCredentialTestResult,
  AccessGroupResponse,
  AgeingInfrastructureResponse,
  ActivateGroupRequest,
  AddScopeRequest,
  AiWorkerHealthResponse,
  ApiKeyCreatedResponse,
  ApiKeyResponse,
  AreaTypeResponse,
  BoundaryImportRequest,
  BoundaryImportResult,
  AssignGroupRequest,
  AuthResponse,
  BulkImportRequest,
  BulkImportResult,
  CameraHealthHistoryResponse,
  CameraHealthResponse,
  CameraListQuery,
  CameraPatchRequest,
  CameraPage,
  CameraResponse,
  CameraStatusHistoryResponse,
  CameraWriteRequest,
  CapabilityResponse,
  ChangePasswordRequest,
  ConnectionTestAccepted,
  ConnectionTestResult,
  ConnectorHealthResponse,
  ConnectorTargetRequest,
  CorrelationGroupDetail,
  CorrelationGroupSummary,
  CoverageSummaryResponse,
  CreateApiKeyRequest,
  CreateFromFederatedRequest,
  CreateGroupRequest,
  CreateUserRequest,
  CreateWatchlistEntryRequest,
  CredentialExistsResponse,
  CredentialRequest,
  CredentialResponse,
  CreatedResponse,
  DeactivateRequest,
  DetectionResponse,
  DetectionTagRequest,
  EventPage,
  FederatedCameraResponse,
  GapAnalysisResponse,
  GeoJsonFeature,
  GeoJsonFeatureCollection,
  GeographicAreaResponse,
  GeographicAreaRequest,
  GroupMemberResponse,
  HealthOverrideRequest,
  LoginRequest,
  MaintenanceCreateRequest,
  MaintenanceRecordResponse,
  MaintenanceUpdateRequest,
  MoveUnitRequest,
  MoveUnitResponse,
  OrganizationResponse,
  OrganizationRequest,
  OrganizationUnitResponse,
  OrganizationUnitRequest,
  OverviewResponse,
  PageResult,
  PermissionResponse,
  PollInventoryResponse,
  ReconcileRequest,
  ReconcileResponse,
  ResetPasswordRequest,
  RoleResponse,
  RoleWriteRequest,
  SavedCredentialRequest,
  SavedCredentialResponse,
  SavedCredentialUpdateRequest,
  StreamSessionResponse,
  TargetStateRequest,
  UnreconciledPage,
  UpdateGroupRequest,
  UpdateUserRequest,
  UserGroupResponse,
  UserResponse,
  VideoWallPreferences,
  VmsResponse,
  WatchlistAlertResponse,
  WatchlistEntryCreatedResponse,
  WatchlistEntryResponse,
} from './models';

type QueryValue = string | number | boolean | null | undefined;

function withQuery(path: string, query: object): string {
  const parameters = new URLSearchParams();
  Object.entries(query as Record<string, QueryValue>).forEach(([key, value]) => {
    if (value !== undefined && value !== null) parameters.set(key, String(value));
  });
  const queryString = parameters.toString();
  return queryString ? `${path}?${queryString}` : path;
}

function json(body: unknown): RequestInit {
  return { body: JSON.stringify(body), method: 'POST' };
}

/**
 * Walks every page of a `page`/`pageSize`-paginated endpoint and concatenates the results.
 *
 * Some lists (the organization/unit/area hierarchy) are consumed as a *complete* set — to build
 * a client-side tree, or to populate every "select a parent" dropdown — so truncating them to
 * one page would silently hide nodes. The unpaginated form of these same endpoints already caps
 * out at a hard limit (1000 rows) with no way to see past it; walking pages instead removes that
 * cap while keeping the "give me everything" contract these callers actually need.
 */
async function fetchAllPages<T>(
  fetchPage: (page: number, pageSize: number) => Promise<PageResult<T>>,
  pageSize = 200,
): Promise<T[]> {
  const first = await fetchPage(1, pageSize);
  const items = [...first.items];
  for (let page = 2; page <= first.totalPages; page += 1) {
    const next = await fetchPage(page, pageSize);
    items.push(...next.items);
  }
  return items;
}

export function normalizeAuthResponse(raw: {
  token?: string;
  accessToken?: string;
  expiresAt?: string;
  accessExpiresIn?: number | string;
  refreshToken?: string;
  refreshExpiresIn?: number | string;
  mustChangePassword?: boolean;
}): AuthResponse {
  const token = raw.accessToken ?? raw.token ?? '';
  const expiresAt = raw.expiresAt
    ?? (raw.accessExpiresIn ? new Date(Date.now() + Number(raw.accessExpiresIn) * 1000).toISOString() : new Date(Date.now() + 15 * 60 * 1000).toISOString());
  return {
    token,
    accessToken: raw.accessToken ?? token,
    expiresAt,
    accessExpiresIn: raw.accessExpiresIn ? Number(raw.accessExpiresIn) : undefined,
    refreshToken: raw.refreshToken,
    refreshExpiresIn: raw.refreshExpiresIn ? Number(raw.refreshExpiresIn) : undefined,
    mustChangePassword: Boolean(raw.mustChangePassword),
  };
}

export const api = {
  auth: {
    login: async (body: LoginRequest) => normalizeAuthResponse(await request<Record<string, unknown>>('/api/v1/auth/login', json(body))),
    changePassword: async (body: ChangePasswordRequest) => normalizeAuthResponse(await request<Record<string, unknown>>('/api/v1/auth/password', json(body))),
    refresh: async (refreshToken: string) => normalizeAuthResponse(await request<Record<string, unknown>>('/api/v1/auth/refresh', json({ refreshToken }))),
    logout: () => request<void>('/api/v1/auth/logout', { method: 'POST' }),
  },
  cameras: {
    list: (query: CameraListQuery, signal?: AbortSignal) => request<CameraPage>(withQuery('/api/v1/cameras', query), {}, signal),
    get: (id: string, signal?: AbortSignal) => request<CameraResponse>(`/api/v1/cameras/${id}`, {}, signal),
    create: (body: CameraWriteRequest) => request<CreatedResponse>('/api/v1/cameras', json(body)),
    replace: (id: string, body: CameraWriteRequest) => request<CameraResponse>(`/api/v1/cameras/${id}`, { body: JSON.stringify(body), method: 'PUT' }),
    update: (id: string, body: CameraPatchRequest) => request<CameraResponse>(`/api/v1/cameras/${id}`, { body: JSON.stringify(body), method: 'PATCH' }),
    retire: (id: string) => request<void>(`/api/v1/cameras/${id}`, { method: 'DELETE' }),
    bulkImport: (body: BulkImportRequest) => request<BulkImportResult>('/api/v1/cameras/bulk-import', json(body)),
    /** The registry's own total, unlike `api.overview()`'s `cameras` field — that one reflects
     * only VMS-federation-discovered inventory (`connector_target`/`federated_camera`), not every
     * manually-registered camera. This is what a "total cameras" figure should use. */
    count: (query: CameraListQuery = {}, signal?: AbortSignal) =>
      request<{ total: number }>(withQuery('/api/v1/cameras/count', query), {}, signal),
    ageingInfrastructure: (query: { organizationUnitId?: string; geographicAreaId?: string; oldestLimit?: number } = {}, signal?: AbortSignal) =>
      request<AgeingInfrastructureResponse>(withQuery('/api/v1/cameras/reports/ageing-infrastructure', query), {}, signal),
    health: (id: string, signal?: AbortSignal) => request<CameraHealthResponse>(`/api/v1/cameras/${id}/health`, {}, signal),
    healthHistory: (id: string, query: { from?: string; to?: string; limit?: number } = {}, signal?: AbortSignal) => request<CameraHealthHistoryResponse>(withQuery(`/api/v1/cameras/${id}/health/history`, query), {}, signal),
    overrideHealth: (id: string, body: HealthOverrideRequest) => request<CameraHealthResponse>(`/api/v1/cameras/${id}/health`, { body: JSON.stringify(body), method: 'PATCH' }),
    maintenance: (id: string, query: { status?: string; limit?: number } = {}, signal?: AbortSignal) => request<MaintenanceRecordResponse[]>(withQuery(`/api/v1/cameras/${id}/maintenance`, query), {}, signal),
    createMaintenance: (id: string, body: MaintenanceCreateRequest) => request<CreatedResponse>(`/api/v1/cameras/${id}/maintenance`, json(body)),
    updateMaintenance: (id: string, recordId: string, body: MaintenanceUpdateRequest) => request<MaintenanceRecordResponse>(`/api/v1/cameras/${id}/maintenance/${recordId}`, { body: JSON.stringify(body), method: 'PATCH' }),
    connectionTests: {
      create: (body: CameraConnectionTestRequest) => request<CameraConnectionTestAccepted>('/api/v1/cameras/connection-test', json(body)),
      get: (statusUrl: string, signal?: AbortSignal) => request<CameraConnectionTestResult>(statusUrl, {}, signal),
    },
    credentials: {
      status: (id: string, signal?: AbortSignal) => request<CredentialExistsResponse>(`/api/v1/cameras/${id}/credential/status`, {}, signal),
      save: (id: string, body: CredentialRequest) => request<CredentialResponse>(`/api/v1/cameras/${id}/credential`, { body: JSON.stringify(body), method: 'PUT' }),
    },
    credentialTests: {
      create: (cameraId: string) => request<CameraCredentialTestAccepted>(`/api/v1/cameras/${cameraId}/credential-test`, { method: 'POST' }),
      get: (statusUrl: string, signal?: AbortSignal) => request<CameraCredentialTestResult>(statusUrl, {}, signal),
    },
    /** Tests the credential already saved on a created camera (post-save, authenticating check —
     * contrast with `testConnection`, which is a pre-save reachability-only probe). Polls the
     * accepted test to a terminal state, same pattern as `testConnection`. */
    testCredential: async (cameraId: string): Promise<CameraCredentialTestResult> => {
      const accepted = await api.cameras.credentialTests.create(cameraId);
      const deadline = Date.now() + 15_000;
      let result = await api.cameras.credentialTests.get(accepted.statusUrl);
      while (result.status.toLowerCase() === 'pending' || result.status.toLowerCase() === 'running') {
        if (Date.now() > deadline) {
          return { ...result, status: 'timeout', failureReason: result.failureReason ?? 'The credential check timed out.' };
        }
        await new Promise((resolve) => setTimeout(resolve, 750));
        result = await api.cameras.credentialTests.get(accepted.statusUrl);
      }
      return result;
    },
    /** Pre-save reachability check for a standalone camera. Polls the accepted test to a terminal
     * state so callers can `await` a single answer; this never sees a credential (see
     * CameraForm's ConnectionCheck) — it only reports whether protocol/ipAddress/port answered. */
    testConnection: async (body: CameraConnectionTestRequest): Promise<{ reachable: boolean; detail?: string }> => {
      const accepted = await api.cameras.connectionTests.create(body);
      const deadline = Date.now() + 15_000;
      let result = await api.cameras.connectionTests.get(accepted.statusUrl);
      while (result.status.toLowerCase() === 'pending' || result.status.toLowerCase() === 'running') {
        if (Date.now() > deadline) return { reachable: false, detail: 'The connectivity check timed out.' };
        await new Promise((resolve) => setTimeout(resolve, 750));
        result = await api.cameras.connectionTests.get(accepted.statusUrl);
      }
      if (result.failureReason) return { reachable: false, detail: result.failureReason };
      if (result.result?.reachable) return { reachable: true };
      return { reachable: false, detail: result.result?.failure ?? undefined };
    },
  },
  gis: {
    cameras: (query: { bbox: string; includeSectors?: boolean; includeRetired?: boolean; organizationUnitId?: string; operationalStatus?: string; maintenanceStatus?: string }, signal?: AbortSignal) => request<GeoJsonFeatureCollection>(withQuery('/api/v1/gis/cameras', query), {}, signal),
    cameraCoverage: (id: string, signal?: AbortSignal) => request<GeoJsonFeature | undefined>(`/api/v1/cameras/${id}/coverage`, {}, signal),
    coverage: (query: { geographicAreaId?: string; bbox?: string; organizationUnitId?: string }, signal?: AbortSignal) => request<CoverageSummaryResponse>(withQuery('/api/v1/gis/coverage', query), {}, signal),
    gaps: (geographicAreaId: string, signal?: AbortSignal) => request<GapAnalysisResponse>(withQuery('/api/v1/gis/gaps', { geographicAreaId }), {}, signal),
  },
  reference: {
    organizations: (signal?: AbortSignal) => fetchAllPages((page, pageSize) =>
      request<PageResult<OrganizationResponse>>(withQuery('/api/v1/organizations', { page, pageSize }), {}, signal)),
    organizationUnits: (organizationId: string, signal?: AbortSignal) => fetchAllPages((page, pageSize) =>
      request<PageResult<OrganizationUnitResponse>>(withQuery(`/api/v1/organizations/${organizationId}/units`, { page, pageSize }), {}, signal)),
    organizationUnit: (id: string, signal?: AbortSignal) => request<OrganizationUnitResponse>(`/api/v1/organization-units/${id}`, {}, signal),
    geographicAreas: (query: { rootsOnly?: boolean; parentId?: string } = {}, signal?: AbortSignal) => fetchAllPages((page, pageSize) =>
      request<PageResult<GeographicAreaResponse>>(withQuery('/api/v1/geographic-areas', { ...query, page, pageSize }), {}, signal)),
    geographicArea: (id: string, signal?: AbortSignal) => request<GeographicAreaResponse>(`/api/v1/geographic-areas/${id}`, {}, signal),
  },
  vms: {
    list: (signal?: AbortSignal) => request<VmsResponse[]>('/api/v1/vms', {}, signal),
    listPage: (query: { page: number; pageSize?: number }, signal?: AbortSignal) =>
      request<PageResult<VmsResponse>>(withQuery('/api/v1/vms', query), {}, signal),
    create: (body: ConnectorTargetRequest) => request<CreatedResponse>('/api/v1/vms', json(body)),
    get: (id: string, signal?: AbortSignal) => request<VmsResponse>(`/api/v1/vms/${id}`, {}, signal),
    replace: (id: string, body: ConnectorTargetRequest) => request<void>(`/api/v1/vms/${id}`, { body: JSON.stringify(body), method: 'PUT' }),
    setState: (id: string, body: TargetStateRequest) => request<void>(`/api/v1/vms/${id}/state`, json(body)),
    pollInventory: (id: string) => request<PollInventoryResponse>(`/api/v1/vms/${id}/poll-inventory`, { method: 'POST' }),
    remove: (id: string) => request<void>(`/api/v1/vms/${id}`, { method: 'DELETE' }),
    health: (id: string, query: { limit?: number; days?: number } = {}, signal?: AbortSignal) => request<ConnectorHealthResponse[]>(withQuery(`/api/v1/vms/${id}/health`, query), {}, signal),
    capabilities: (id: string, signal?: AbortSignal) => request<CapabilityResponse>(`/api/v1/vms/${id}/capabilities`, {}, signal),
    cameraStatusHistory: (id: string, nativeCameraId: string, query: { from?: string; to?: string; limit?: number } = {}, signal?: AbortSignal) => request<CameraStatusHistoryResponse>(withQuery(`/api/v1/vms/${id}/cameras/${nativeCameraId}/status-history`, query), {}, signal),
    discoveredCameras: (id: string, signal?: AbortSignal) => request<FederatedCameraResponse[]>(`/api/v1/vms/${id}/cameras`, {}, signal),
  },
  reconciliation: {
    unreconciled: (query: { targetId?: string; cursor?: string; limit?: number } = {}, signal?: AbortSignal) => request<UnreconciledPage>(withQuery('/api/v1/cameras/unreconciled', query), {}, signal),
    reconcile: (cameraId: string, body: ReconcileRequest) => request<ReconcileResponse>(`/api/v1/cameras/${cameraId}/reconcile`, json(body)),
    createFromFederated: (body: CreateFromFederatedRequest) => request<CreatedResponse>('/api/v1/cameras/from-federated', json(body)),
  },
  detections: {
    search: (query: { plateNumber?: string; targetId?: string; from?: string; to?: string; limit?: number } = {}, signal?: AbortSignal) =>
      request<DetectionResponse[]>(withQuery('/api/v1/detections', query), {}, signal),
    addTag: (eventId: string, body: DetectionTagRequest) => request<void>(`/api/v1/detections/${eventId}/tags`, json(body)),
    removeTag: (eventId: string, tag: string) => request<void>(`/api/v1/detections/${eventId}/tags/${encodeURIComponent(tag)}`, { method: 'DELETE' }),
    /** The annotated evidence snapshot (`GET /{eventId}/evidence`) — a binary image, not JSON,
     * so it goes through `requestBlob` rather than the usual `request`. */
    evidence: (eventId: string, occurredAt: string, signal?: AbortSignal) =>
      requestBlob(withQuery(`/api/v1/detections/${eventId}/evidence`, { occurredAt }), signal),
  },
  events: {
    query: (query: { from: string; to: string; cameraId?: string; eventType?: string; objectReference?: string; cursor?: string; limit?: number }, signal?: AbortSignal) =>
      request<EventPage>(withQuery('/api/v1/events', query), {}, signal),
  },
  correlation: {
    /** Groups whose activity overlaps `from`/`to` (default: last 24h, max 7 days), newest
     * activity first. Unpaginated call returns the plain array (capped at 500). */
    groups: (query: { from?: string; to?: string } = {}, signal?: AbortSignal) =>
      request<CorrelationGroupSummary[]>(withQuery('/api/v1/correlation/groups', query), {}, signal),
    get: (id: string, signal?: AbortSignal) => request<CorrelationGroupDetail>(`/api/v1/correlation/groups/${id}`, {}, signal),
  },
  credentials: {
    status: (id: string, signal?: AbortSignal) => request<CredentialExistsResponse>(`/api/v1/vms/${id}/credential/status`, {}, signal),
    save: (id: string, body: CredentialRequest) => request<CredentialResponse>(`/api/v1/vms/${id}/credential`, { body: JSON.stringify(body), method: 'PUT' }),
  },
  connectionTests: {
    create: (vmsId: string) => request<ConnectionTestAccepted>(`/api/v1/vms/${vmsId}/test`, { method: 'POST' }),
    get: (statusUrl: string, signal?: AbortSignal) => request<ConnectionTestResult>(statusUrl, {}, signal),
  },
  /** The saved-credential library: reusable named credentials a camera can point at instead of
   * having its own username/password re-typed. Metadata only — never secret material. */
  credentialLibrary: {
    list: (signal?: AbortSignal) => request<SavedCredentialResponse[]>('/api/v1/credential-library', {}, signal),
    get: (id: string, signal?: AbortSignal) => request<SavedCredentialResponse>(`/api/v1/credential-library/${id}`, {}, signal),
    create: (body: SavedCredentialRequest) => request<SavedCredentialResponse>('/api/v1/credential-library', json(body)),
    update: (id: string, body: SavedCredentialUpdateRequest) => request<SavedCredentialResponse>(`/api/v1/credential-library/${id}`, { body: JSON.stringify(body), method: 'PUT' }),
    delete: (id: string) => request<void>(`/api/v1/credential-library/${id}`, { method: 'DELETE' }),
  },
  admin: {
    organizations: {
      list: (signal?: AbortSignal) => fetchAllPages((page, pageSize) =>
        request<PageResult<OrganizationResponse>>(withQuery('/api/v1/organizations', { page, pageSize }), {}, signal)),
      get: (id: string, signal?: AbortSignal) => request<OrganizationResponse>(`/api/v1/organizations/${id}`, {}, signal),
      create: (body: OrganizationRequest) => request<CreatedResponse>('/api/v1/organizations', json(body)),
      update: (id: string, body: OrganizationRequest) => request<OrganizationResponse>(`/api/v1/organizations/${id}`, { body: JSON.stringify(body), method: 'PUT' }),
      listUnits: (id: string, signal?: AbortSignal) => fetchAllPages((page, pageSize) =>
        request<PageResult<OrganizationUnitResponse>>(withQuery(`/api/v1/organizations/${id}/units`, { page, pageSize }), {}, signal)),
      createUnit: (id: string, body: OrganizationUnitRequest) => request<CreatedResponse>(`/api/v1/organizations/${id}/units`, json(body)),
      updateUnit: (id: string, body: OrganizationUnitRequest) => request<OrganizationUnitResponse>(`/api/v1/organization-units/${id}`, { body: JSON.stringify(body), method: 'PUT' }),
      activateUnit: (id: string) => request<void>(`/api/v1/organization-units/${id}/activate`, { method: 'POST' }),
      deactivateUnit: (id: string, body: DeactivateRequest = {}) => request<void>(`/api/v1/organization-units/${id}/deactivate`, json(body)),
      moveUnit: (id: string, body: MoveUnitRequest) => request<MoveUnitResponse>(`/api/v1/organization-units/${id}/move`, json(body)),
    },
    geography: {
      listAreas: (query: { rootsOnly?: boolean; parentId?: string } = {}, signal?: AbortSignal) => fetchAllPages((page, pageSize) =>
        request<PageResult<GeographicAreaResponse>>(withQuery('/api/v1/geographic-areas', { ...query, page, pageSize }), {}, signal)),
      getArea: (id: string, signal?: AbortSignal) => request<GeographicAreaResponse>(`/api/v1/geographic-areas/${id}`, {}, signal),
      listAreaChildren: (id: string, signal?: AbortSignal) => fetchAllPages((page, pageSize) =>
        request<PageResult<GeographicAreaResponse>>(withQuery(`/api/v1/geographic-areas/${id}/children`, { page, pageSize }), {}, signal)),
      listAreaAncestors: (id: string, signal?: AbortSignal) => request<GeographicAreaResponse[]>(`/api/v1/geographic-areas/${id}/ancestors`, {}, signal),
      listAreaTypes: (signal?: AbortSignal) => request<AreaTypeResponse[]>('/api/v1/geographic-areas/types', {}, signal),
      createArea: (body: GeographicAreaRequest) => request<CreatedResponse>('/api/v1/geographic-areas', json(body)),
      updateArea: (id: string, body: GeographicAreaRequest) => request<GeographicAreaResponse>(`/api/v1/geographic-areas/${id}`, { body: JSON.stringify(body), method: 'PUT' }),
      activateArea: (id: string) => request<void>(`/api/v1/geographic-areas/${id}/activate`, { method: 'POST' }),
      deactivateArea: (id: string, body: DeactivateRequest = {}) => request<void>(`/api/v1/geographic-areas/${id}/deactivate`, json(body)),
      /** Attaches a surveyed boundary `Polygon` to each area (1-200 rows), replacing any prior
       * one — what `GET /gis/gaps` needs before it can analyze an area. `geography.manage`. */
      bulkImportBoundaries: (body: BoundaryImportRequest) => request<BoundaryImportResult>('/api/v1/geographic-areas/bulk-import-boundaries', json(body)),
    },
    groups: {
      list: (signal?: AbortSignal) => request<AccessGroupResponse[]>('/api/v1/access-groups', {}, signal),
      listPage: (query: { page: number; pageSize?: number }, signal?: AbortSignal) =>
        request<PageResult<AccessGroupResponse>>(withQuery('/api/v1/access-groups', query), {}, signal),
      get: (id: string, signal?: AbortSignal) => request<AccessGroupResponse>(`/api/v1/access-groups/${id}`, {}, signal),
      members: (id: string, signal?: AbortSignal) => request<GroupMemberResponse[]>(`/api/v1/access-groups/${id}/members`, {}, signal),
      membersPage: (id: string, query: { page: number; pageSize?: number }, signal?: AbortSignal) =>
        request<PageResult<GroupMemberResponse>>(withQuery(`/api/v1/access-groups/${id}/members`, query), {}, signal),
      create: (body: CreateGroupRequest) => request<CreatedResponse>('/api/v1/access-groups', json(body)),
      update: (id: string, body: UpdateGroupRequest) => request<AccessGroupResponse>(`/api/v1/access-groups/${id}`, { body: JSON.stringify(body), method: 'PUT' }),
      activate: (id: string, body?: ActivateGroupRequest) => request<void>(`/api/v1/access-groups/${id}/activate`, json(body ?? {})),
      disable: (id: string) => request<void>(`/api/v1/access-groups/${id}/disable`, { method: 'POST' }),
      addScope: (id: string, body: AddScopeRequest) => request<CreatedResponse>(`/api/v1/access-groups/${id}/scopes`, json(body)),
      removeScope: (id: string, scopeId: string) => request<void>(`/api/v1/access-groups/${id}/scopes/${scopeId}`, { method: 'DELETE' }),
    },
    roles: {
      list: (includeInactive = true, signal?: AbortSignal) => request<RoleResponse[]>(withQuery('/api/v1/roles', { includeInactive }), {}, signal),
      get: (id: string, signal?: AbortSignal) => request<RoleResponse>(`/api/v1/roles/${id}`, {}, signal),
      create: (body: RoleWriteRequest) => request<CreatedResponse>('/api/v1/roles', json(body)),
      update: (id: string, body: RoleWriteRequest) => request<RoleResponse>(`/api/v1/roles/${id}`, { body: JSON.stringify(body), method: 'PUT' }),
      delete: (id: string) => request<void>(`/api/v1/roles/${id}`, { method: 'DELETE' }),
      permissions: (signal?: AbortSignal) => request<PermissionResponse[]>('/api/v1/permissions', {}, signal),
    },
    watchlist: {
      list: (signal?: AbortSignal) => request<WatchlistEntryResponse[]>('/api/v1/watchlist', {}, signal),
      listPage: (query: { active?: boolean; page: number; pageSize?: number }, signal?: AbortSignal) =>
        request<PageResult<WatchlistEntryResponse>>(withQuery('/api/v1/watchlist', query), {}, signal),
      create: (body: CreateWatchlistEntryRequest) => request<WatchlistEntryCreatedResponse>('/api/v1/watchlist', json(body)),
      deactivate: (id: string) => request<void>(`/api/v1/watchlist/${id}`, { method: 'DELETE' }),
      listAlerts: (query: { limit?: number } = {}, signal?: AbortSignal) => request<WatchlistAlertResponse[]>(withQuery('/api/v1/watchlist/alerts', query), {}, signal),
      listAlertsPage: (query: {
        acknowledged?: boolean; plate?: string; entryId?: string; severity?: string;
        from?: string; to?: string; page: number; pageSize?: number;
      }, signal?: AbortSignal) => request<PageResult<WatchlistAlertResponse>>(withQuery('/api/v1/watchlist/alerts', query), {}, signal),
      acknowledgeAlert: (id: string) => request<void>(`/api/v1/watchlist/alerts/${id}/acknowledge`, { method: 'POST' }),
    },
    apiKeys: {
      list: (signal?: AbortSignal) => request<ApiKeyResponse[]>('/api/v1/api-keys', {}, signal),
      listPage: (query: { page: number; pageSize?: number }, signal?: AbortSignal) =>
        request<PageResult<ApiKeyResponse>>(withQuery('/api/v1/api-keys', query), {}, signal),
      create: (body: CreateApiKeyRequest) => request<ApiKeyCreatedResponse>('/api/v1/api-keys', json(body)),
      revoke: (id: string) => request<void>(`/api/v1/api-keys/${id}`, { method: 'DELETE' }),
    },
    workerHealth: {
      listPage: (query: { page: number; pageSize?: number }, signal?: AbortSignal) =>
        request<PageResult<AiWorkerHealthResponse>>(withQuery('/api/v1/worker-health', query), {}, signal),
      retire: (id: string) => request<void>(`/api/v1/worker-health/${id}`, { method: 'DELETE' }),
    },
    users: {
      listPage: (query: { page: number; pageSize?: number }, signal?: AbortSignal) =>
        request<PageResult<UserResponse>>(withQuery('/api/v1/users', query), {}, signal),
      get: (id: string, signal?: AbortSignal) => request<UserResponse>(`/api/v1/users/${id}`, {}, signal),
      groups: (id: string, signal?: AbortSignal) => request<UserGroupResponse[]>(`/api/v1/users/${id}/groups`, {}, signal),
      permissions: (id: string, signal?: AbortSignal) => request<string[]>(`/api/v1/users/${id}/permissions`, {}, signal),
      create: (body: CreateUserRequest) => request<CreatedResponse>('/api/v1/users', json(body)),
      update: (id: string, body: UpdateUserRequest) => request<void>(`/api/v1/users/${id}`, { body: JSON.stringify(body), method: 'PUT' }),
      resetPassword: (id: string, body: ResetPasswordRequest) => request<void>(`/api/v1/users/${id}/password`, { body: JSON.stringify(body), method: 'POST' }),
      addToGroup: (id: string, body: AssignGroupRequest) => request<void>(`/api/v1/users/${id}/groups`, json(body)),
      removeFromGroup: (id: string, groupId: string) => request<void>(`/api/v1/users/${id}/groups/${groupId}`, { method: 'DELETE' }),
    },
  },
  videoWall: {
    /** 404 means the caller has never saved a layout — treated as "nothing saved" by the caller,
     * not surfaced as an error. */
    get: (signal?: AbortSignal) => request<VideoWallPreferences>('/api/v1/video-wall/preferences', {}, signal),
    save: (body: VideoWallPreferences) => request<VideoWallPreferences>('/api/v1/video-wall/preferences', { body: JSON.stringify(body), method: 'PUT' }),
    clear: () => request<void>('/api/v1/video-wall/preferences', { method: 'DELETE' }),
  },
  streams: {
    /** Mints a short-lived, single-camera HLS session token. 404 means the caller lacks
     * `camera.read` for this camera or it doesn't exist — treated as "no live feed", not an error. */
    session: (cameraId: string, signal?: AbortSignal) => request<StreamSessionResponse>(`/api/v1/streams/${cameraId}/session`, {}, signal),
    /** Relays a WHEP SDP offer to a WEBRTC-mode camera's own native endpoint
     * (`StreamSessionEndpoints.WhepOfferAsync`) and returns the device's SDP answer plus the
     * relayed session-teardown path (this API's own, already rewritten from the device's
     * `Location` — pass it straight to `whepTeardown`).
     *
     * Deliberately bypasses `request()`: this isn't the platform login session, it's the
     * short-lived, single-camera stream-session token from `session()` above, sent as its own
     * `Authorization: Bearer`, and the body/response are raw SDP text, not JSON — none of which
     * `request()`'s JSON-in/JSON-out, platform-bearer-token contract fits. A relative path (not
     * `apiBaseUrl()`) for the same reason `LiveVideoTile`'s HLS requests are relative — same-origin
     * keeps this on the path the streaming gateway's cookie/session handling assumes. */
    whepOffer: async (cameraId: string, streamToken: string, offerSdp: string, signal?: AbortSignal) => {
      const response = await fetch(`/api/v1/streams/${cameraId}/whep`, {
        method: 'POST',
        headers: { 'content-type': 'application/sdp', authorization: `Bearer ${streamToken}` },
        body: offerSdp,
        signal,
      });
      if (!response.ok) {
        throw new Error(`WHEP offer failed with status ${response.status}`);
      }
      return { answerSdp: await response.text(), teardownPath: response.headers.get('location') };
    },
    /** Best-effort teardown of a session `whepOffer` started — see
     * `StreamSessionEndpoints.WhepTeardownAsync`'s own remarks on why a failure here is not
     * escalated: the device closing the peer connection on its own idle timeout is an accepted
     * outcome. */
    whepTeardown: (teardownPath: string, streamToken: string) =>
      fetch(teardownPath, { method: 'DELETE', headers: { authorization: `Bearer ${streamToken}` } }).catch(() => undefined),
  },
  overview: (signal?: AbortSignal) => request<OverviewResponse>('/api/v1/overview', {}, signal),
};
