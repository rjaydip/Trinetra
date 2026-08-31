import '@testing-library/jest-dom/vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
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
    token: 'registry-token',
    expiresAt: new Date(Date.now() + 60_000).toISOString(),
    mustChangePassword: false,
  }));
}

function liveCamera() {
  return {
    id: cameraId, cameraCode: 'CAM-001', name: 'North Gate',
    organizationUnitId: 'c0a80101-0000-4000-8000-000000000010',
    siteId: 'c0a80101-0000-4000-8000-000000000020',
    cameraType: 'FIXED', latitude: 12.9716, longitude: 77.5946,
    manufacturer: null, model: null, serialNumber: null, altitude: null,
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

describe('RegistryPage', () => {
  it('uses the backend next cursor for the next page', async () => {
    signIn();
    let lastCameraRequest: URL | undefined;
    vi.stubGlobal('fetch', async (input: RequestInfo | URL) => {
      const url = new URL(String(input));
      if (url.pathname === '/api/v1/cameras') {
        lastCameraRequest = url;
        return new Response(JSON.stringify({ items: [liveCamera()], nextCursor: url.searchParams.get('cursor') ? null : 'next-page-token' }), {
          status: 200, headers: { 'content-type': 'application/json' },
        });
      }
      return new Response(null, { status: 404 });
    });
    const user = userEvent.setup();

    renderApp('/cameras');
    await user.click(await screen.findByRole('button', { name: /next page/i }));

    await waitFor(() => expect(lastCameraRequest?.searchParams.get('cursor')).toBe('next-page-token'));
  });

  it('reflects supported filters in the registry URL query and API request', async () => {
    signIn();
    let lastCameraRequest: URL | undefined;
    vi.stubGlobal('fetch', async (input: RequestInfo | URL) => {
      const url = new URL(String(input));
      if (url.pathname === '/api/v1/cameras') {
        lastCameraRequest = url;
        return new Response(JSON.stringify({ items: [liveCamera()], nextCursor: null }), {
          status: 200, headers: { 'content-type': 'application/json' },
        });
      }
      return new Response(null, { status: 404 });
    });
    const user = userEvent.setup();

    renderApp('/cameras');
    await user.type(await screen.findByLabelText(/search cameras/i), 'north');
    await user.type(screen.getByLabelText(/camera type/i), 'FIXED');

    await waitFor(() => {
      expect(lastCameraRequest?.searchParams.get('q')).toBe('north');
      expect(lastCameraRequest?.searchParams.get('cameraType')).toBe('FIXED');
    });
  });
});
