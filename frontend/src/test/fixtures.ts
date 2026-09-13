import type { AuthResponse, CameraResponse, GeographicAreaResponse, OrganizationResponse, PageResult, VmsResponse } from '../api/models';

/** Wraps a fixture list as a single-page `PageResult` envelope, for mocking a paginated endpoint. */
export function pageEnvelope<T>(items: T[]): PageResult<T> {
  return { items, page: 1, pageSize: Math.max(items.length, 1), total: items.length, totalPages: 1 };
}

/** Test fixtures only: production always uses backend responses. */
export function sessionFixture(subject = 'reviewer', permissions: string[] | string = []): AuthResponse {
  return {
    token: `eyJhbGciOiJIUzI1NiJ9.${btoa(JSON.stringify({ sub: subject, jti: `session-${subject}`, 'trinetra:perm': permissions })).replaceAll('+', '-').replaceAll('/', '_').replaceAll('=', '')}.test-signature`,
    expiresAt: new Date(Date.now() + 60_000).toISOString(),
    mustChangePassword: false,
  };
}

export function cameraFixture(overrides: Partial<CameraResponse> = {}): CameraResponse {
  return {
    id: 'c0a80101-0000-4000-8000-000000000001', cameraCode: 'CAM-001', name: 'North Gate',
    organizationUnitId: 'c0a80101-0000-4000-8000-000000000010', geographicAreaId: 'c0a80101-0000-4000-8000-000000000020',
    cameraType: 'FIXED', latitude: 12.9716, longitude: 77.5946,
    manufacturer: null, model: null, serialNumber: null, altitude: null, mountingHeight: null,
    azimuth: null, tilt: null, horizontalFov: null, verticalFov: null, effectiveRange: null,
    ipAddress: null, port: null, protocol: null, vmsId: null, streamReference: null, credentialReference: null,
    installationDate: null, operationalStatus: 'ACTIVE', connectivityStatus: 'ONLINE', maintenanceStatus: 'NORMAL',
    hasCoverage: false, lastSeenAt: null, lastHealthCheckAt: null, retiredAt: null, ...overrides,
  };
}

export function organizationFixture(overrides: Partial<OrganizationResponse> = {}): OrganizationResponse {
  return {
    id: '11111111-1111-4111-8111-111111111111', code: 'OPS', name: 'Operations',
    organizationType: 'PUBLIC', description: null, status: 'ACTIVE', ...overrides,
  };
}

export function geographicAreaFixture(overrides: Partial<GeographicAreaResponse> = {}): GeographicAreaResponse {
  return {
    id: '33333333-3333-4333-8333-333333333333', parentAreaId: null, code: 'HQ',
    name: 'Headquarters', areaType: 'DISTRICT', status: 'ACTIVE', ...overrides,
  };
}

export function vmsFixture(overrides: Partial<VmsResponse> = {}): VmsResponse {
  return {
    id: '44444444-4444-4444-8444-444444444444', code: 'NORTH-NVR',
    organizationUnitId: '22222222-2222-4222-8222-222222222222',
    geographicAreaId: '33333333-3333-4333-8333-333333333333',
    displayName: 'North NVR', vendor: 'DahuaCgi', runtimeClass: 'Managed',
    endpoint: 'https://nvr.example.test', credentialReference: 'vms/north-nvr',
    verifyTls: true, state: 'Active', expectedCameraCount: 24, ...overrides,
  };
}
