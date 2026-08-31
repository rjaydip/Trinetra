import '@testing-library/jest-dom/vitest';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen } from '@testing-library/react';
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
  it('shows an actionable unavailable state when the map request fails', async () => {
    sessionStorage.setItem('trinetra.auth.session', JSON.stringify({
      token: 'map-token',
      expiresAt: new Date(Date.now() + 60_000).toISOString(),
      mustChangePassword: false,
    }));
    vi.stubGlobal('fetch', async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url.includes('/api/v1/cameras?')) {
        return new Response(JSON.stringify({ items: [{
          id: 'c0a80101-0000-4000-8000-000000000001', cameraCode: 'CAM-001', name: 'Gate',
          organizationUnitId: 'c0a80101-0000-4000-8000-000000000010', siteId: 'c0a80101-0000-4000-8000-000000000020',
          cameraType: 'Fixed', latitude: 12.9716, longitude: 77.5946, operationalStatus: 'ACTIVE',
          connectivityStatus: 'ONLINE', maintenanceStatus: 'CURRENT', hasCoverage: false,
          lastSeenAt: null, lastHealthCheckAt: null, retiredAt: null,
        }], nextCursor: null }), {
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
});
