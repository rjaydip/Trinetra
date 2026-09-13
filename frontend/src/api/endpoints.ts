import { request } from './client';
import type {
  AccessGroupResponse,
  ActivateGroupRequest,
  AddScopeRequest,
  AiWorkerHealthResponse,
  ApiKeyCreatedResponse,
  ApiKeyResponse,
  AreaTypeResponse,
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
  EventPage,
  FederatedCameraResponse,
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
  ReconcileRequest,
  ReconcileResponse,
  ResetPasswordRequest,
  RoleResponse,
  RoleWriteRequest,
  TargetStateRequest,
  UnreconciledPage,
  UpdateGroupRequest,
  UpdateUserRequest,
  UserGroupResponse,
  UserResponse,
  VmsResponse,
  WatchlistAlertResponse,
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
    list: (query: CameraListQuery) => request<CameraPage>(withQuery('/api/v1/cameras', query)),
    get: (id: string) => request<CameraResponse>(`/api/v1/cameras/${id}`),
    create: (body: CameraWriteRequest) => request<CreatedResponse>('/api/v1/cameras', json(body)),
    replace: (id: string, body: CameraWriteRequest) => request<CameraResponse>(`/api/v1/cameras/${id}`, { body: JSON.stringify(body), method: 'PUT' }),
    update: (id: string, body: CameraPatchRequest) => request<CameraResponse>(`/api/v1/cameras/${id}`, { body: JSON.stringify(body), method: 'PATCH' }),
    retire: (id: string) => request<void>(`/api/v1/cameras/${id}`, { method: 'DELETE' }),
    bulkImport: (body: BulkImportRequest) => request<BulkImportResult>('/api/v1/cameras/bulk-import', json(body)),
    health: (id: string) => request<CameraHealthResponse>(`/api/v1/cameras/${id}/health`),
    healthHistory: (id: string, query: { from?: string; to?: string; limit?: number } = {}) => request<CameraHealthHistoryResponse>(withQuery(`/api/v1/cameras/${id}/health/history`, query)),
    overrideHealth: (id: string, body: HealthOverrideRequest) => request<CameraHealthResponse>(`/api/v1/cameras/${id}/health`, { body: JSON.stringify(body), method: 'PATCH' }),
    maintenance: (id: string, query: { status?: string; limit?: number } = {}) => request<MaintenanceRecordResponse[]>(withQuery(`/api/v1/cameras/${id}/maintenance`, query)),
    createMaintenance: (id: string, body: MaintenanceCreateRequest) => request<CreatedResponse>(`/api/v1/cameras/${id}/maintenance`, json(body)),
    updateMaintenance: (id: string, recordId: string, body: MaintenanceUpdateRequest) => request<MaintenanceRecordResponse>(`/api/v1/cameras/${id}/maintenance/${recordId}`, { body: JSON.stringify(body), method: 'PATCH' }),
  },
  gis: {
    cameras: (query: { bbox: string; includeSectors?: boolean; includeRetired?: boolean; organizationUnitId?: string; operationalStatus?: string; maintenanceStatus?: string }) => request<GeoJsonFeatureCollection>(withQuery('/api/v1/gis/cameras', query)),
    cameraCoverage: (id: string) => request<GeoJsonFeature | undefined>(`/api/v1/cameras/${id}/coverage`),
    coverage: (query: { geographicAreaId?: string; bbox?: string; organizationUnitId?: string }) => request<CoverageSummaryResponse>(withQuery('/api/v1/gis/coverage', query)),
  },
  reference: {
    organizations: () => fetchAllPages((page, pageSize) =>
      request<PageResult<OrganizationResponse>>(withQuery('/api/v1/organizations', { page, pageSize }))),
    organizationUnits: (organizationId: string) => fetchAllPages((page, pageSize) =>
      request<PageResult<OrganizationUnitResponse>>(withQuery(`/api/v1/organizations/${organizationId}/units`, { page, pageSize }))),
    geographicAreas: (query: { rootsOnly?: boolean; parentId?: string } = {}) => fetchAllPages((page, pageSize) =>
      request<PageResult<GeographicAreaResponse>>(withQuery('/api/v1/geographic-areas', { ...query, page, pageSize }))),
  },
  vms: {
    list: () => request<VmsResponse[]>('/api/v1/vms'),
    listPage: (query: { page: number; pageSize?: number }) =>
      request<PageResult<VmsResponse>>(withQuery('/api/v1/vms', query)),
    create: (body: ConnectorTargetRequest) => request<CreatedResponse>('/api/v1/vms', json(body)),
    get: (id: string) => request<VmsResponse>(`/api/v1/vms/${id}`),
    replace: (id: string, body: ConnectorTargetRequest) => request<void>(`/api/v1/vms/${id}`, { body: JSON.stringify(body), method: 'PUT' }),
    setState: (id: string, body: TargetStateRequest) => request<void>(`/api/v1/vms/${id}/state`, json(body)),
    remove: (id: string) => request<void>(`/api/v1/vms/${id}`, { method: 'DELETE' }),
    health: (id: string, query: { limit?: number; days?: number } = {}) => request<ConnectorHealthResponse[]>(withQuery(`/api/v1/vms/${id}/health`, query)),
    capabilities: (id: string) => request<CapabilityResponse>(`/api/v1/vms/${id}/capabilities`),
    cameraStatusHistory: (id: string, nativeCameraId: string, query: { from?: string; to?: string; limit?: number } = {}) => request<CameraStatusHistoryResponse>(withQuery(`/api/v1/vms/${id}/cameras/${nativeCameraId}/status-history`, query)),
    discoveredCameras: (id: string) => request<FederatedCameraResponse[]>(`/api/v1/vms/${id}/cameras`),
  },
  reconciliation: {
    unreconciled: (query: { targetId?: string; cursor?: string; limit?: number } = {}) => request<UnreconciledPage>(withQuery('/api/v1/cameras/unreconciled', query)),
    reconcile: (cameraId: string, body: ReconcileRequest) => request<ReconcileResponse>(`/api/v1/cameras/${cameraId}/reconcile`, json(body)),
    createFromFederated: (body: CreateFromFederatedRequest) => request<CreatedResponse>('/api/v1/cameras/from-federated', json(body)),
  },
  detections: {
    search: (query: { plateNumber?: string; targetId?: string; from?: string; to?: string; limit?: number } = {}) =>
      request<DetectionResponse[]>(withQuery('/api/v1/detections', query)),
  },
  events: {
    query: (query: { from: string; to: string; cameraId?: string; eventType?: string; objectReference?: string; cursor?: string; limit?: number }) =>
      request<EventPage>(withQuery('/api/v1/events', query)),
  },
  credentials: {
    status: (id: string) => request<CredentialExistsResponse>(`/api/v1/vms/${id}/credential/status`),
    save: (id: string, body: CredentialRequest) => request<CredentialResponse>(`/api/v1/vms/${id}/credential`, { body: JSON.stringify(body), method: 'PUT' }),
  },
  connectionTests: {
    create: (vmsId: string) => request<ConnectionTestAccepted>(`/api/v1/vms/${vmsId}/test`, { method: 'POST' }),
    get: (statusUrl: string) => request<ConnectionTestResult>(statusUrl),
  },
  admin: {
    organizations: {
      list: () => fetchAllPages((page, pageSize) =>
        request<PageResult<OrganizationResponse>>(withQuery('/api/v1/organizations', { page, pageSize }))),
      get: (id: string) => request<OrganizationResponse>(`/api/v1/organizations/${id}`),
      create: (body: OrganizationRequest) => request<CreatedResponse>('/api/v1/organizations', json(body)),
      update: (id: string, body: OrganizationRequest) => request<OrganizationResponse>(`/api/v1/organizations/${id}`, { body: JSON.stringify(body), method: 'PUT' }),
      listUnits: (id: string) => fetchAllPages((page, pageSize) =>
        request<PageResult<OrganizationUnitResponse>>(withQuery(`/api/v1/organizations/${id}/units`, { page, pageSize }))),
      createUnit: (id: string, body: OrganizationUnitRequest) => request<CreatedResponse>(`/api/v1/organizations/${id}/units`, json(body)),
      updateUnit: (id: string, body: OrganizationUnitRequest) => request<OrganizationUnitResponse>(`/api/v1/organization-units/${id}`, { body: JSON.stringify(body), method: 'PUT' }),
      activateUnit: (id: string) => request<void>(`/api/v1/organization-units/${id}/activate`, { method: 'POST' }),
      deactivateUnit: (id: string, body: DeactivateRequest = {}) => request<void>(`/api/v1/organization-units/${id}/deactivate`, json(body)),
      moveUnit: (id: string, body: MoveUnitRequest) => request<MoveUnitResponse>(`/api/v1/organization-units/${id}/move`, json(body)),
    },
    geography: {
      listAreas: (query: { rootsOnly?: boolean; parentId?: string } = {}) => fetchAllPages((page, pageSize) =>
        request<PageResult<GeographicAreaResponse>>(withQuery('/api/v1/geographic-areas', { ...query, page, pageSize }))),
      getArea: (id: string) => request<GeographicAreaResponse>(`/api/v1/geographic-areas/${id}`),
      listAreaChildren: (id: string) => fetchAllPages((page, pageSize) =>
        request<PageResult<GeographicAreaResponse>>(withQuery(`/api/v1/geographic-areas/${id}/children`, { page, pageSize }))),
      listAreaAncestors: (id: string) => request<GeographicAreaResponse[]>(`/api/v1/geographic-areas/${id}/ancestors`),
      listAreaTypes: () => request<AreaTypeResponse[]>('/api/v1/geographic-areas/types'),
      createArea: (body: GeographicAreaRequest) => request<CreatedResponse>('/api/v1/geographic-areas', json(body)),
      updateArea: (id: string, body: GeographicAreaRequest) => request<GeographicAreaResponse>(`/api/v1/geographic-areas/${id}`, { body: JSON.stringify(body), method: 'PUT' }),
      activateArea: (id: string) => request<void>(`/api/v1/geographic-areas/${id}/activate`, { method: 'POST' }),
      deactivateArea: (id: string, body: DeactivateRequest = {}) => request<void>(`/api/v1/geographic-areas/${id}/deactivate`, json(body)),
    },
    groups: {
      list: () => request<AccessGroupResponse[]>('/api/v1/access-groups'),
      listPage: (query: { page: number; pageSize?: number }) =>
        request<PageResult<AccessGroupResponse>>(withQuery('/api/v1/access-groups', query)),
      get: (id: string) => request<AccessGroupResponse>(`/api/v1/access-groups/${id}`),
      members: (id: string) => request<GroupMemberResponse[]>(`/api/v1/access-groups/${id}/members`),
      membersPage: (id: string, query: { page: number; pageSize?: number }) =>
        request<PageResult<GroupMemberResponse>>(withQuery(`/api/v1/access-groups/${id}/members`, query)),
      create: (body: CreateGroupRequest) => request<CreatedResponse>('/api/v1/access-groups', json(body)),
      update: (id: string, body: UpdateGroupRequest) => request<AccessGroupResponse>(`/api/v1/access-groups/${id}`, { body: JSON.stringify(body), method: 'PUT' }),
      activate: (id: string, body?: ActivateGroupRequest) => request<void>(`/api/v1/access-groups/${id}/activate`, json(body ?? {})),
      disable: (id: string) => request<void>(`/api/v1/access-groups/${id}/disable`, { method: 'POST' }),
      addScope: (id: string, body: AddScopeRequest) => request<CreatedResponse>(`/api/v1/access-groups/${id}/scopes`, json(body)),
      removeScope: (id: string, scopeId: string) => request<void>(`/api/v1/access-groups/${id}/scopes/${scopeId}`, { method: 'DELETE' }),
    },
    roles: {
      list: (includeInactive = true) => request<RoleResponse[]>(withQuery('/api/v1/roles', { includeInactive })),
      get: (id: string) => request<RoleResponse>(`/api/v1/roles/${id}`),
      create: (body: RoleWriteRequest) => request<CreatedResponse>('/api/v1/roles', json(body)),
      update: (id: string, body: RoleWriteRequest) => request<RoleResponse>(`/api/v1/roles/${id}`, { body: JSON.stringify(body), method: 'PUT' }),
      delete: (id: string) => request<void>(`/api/v1/roles/${id}`, { method: 'DELETE' }),
      permissions: () => request<PermissionResponse[]>('/api/v1/permissions'),
    },
    watchlist: {
      list: () => request<WatchlistEntryResponse[]>('/api/v1/watchlist'),
      listPage: (query: { active?: boolean; page: number; pageSize?: number }) =>
        request<PageResult<WatchlistEntryResponse>>(withQuery('/api/v1/watchlist', query)),
      create: (body: CreateWatchlistEntryRequest) => request<CreatedResponse>('/api/v1/watchlist', json(body)),
      deactivate: (id: string) => request<void>(`/api/v1/watchlist/${id}`, { method: 'DELETE' }),
      listAlerts: (query: { limit?: number } = {}) => request<WatchlistAlertResponse[]>(withQuery('/api/v1/watchlist/alerts', query)),
      listAlertsPage: (query: {
        acknowledged?: boolean; plate?: string; entryId?: string; severity?: string;
        from?: string; to?: string; page: number; pageSize?: number;
      }) => request<PageResult<WatchlistAlertResponse>>(withQuery('/api/v1/watchlist/alerts', query)),
      acknowledgeAlert: (id: string) => request<void>(`/api/v1/watchlist/alerts/${id}/acknowledge`, { method: 'POST' }),
    },
    apiKeys: {
      list: () => request<ApiKeyResponse[]>('/api/v1/api-keys'),
      listPage: (query: { page: number; pageSize?: number }) =>
        request<PageResult<ApiKeyResponse>>(withQuery('/api/v1/api-keys', query)),
      create: (body: CreateApiKeyRequest) => request<ApiKeyCreatedResponse>('/api/v1/api-keys', json(body)),
      revoke: (id: string) => request<void>(`/api/v1/api-keys/${id}`, { method: 'DELETE' }),
    },
    workerHealth: {
      listPage: (query: { page: number; pageSize?: number }) =>
        request<PageResult<AiWorkerHealthResponse>>(withQuery('/api/v1/worker-health', query)),
      retire: (id: string) => request<void>(`/api/v1/worker-health/${id}`, { method: 'DELETE' }),
    },
    users: {
      listPage: (query: { page: number; pageSize?: number }) =>
        request<PageResult<UserResponse>>(withQuery('/api/v1/users', query)),
      get: (id: string) => request<UserResponse>(`/api/v1/users/${id}`),
      groups: (id: string) => request<UserGroupResponse[]>(`/api/v1/users/${id}/groups`),
      permissions: (id: string) => request<string[]>(`/api/v1/users/${id}/permissions`),
      create: (body: CreateUserRequest) => request<CreatedResponse>('/api/v1/users', json(body)),
      update: (id: string, body: UpdateUserRequest) => request<void>(`/api/v1/users/${id}`, { body: JSON.stringify(body), method: 'PUT' }),
      resetPassword: (id: string, body: ResetPasswordRequest) => request<void>(`/api/v1/users/${id}/password`, { body: JSON.stringify(body), method: 'POST' }),
      addToGroup: (id: string, body: AssignGroupRequest) => request<void>(`/api/v1/users/${id}/groups`, json(body)),
      removeFromGroup: (id: string, groupId: string) => request<void>(`/api/v1/users/${id}/groups/${groupId}`, { method: 'DELETE' }),
    },
  },
  overview: () => request<OverviewResponse>('/api/v1/overview'),
};
