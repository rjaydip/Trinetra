import '@testing-library/jest-dom/vitest';
import { act, render, screen, within } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { App } from '../../App';
import { AuthProvider } from '../../auth/AuthProvider';
import { cameraFixture, sessionFixture } from '../../test/fixtures';

const cameraId = 'c0a80101-0000-4000-8000-000000000001';

function renderApp(path: string) {
  return render(
    <MemoryRouter initialEntries={[path]}>
      <AuthProvider><App /></AuthProvider>
    </MemoryRouter>,
  );
}

function signIn(permissions = ['camera.health.read']) {
  sessionStorage.setItem('trinetra.auth.session', JSON.stringify(sessionFixture('details', permissions)));
}

function camera() {
  return {
    id: cameraId, cameraCode: 'CAM-001', name: 'North Gate',
    organizationUnitId: 'c0a80101-0000-4000-8000-000000000010',
    geographicAreaId: 'c0a80101-0000-4000-8000-000000000020',
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
  it('shows all safe registry, connection and coverage metadata without credential references', async () => {
    signIn();
    const record = cameraFixture({ model: 'Q6135', serialNumber: 'AX-4421', installationDate: '2025-02-14',
      altitude: 12, mountingHeight: 8, azimuth: 0, tilt: -15, horizontalFov: 90, verticalFov: 45, effectiveRange: 70,
      ipAddress: '10.0.0.8', port: 554, protocol: 'RTSP', vmsId: 'c0a80101-0000-4000-8000-000000000090',
      streamReference: 'external-stream-17', credentialReference: 'DO-NOT-RENDER-CREDENTIAL', hasCoverage: true,
    });
    vi.stubGlobal('fetch', async (input: RequestInfo | URL) => String(input).endsWith(cameraId) ? Response.json(record) : new Response(null, { status: 403 }));
    renderApp(`/cameras/${cameraId}`);
    await screen.findByRole('heading', { name: 'North Gate' });
    const metadata = [
      ['Model', 'Q6135'], ['Serial number', 'AX-4421'],
      ['Installation date', '2025-02-14'], ['IP address', '10.0.0.8'], ['Port', '554'], ['Protocol', 'RTSP'],
      ['Stream reference', 'external-stream-17'], ['Altitude', '12 m'], ['Mounting height', '8 m'],
      ['Azimuth', '0°'], ['Tilt', '-15°'], ['Horizontal field of view', '90°'], ['Vertical field of view', '45°'], ['Effective range', '70 m'],
    ];
    for (const [label, value] of metadata) expect(screen.getByText(label).nextElementSibling).toHaveTextContent(value);
    // Organization unit / geographic area / VMS are resolved to human-readable names via
    // separate lookups; every URL but the camera record itself 403s in this test's fetch stub,
    // so resolution fails and each field falls back to showing the raw id — exactly as it should
    // when the admin lacks permission to read the reference record, or it's since been deleted.
    expect(await screen.findByText('Organization unit')).toBeVisible();
    expect(screen.getByText('Organization unit').nextElementSibling).toHaveTextContent(record.organizationUnitId);
    expect(screen.getByText('Geographic area').nextElementSibling).toHaveTextContent(record.geographicAreaId!);
    expect(screen.getByText('VMS').nextElementSibling).toHaveTextContent(record.vmsId!);
    expect(screen.queryByText('DO-NOT-RENDER-CREDENTIAL')).not.toBeInTheDocument();
    expect(screen.queryByText(/credential reference/i)).not.toBeInTheDocument();
  });

  it.each(['loaded', 'empty', 'forbidden'] as const)('renders the backend health-history %s state', async (state) => {
    signIn();
    vi.stubGlobal('fetch', async (input: RequestInfo | URL) => {
      const url = new URL(String(input));
      if (url.pathname.endsWith('/health/history')) {
        return state === 'forbidden' ? Response.json({ title: 'Forbidden' }, { status: 403 }) : Response.json({
          cameraId, from: '2026-08-30T00:00:00Z', to: '2026-08-31T00:00:00Z', items: state === 'empty' ? [] : [{
            operationalStatus: 'ACTIVE', connectivityStatus: 'DEGRADED', checkedAt: '2026-08-30T14:30:00Z',
            latencyMs: 725, errorCode: 'HEALTH_TIMEOUT', failureReason: 'Probe timed out', source: 'scheduled-probe',
          }],
        });
      }
      return url.pathname.endsWith(cameraId) ? Response.json(camera()) : new Response(null, { status: 403 });
    });
    renderApp(`/cameras/${cameraId}`);
    const history = await screen.findByRole('region', { name: /^health history$/i });
    if (state === 'loaded') {
      expect(await within(history).findByText('Probe timed out')).toBeVisible();
      expect(within(history).getByText('725 ms')).toBeVisible();
      expect(within(history).getByText('HEALTH_TIMEOUT')).toBeVisible();
      expect(within(history).getByText('scheduled-probe')).toBeVisible();
    } else {
      expect(await within(history).findByText(state === 'empty' ? /no health checks/i : /unavailable for your permissions/i)).toBeVisible();
    }
  });

  it('announces health-history loading until the response arrives', async () => {
    signIn();
    let finishHistory: (() => void) | undefined;
    vi.stubGlobal('fetch', (input: RequestInfo | URL) => {
      if (String(input).includes('/health/history')) return new Promise<Response>((resolve) => {
        finishHistory = () => resolve(Response.json({ cameraId, from: '2026-08-30T00:00:00Z', to: '2026-08-31T00:00:00Z', items: [] }));
      });
      return Promise.resolve(String(input).endsWith(cameraId) ? Response.json(camera()) : new Response(null, { status: 403 }));
    });
    renderApp(`/cameras/${cameraId}`);
    expect(await screen.findByText(/loading health history/i)).toBeVisible();
    await act(async () => finishHistory!());
    expect(await screen.findByText(/no health checks/i)).toBeVisible();
  });

  it('presents health history as unavailable when the session lacks its read permission', async () => {
    signIn([]);
    vi.stubGlobal('fetch', async (input: RequestInfo | URL) => String(input).endsWith(cameraId) ? Response.json(camera()) : new Response(null, { status: 403 }));
    renderApp(`/cameras/${cameraId}`);
    const history = await screen.findByRole('region', { name: /^health history$/i });
    expect(within(history).getByText(/unavailable for your permissions/i)).toBeVisible();
    expect(within(history).queryByText(/loading/i)).not.toBeInTheDocument();
  });

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
