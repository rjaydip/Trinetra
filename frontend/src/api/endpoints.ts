import { request } from './client';
import type {
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
  CredentialExistsResponse,
  CredentialRequest,
  CredentialResponse,
  CreatedResponse,
  FederatedCameraResponse,
  GeoJsonFeature,
  GeoJsonFeatureCollection,
  GeographicAreaResponse,
  HealthOverrideRequest,
  LoginRequest,
  MaintenanceCreateRequest,
  MaintenanceRecordResponse,
  MaintenanceUpdateRequest,
  OrganizationResponse,
  OrganizationUnitResponse,
  OverviewResponse,
  SiteResponse,
  VmsResponse,
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

export const api = {
  auth: {
    login: (body: LoginRequest) => request<AuthResponse>('/api/v1/auth/login', json(body)),
    changePassword: (body: ChangePasswordRequest) => request<AuthResponse>('/api/v1/auth/password', json(body)),
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
    sites: (areaId?: string) => request<SiteResponse[]>(withQuery('/api/v1/sites', { areaId })),
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
    get: (vmsId: string, testId: string) => request<ConnectionTestResult>(`/api/v1/vms/${vmsId}/test/${testId}`),
  },
  overview: () => request<OverviewResponse>('/api/v1/overview'),
};
