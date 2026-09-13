import '@testing-library/jest-dom/vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, useNavigate } from 'react-router-dom';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { App } from '../../App';
import { AuthProvider } from '../../auth/AuthProvider';

const cameraId = 'c0a80101-0000-4000-8000-000000000001';

function HistoryBackButton() {
  const navigate = useNavigate();
  return <button onClick={() => navigate(-1)} type="button">Previous route</button>;
}

function renderApp(path: string | string[], initialIndex?: number) {
  return render(
    <MemoryRouter initialEntries={Array.isArray(path) ? path : [path]} initialIndex={initialIndex}>
      <AuthProvider><HistoryBackButton /><App /></AuthProvider>
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
    geographicAreaId: 'c0a80101-0000-4000-8000-000000000020',
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
  it('preserves filter focus while results load and allows recovery from an invalid filter', async () => {
    signIn();
    let rejectFilter: (() => void) | undefined;
    vi.stubGlobal('fetch', (input: RequestInfo | URL) => {
      const url = new URL(String(input));
      if (url.pathname === '/api/v1/cameras' && url.searchParams.get('cameraType')) {
        return new Promise<Response>((resolve) => { rejectFilter = () => resolve(Response.json({ title: 'Invalid filter', detail: 'Camera type is not recognized.' }, { status: 400 })); });
      }
      if (url.pathname === '/api/v1/cameras') return Promise.resolve(Response.json({ items: [liveCamera()], nextCursor: null }));
      if (url.pathname === '/api/v1/organizations') return Promise.resolve(Response.json({ items: [], page: 1, pageSize: 200, total: 0, totalPages: 0 }));
      if (url.pathname === '/api/v1/geographic-areas') return Promise.resolve(Response.json({ items: [], page: 1, pageSize: 200, total: 0, totalPages: 0 }));
      return Promise.resolve(new Response(null, { status: 404 }));
    });
    const user = userEvent.setup();
    renderApp('/cameras');
    const input = await screen.findByLabelText(/camera type/i);
    await user.type(input, 'bad');
    await waitFor(() => expect(rejectFilter).toBeDefined());
    expect(input).toBeVisible();
    expect(input).toHaveFocus();
    rejectFilter!();
    expect(await screen.findByRole('heading', { name: /couldn't load camera registry/i })).toBeVisible();
    expect(screen.getByLabelText(/camera type/i)).toBe(input);
    await user.clear(input);
    expect((await screen.findAllByText('North Gate'))[0]).toBeVisible();
    expect(input).toHaveFocus();
  });

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
    await user.click(await screen.findByRole('button', { name: /^next$/i }));

    await waitFor(() => expect(lastCameraRequest?.searchParams.get('cursor')).toBe('next-page-token'));
  });

  it('disables Next when the API returns no next cursor', async () => {
    signIn();
    vi.stubGlobal('fetch', async () => Response.json({ items: [liveCamera()], nextCursor: null }));

    renderApp('/cameras');

    expect(await screen.findByRole('button', { name: /^next$/i })).toBeDisabled();
  });

  it('disables Next while a refetch retains a next cursor', async () => {
    signIn();
    let resolveRefetch: ((response: Response) => void) | undefined;
    let cameraRequests = 0;
    vi.stubGlobal('fetch', () => {
      cameraRequests += 1;
      if (cameraRequests === 1) return Promise.resolve(Response.json({ items: [liveCamera()], nextCursor: 'opaque-next-cursor' }));
      return new Promise<Response>((resolve) => { resolveRefetch = resolve; });
    });

    renderApp('/cameras');
    const next = await screen.findByRole('button', { name: /^next$/i });
    fireEvent(window, new Event('visibilitychange'));

    await waitFor(() => expect(resolveRefetch).toBeDefined());
    expect(next).toBeDisabled();
    resolveRefetch!(Response.json({ items: [liveCamera()], nextCursor: 'opaque-next-cursor' }));
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

  it('uses the active history entry query for the registry request', async () => {
    signIn();
    let lastCameraRequest: URL | undefined;
    vi.stubGlobal('fetch', async (input: RequestInfo | URL) => {
      const url = new URL(String(input));
      if (url.pathname === '/api/v1/cameras') {
        lastCameraRequest = url;
        return Response.json({ items: [liveCamera()], nextCursor: null });
      }
      return new Response(null, { status: 404 });
    });
    const user = userEvent.setup();

    renderApp(['/cameras?q=first', '/cameras?q=second'], 1);
    await waitFor(() => expect(lastCameraRequest?.searchParams.get('q')).toBe('second'));
    await user.click(screen.getByRole('button', { name: /previous route/i }));

    await waitFor(() => expect(lastCameraRequest?.searchParams.get('q')).toBe('first'));
  });

  it('blocks next-page navigation until a changed filter is committed without a cursor', async () => {
    signIn();
    const cameraRequests: URL[] = [];
    vi.stubGlobal('fetch', async (input: RequestInfo | URL) => {
      const url = new URL(String(input));
      if (url.pathname === '/api/v1/cameras') {
        cameraRequests.push(url);
        return Response.json({ items: [liveCamera()], nextCursor: url.searchParams.get('q') ? null : 'next-page-token' });
      }
      return new Response(null, { status: 404 });
    });
    const user = userEvent.setup();

    renderApp('/cameras');
    const next = await screen.findByRole('button', { name: /^next$/i });
    await user.type(screen.getByLabelText(/search cameras/i), 'north');

    expect(next).toBeDisabled();
    await user.click(next);
    await waitFor(() => expect(cameraRequests.at(-1)?.searchParams.get('q')).toBe('north'));
    expect(cameraRequests.at(-1)?.searchParams.get('cursor')).toBeNull();
  });
});
