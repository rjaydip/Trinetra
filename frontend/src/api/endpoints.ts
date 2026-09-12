import { request } from './client';
import type {
  AccessGroupResponse,
  ActivateGroupRequest,
  AddScopeRequest,
  ApiKeyCreatedResponse,
  ApiKeyResponse,
  AreaTypeResponse,
  AuthResponse,
  BulkImportRequest,
  BulkImportResult,
  CameraHealthHistoryResponse,
  CameraHealthResponse,
  CameraListQuery,
  CameraPatchRequest,
  CameraPage,
  CameraResponse,
  CameraWriteRequest,
  ChangePasswordRequest,
  ConnectionTestAccepted,
  ConnectionTestResult,
  ConnectorTargetRequest,
  CoverageSummaryResponse,
  CreateApiKeyRequest,
  CreateGroupRequest,
  CreateWatchlistEntryRequest,
  CredentialExistsResponse,
  CredentialRequest,
  CredentialResponse,
  CreatedResponse,
  DeactivateRequest,
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
  OrganizationResponse,
  OrganizationRequest,
  OrganizationUnitResponse,
  OrganizationUnitRequest,
  OverviewResponse,
  PermissionResponse,
  RoleResponse,
  RoleWriteRequest,
  UpdateGroupRequest,
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
    organizations: () => request<OrganizationResponse[]>('/api/v1/organizations'),
    organizationUnits: (organizationId: string) => request<OrganizationUnitResponse[]>(`/api/v1/organizations/${organizationId}/units`),
    geographicAreas: (query: { rootsOnly?: boolean; parentId?: string } = {}) => request<GeographicAreaResponse[]>(withQuery('/api/v1/geographic-areas', query)),
  },
  vms: {
    list: () => request<VmsResponse[]>('/api/v1/vms'),
    create: (body: ConnectorTargetRequest) => request<CreatedResponse>('/api/v1/vms', json(body)),
    get: (id: string) => request<VmsResponse>(`/api/v1/vms/${id}`),
    discoveredCameras: (id: string) => request<FederatedCameraResponse[]>(`/api/v1/vms/${id}/cameras`),
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
      list: () => request<OrganizationResponse[]>('/api/v1/organizations'),
      get: (id: string) => request<OrganizationResponse>(`/api/v1/organizations/${id}`),
      create: (body: OrganizationRequest) => request<CreatedResponse>('/api/v1/organizations', json(body)),
      update: (id: string, body: OrganizationRequest) => request<OrganizationResponse>(`/api/v1/organizations/${id}`, { body: JSON.stringify(body), method: 'PUT' }),
      listUnits: (id: string) => request<OrganizationUnitResponse[]>(`/api/v1/organizations/${id}/units`),
      createUnit: (id: string, body: OrganizationUnitRequest) => request<CreatedResponse>(`/api/v1/organizations/${id}/units`, json(body)),
      updateUnit: (id: string, body: OrganizationUnitRequest) => request<OrganizationUnitResponse>(`/api/v1/organization-units/${id}`, { body: JSON.stringify(body), method: 'PUT' }),
      activateUnit: (id: string) => request<void>(`/api/v1/organization-units/${id}/activate`, { method: 'POST' }),
      deactivateUnit: (id: string, body: DeactivateRequest = {}) => request<void>(`/api/v1/organization-units/${id}/deactivate`, json(body)),
    },
    geography: {
      listAreas: (query: { rootsOnly?: boolean; parentId?: string } = {}) => request<GeographicAreaResponse[]>(withQuery('/api/v1/geographic-areas', query)),
      getArea: (id: string) => request<GeographicAreaResponse>(`/api/v1/geographic-areas/${id}`),
      listAreaChildren: (id: string) => request<GeographicAreaResponse[]>(`/api/v1/geographic-areas/${id}/children`),
      listAreaAncestors: (id: string) => request<GeographicAreaResponse[]>(`/api/v1/geographic-areas/${id}/ancestors`),
      listAreaTypes: () => request<AreaTypeResponse[]>('/api/v1/geographic-areas/types'),
      createArea: (body: GeographicAreaRequest) => request<CreatedResponse>('/api/v1/geographic-areas', json(body)),
      updateArea: (id: string, body: GeographicAreaRequest) => request<GeographicAreaResponse>(`/api/v1/geographic-areas/${id}`, { body: JSON.stringify(body), method: 'PUT' }),
      activateArea: (id: string) => request<void>(`/api/v1/geographic-areas/${id}/activate`, { method: 'POST' }),
      deactivateArea: (id: string, body: DeactivateRequest = {}) => request<void>(`/api/v1/geographic-areas/${id}/deactivate`, json(body)),
    },
    groups: {
      list: () => request<AccessGroupResponse[]>('/api/v1/access-groups'),
      get: (id: string) => request<AccessGroupResponse>(`/api/v1/access-groups/${id}`),
      members: (id: string) => request<GroupMemberResponse[]>(`/api/v1/access-groups/${id}/members`),
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
      create: (body: CreateWatchlistEntryRequest) => request<CreatedResponse>('/api/v1/watchlist', json(body)),
      deactivate: (id: string) => request<void>(`/api/v1/watchlist/${id}`, { method: 'DELETE' }),
      listAlerts: (query: { limit?: number } = {}) => request<WatchlistAlertResponse[]>(withQuery('/api/v1/watchlist/alerts', query)),
      acknowledgeAlert: (id: string) => request<void>(`/api/v1/watchlist/alerts/${id}/acknowledge`, { method: 'POST' }),
    },
    apiKeys: {
      list: () => request<ApiKeyResponse[]>('/api/v1/api-keys'),
      create: (body: CreateApiKeyRequest) => request<ApiKeyCreatedResponse>('/api/v1/api-keys', json(body)),
      revoke: (id: string) => request<void>(`/api/v1/api-keys/${id}`, { method: 'DELETE' }),
    },
  },
  overview: () => request<OverviewResponse>('/api/v1/overview'),
};
