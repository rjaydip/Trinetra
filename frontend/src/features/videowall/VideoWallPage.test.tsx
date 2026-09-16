import '@testing-library/jest-dom/vitest';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { VideoWallPage } from './VideoWallPage';

const camera = {
  id: '11111111-1111-4111-8111-111111111111', cameraCode: 'CAM-07', name: 'Gate 7 North',
  organizationUnitId: 'ou-1', geographicAreaId: 'ga-1', cameraType: 'FIXED', latitude: 12.9, longitude: 77.6,
  operationalStatus: 'ONLINE', connectivityStatus: 'CONNECTED', maintenanceStatus: 'NORMAL',
  hasCoverage: false, lastSeenAt: '2026-09-12T10:00:00Z', lastHealthCheckAt: null, retiredAt: null,
  streamReference: 'rtsp://10.0.0.7:554/stream1',
};

function renderPage() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(<QueryClientProvider client={queryClient}><MemoryRouter><VideoWallPage /></MemoryRouter></QueryClientProvider>);
}

/** Base fetch mock: no saved server preference (404) and a one-camera picker/detail set,
 * overridable per-test via `overrides`. */
function stubFetch(overrides: (url: URL, init?: RequestInit) => Response | undefined = () => undefined) {
  vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = new URL(String(input));
    const overridden = overrides(url, init);
    if (overridden) return overridden;
    if (url.pathname === '/api/v1/video-wall/preferences') return new Response(null, { status: 404 });
    if (url.pathname === `/api/v1/cameras/${camera.id}`) return Response.json(camera);
    if (url.pathname === '/api/v1/cameras') return Response.json({ items: [camera], nextCursor: null });
    return new Response(null, { status: 404 });
  }));
}

beforeEach(() => {
  window.localStorage.clear();
});

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('VideoWallPage', () => {
  it('shows an explicit "not configured" state with a Configure action when nothing is saved', async () => {
    stubFetch();
    renderPage();

    expect(await screen.findByText(/video wall is not configured/i)).toBeVisible();
    expect(screen.queryByLabelText('Camera wall tiles')).not.toBeInTheDocument();
  });

  it('syncs from the server even when this device already has a locally-cached layout (cross-device regression)', async () => {
    // Regression test: a device that had ever saved a wall locally used to treat that cache as
    // "already reconciled with the server" and never look at the server response again — so a
    // layout saved from a *different* device (or browser) for the same account was invisible
    // here. The server-saved layout must always win once it's back, even on first mount.
    window.localStorage.setItem('trinetra.videowall.v3', JSON.stringify({ columnCount: 3, cameraIds: [camera.id] }));
    const otherCamera = { ...camera, id: '33333333-3333-4333-8333-333333333333', cameraCode: 'CAM-09', name: 'Loading Dock' };
    stubFetch((url) => {
      if (url.pathname === '/api/v1/video-wall/preferences') return Response.json({ columnCount: 2, cameraIds: [otherCamera.id] });
      if (url.pathname === `/api/v1/cameras/${otherCamera.id}`) return Response.json(otherCamera);
      return undefined;
    });
    renderPage();

    // Instant paint from the stale local cache is fine transiently, but the server's own camera
    // must win once its response lands.
    expect(await screen.findByRole('heading', { name: 'Loading Dock' })).toBeVisible();
    expect(screen.queryByRole('heading', { name: 'Gate 7 North' })).not.toBeInTheDocument();
  });

  it('clears a stale local cache when the server confirms nothing is saved (404)', async () => {
    window.localStorage.setItem('trinetra.videowall.v3', JSON.stringify({ columnCount: 3, cameraIds: [camera.id] }));
    stubFetch(); // base stub already 404s the preferences endpoint

    renderPage();

    expect(await screen.findByText(/video wall is not configured/i)).toBeVisible();
    expect(screen.queryByRole('heading', { name: 'Gate 7 North' })).not.toBeInTheDocument();
  });

  it('configuring: sets a column count, adds a camera by search, and shows its status honestly labeling video as unavailable', async () => {
    stubFetch();
    const user = userEvent.setup();
    renderPage();

    await user.click(await screen.findByRole('button', { name: /configure/i }));
    await user.type(screen.getByLabelText('Columns'), '{backspace}4');
    await user.type(screen.getByLabelText('Add camera'), 'Gate 7');

    const result = await screen.findByRole('button', { name: /Gate 7 North/ });
    await user.click(result);

    expect(await screen.findByRole('heading', { name: 'Gate 7 North' })).toBeVisible();
    expect(screen.getByText('ONLINE')).toBeVisible();
    expect(screen.getByText(/live feed not available/i)).toBeVisible();
    expect(screen.getByText('rtsp://10.0.0.7:554/stream1')).toBeVisible();
  });

  it('keeps showing the configured wall after clicking Done, even while the save is still in flight', async () => {
    // Regression test: an earlier version derived the view-mode grid from the query cache, which
    // only updated once the save mutation resolved — clicking Done before that happened (or if
    // the save failed) made the wall appear to revert to "not configured".
    let resolveSave!: (response: Response) => void;
    stubFetch((url, init) => {
      if (url.pathname === '/api/v1/video-wall/preferences' && init?.method === 'PUT') {
        return new Promise<Response>((resolve) => { resolveSave = resolve; }) as unknown as Response;
      }
      return undefined;
    });
    const user = userEvent.setup();
    renderPage();

    await user.click(await screen.findByRole('button', { name: /configure/i }));
    await user.type(screen.getByLabelText('Add camera'), 'Gate 7');
    await user.click(await screen.findByRole('button', { name: /Gate 7 North/ }));
    await user.click(screen.getByRole('button', { name: /^done$/i }));

    // The save PUT hasn't resolved yet, but the wall must still be visible, not "not configured".
    expect(screen.getByRole('heading', { name: 'Gate 7 North' })).toBeVisible();
    expect(screen.queryByText(/video wall is not configured/i)).not.toBeInTheDocument();

    resolveSave(Response.json({ columnCount: 3, cameraIds: [camera.id] }));
    await waitFor(() => expect(screen.getByRole('heading', { name: 'Gate 7 North' })).toBeVisible());
  });

  it('grows the wall by one tile per added camera and shrinks it when a tile is removed', async () => {
    const secondCamera = { ...camera, id: '22222222-2222-4222-8222-222222222222', cameraCode: 'CAM-08', name: 'Gate 8 South' };
    stubFetch((url) => {
      if (url.pathname === '/api/v1/cameras') return Response.json({ items: [camera, secondCamera], nextCursor: null });
      if (url.pathname === `/api/v1/cameras/${secondCamera.id}`) return Response.json(secondCamera);
      return undefined;
    });
    const user = userEvent.setup();
    renderPage();

    await user.click(await screen.findByRole('button', { name: /configure/i }));
    await user.type(screen.getByLabelText('Add camera'), 'Gate');
    await user.click(await screen.findByRole('button', { name: /Gate 7 North/ }));
    await user.click(await screen.findByRole('button', { name: /Gate 8 South/ }));

    expect(await screen.findByRole('heading', { name: 'Gate 7 North' })).toBeVisible();
    expect(screen.getByRole('heading', { name: 'Gate 8 South' })).toBeVisible();

    const gate7Tile = screen.getByRole('heading', { name: 'Gate 7 North' }).closest('li')!;
    await user.click(within(gate7Tile).getByRole('button', { name: /remove this camera/i }));

    await waitFor(() => expect(screen.queryByRole('heading', { name: 'Gate 7 North' })).not.toBeInTheDocument());
    expect(screen.getByRole('heading', { name: 'Gate 8 South' })).toBeVisible();
  });

  it('applies a saved server preference and shows the grid directly, with a Configure action to re-edit it', async () => {
    stubFetch((url) => {
      if (url.pathname === '/api/v1/video-wall/preferences') {
        return Response.json({ columnCount: 3, cameraIds: [camera.id] });
      }
      return undefined;
    });
    renderPage();

    expect(await screen.findByRole('heading', { name: 'Gate 7 North' })).toBeVisible();
    expect(screen.getByRole('button', { name: /configure/i })).toBeVisible();
    expect(screen.queryByText(/video wall is not configured/i)).not.toBeInTheDocument();
  });

  it('expands a tile to a full-window view and closes it via the close button or Escape', async () => {
    stubFetch((url) => {
      if (url.pathname === '/api/v1/video-wall/preferences') {
        return Response.json({ columnCount: 3, cameraIds: [camera.id] });
      }
      return undefined;
    });
    const user = userEvent.setup();
    renderPage();
    await screen.findByRole('heading', { name: 'Gate 7 North' });

    await user.click(screen.getByRole('button', { name: /view gate 7 north full screen/i }));

    const overlay = screen.getByRole('button', { name: /exit full screen/i }).closest('.videowall-fullscreen') as HTMLElement;
    expect(within(overlay).getByRole('heading', { name: 'Gate 7 North' })).toBeVisible();

    await user.keyboard('{Escape}');
    await waitFor(() => expect(screen.queryByRole('button', { name: /exit full screen/i })).not.toBeInTheDocument());

    // Still visible on the grid itself, unaffected by opening/closing the overlay.
    expect(screen.getByRole('heading', { name: 'Gate 7 North' })).toBeVisible();
  });

  it('debounces a save call after adding a camera and stays usable if the save fails', async () => {
    let putCount = 0;
    stubFetch((url, init) => {
      if (url.pathname === '/api/v1/video-wall/preferences' && init?.method === 'PUT') {
        putCount += 1;
        return new Response(null, { status: 500 });
      }
      return undefined;
    });

    const user = userEvent.setup();
    renderPage();

    await user.click(await screen.findByRole('button', { name: /configure/i }));
    await user.type(screen.getByLabelText('Add camera'), 'Gate 7');
    await user.click(await screen.findByRole('button', { name: /Gate 7 North/ }));
    await screen.findByRole('heading', { name: 'Gate 7 North' });

    await waitFor(() => expect(putCount).toBe(1), { timeout: 3000 });
    // The grid stays usable and the failure is surfaced inline, not as a blocking error state.
    expect(await screen.findByText(/couldn.t save layout/i)).toBeVisible();
    expect(screen.getByRole('heading', { name: 'Gate 7 North' })).toBeVisible();
  });
});
