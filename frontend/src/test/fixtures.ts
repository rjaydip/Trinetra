import type { AuthResponse, CameraResponse } from '../api/models';

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
    organizationUnitId: 'c0a80101-0000-4000-8000-000000000010', siteId: 'c0a80101-0000-4000-8000-000000000020',
    cameraType: 'FIXED', latitude: 12.9716, longitude: 77.5946,
    manufacturer: null, model: null, serialNumber: null, altitude: null, mountingHeight: null,
    azimuth: null, tilt: null, horizontalFov: null, verticalFov: null, effectiveRange: null,
    ipAddress: null, port: null, protocol: null, vmsId: null, streamReference: null, credentialReference: null,
    installationDate: null, operationalStatus: 'ACTIVE', connectivityStatus: 'ONLINE', maintenanceStatus: 'NORMAL',
    hasCoverage: false, lastSeenAt: null, lastHealthCheckAt: null, retiredAt: null, ...overrides,
  };
}
