import '@testing-library/jest-dom/vitest';
import { render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { App } from '../../App';
import { AuthProvider } from '../../auth/AuthProvider';

const cameraId = 'c0a80101-0000-4000-8000-000000000001';

function renderApp(path: string) {
  return render(
    <MemoryRouter initialEntries={[path]}>
      <AuthProvider><App /></AuthProvider>
    </MemoryRouter>,
  );
}

function signIn() {
  sessionStorage.setItem('trinetra.auth.session', JSON.stringify({
    token: 'detail-token',
    expiresAt: new Date(Date.now() + 60_000).toISOString(),
    mustChangePassword: false,
  }));
}

function camera() {
  return {
    id: cameraId, cameraCode: 'CAM-001', name: 'North Gate',
    organizationUnitId: 'c0a80101-0000-4000-8000-000000000010',
    siteId: 'c0a80101-0000-4000-8000-000000000020',
    cameraType: 'FIXED', latitude: 12.9716, longitude: 77.5946,
    manufacturer: 'Axis', model: null, serialNumber: null, altitude: null,
    mountingHeight: null, azimuth: null, tilt: null, horizontalFov: null,
    verticalFov: null, effectiveRange: null, ipAddress: null, port: null,
    protocol: null, vmsId: null, streamReference: null, credentialReference: null,
    installationDate: null, operationalStatus: 'ACTIVE', connectivityStatus: 'ONLINE',
    maintenanceStatus: 'NORMAL', hasCoverage: false, lastSeenAt: null,
    lastHealthCheckAt: null, retiredAt: null,
  };
}

afterEach(() => {
  vi.unstubAllGlobals();
  sessionStorage.clear();
});

describe('CameraDetailPage', () => {
  it('shows current health data from the documented health endpoint', async () => {
    signIn();
    vi.stubGlobal('fetch', async (input: RequestInfo | URL) => {
      const url = new URL(String(input));
      if (url.pathname === `/api/v1/cameras/${cameraId}`) {
        return Response.json(camera());
      }
      if (url.pathname === `/api/v1/cameras/${cameraId}/health`) {
        return Response.json({
          cameraId, operationalStatus: 'ACTIVE', connectivityStatus: 'DEGRADED',
          maintenanceStatus: 'NORMAL', lastSeenAt: null, lastHealthCheckAt: null,
          failureReason: 'High latency',
        });
      }
      if (url.pathname === `/api/v1/cameras/${cameraId}/maintenance`) return Response.json([]);
      return new Response(null, { status: 404 });
    });

    renderApp(`/cameras/${cameraId}`);

    expect(await screen.findByRole('heading', { name: 'North Gate' })).toBeVisible();
    expect(await screen.findByText('High latency')).toBeVisible();
  });

  it('labels a forbidden supplemental section as unavailable without inventing data', async () => {
    signIn();
    vi.stubGlobal('fetch', async (input: RequestInfo | URL) => {
      const url = new URL(String(input));
      if (url.pathname === `/api/v1/cameras/${cameraId}`) return Response.json(camera());
      if (url.pathname === `/api/v1/cameras/${cameraId}/health`) {
        return Response.json({ title: 'Forbidden', detail: 'No camera.health.read permission.' }, { status: 403 });
      }
      if (url.pathname === `/api/v1/cameras/${cameraId}/maintenance`) return Response.json([]);
      return new Response(null, { status: 404 });
    });

    renderApp(`/cameras/${cameraId}`);

    expect(await screen.findByText(/health information is unavailable for your permissions/i)).toBeVisible();
    expect(screen.queryByText(/high latency/i)).not.toBeInTheDocument();
  });
});
