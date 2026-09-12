import '@testing-library/jest-dom/vitest';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { App } from '../../App';
import { AuthProvider } from '../../auth/AuthProvider';

function renderApp(path: string) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={[path]}>
        <AuthProvider>
          <App />
        </AuthProvider>
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
  sessionStorage.clear();
});

describe('MapPage', () => {
  function signInForMap() {
    sessionStorage.setItem('trinetra.auth.session', JSON.stringify({
      token: 'map-token',
      expiresAt: new Date(Date.now() + 60_000).toISOString(),
      mustChangePassword: false,
    }));
  }

  function liveCamera() {
    return {
      id: 'c0a80101-0000-4000-8000-000000000001', cameraCode: 'CAM-001', name: 'Gate',
      organizationUnitId: 'c0a80101-0000-4000-8000-000000000010', siteId: 'c0a80101-0000-4000-8000-000000000020',
      cameraType: 'Fixed', latitude: 12.9716, longitude: 77.5946, operationalStatus: 'ACTIVE',
      connectivityStatus: 'ONLINE', maintenanceStatus: 'CURRENT', hasCoverage: false,
      lastSeenAt: null, lastHealthCheckAt: null, retiredAt: null,
    };
  }

  function installLiveMapFetch() {
    vi.stubGlobal('fetch', async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url.includes('/api/v1/cameras?')) {
        return new Response(JSON.stringify({ items: [liveCamera()], nextCursor: null }), {
          status: 200, headers: { 'content-type': 'application/json' },
        });
      }
      if (url.includes('/api/v1/gis/cameras?')) {
        return new Response(JSON.stringify({
          type: 'FeatureCollection',
          features: [{ type: 'Feature', geometry: { type: 'Point', coordinates: [77.5946, 12.9716] }, properties: {
            cameraId: liveCamera().id, cameraCode: 'CAM-001', name: 'Gate', cameraType: 'Fixed', connectivityStatus: 'ONLINE',
          } }],
        }), { status: 200, headers: { 'content-type': 'application/json' } });
      }
      if (url.includes(`/api/v1/cameras/${liveCamera().id}`)) {
        return new Response(JSON.stringify(liveCamera()), { status: 200, headers: { 'content-type': 'application/json' } });
      }
      return new Response(null, { status: 404 });
    });
  }

  it('shows an actionable unavailable state when the map request fails', async () => {
    signInForMap();
    vi.stubGlobal('fetch', async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url.includes('/api/v1/cameras?')) {
        return new Response(JSON.stringify({ items: [liveCamera()], nextCursor: null }), {
          status: 200,
          headers: { 'content-type': 'application/json' },
        });
      }

      if (url.includes('/api/v1/gis/cameras?')) throw new TypeError('network unavailable');
      return new Response(null, { status: 404 });
    });

    renderApp('/dashboard');

    expect(await screen.findByText(/couldn't load map cameras/i)).toBeVisible();
  });

  it('opens a camera detail from a keyboard-operable map selection control', async () => {
    signInForMap();
    installLiveMapFetch();
    const user = userEvent.setup();
    renderApp('/dashboard');

    const selection = await screen.findByRole('button', { name: /open camera gate/i });
    selection.focus();
    await user.keyboard('{Enter}');

    expect(await screen.findByRole('dialog', { name: /camera details/i })).toBeVisible();
    expect(screen.getByRole('heading', { name: 'Gate' })).toBeVisible();
  });

  it('traps drawer focus, isolates the map, and restores selection focus after Escape', async () => {
    signInForMap();
    installLiveMapFetch();
    const user = userEvent.setup();
    renderApp('/dashboard');

    const selection = await screen.findByRole('button', { name: /open camera gate/i });
    await user.click(selection);
    const dialog = await screen.findByRole('dialog', { name: /camera details/i });
    const close = screen.getByRole('button', { name: /close details/i });
    expect(dialog).toContainElement(close);
    expect(document.querySelector('.app-shell')).toHaveAttribute('aria-hidden', 'true');
    expect(document.querySelector('.app-shell')).toHaveAttribute('inert');

    await user.tab();
    expect(close).toHaveFocus();
    await user.keyboard('{Escape}');

    expect(screen.queryByRole('dialog', { name: /camera details/i })).not.toBeInTheDocument();
    expect(selection).toHaveFocus();
    expect(document.querySelector('.app-shell')).not.toHaveAttribute('aria-hidden');
    expect(document.querySelector('.app-shell')).not.toHaveAttribute('inert');
  });
});
