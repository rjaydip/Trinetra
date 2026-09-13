import '@testing-library/jest-dom/vitest';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { AuthProvider } from '../../auth/AuthProvider';
import { saveSession } from '../../auth/session';
import { sessionFixture, vmsFixture } from '../../test/fixtures';
import { ReconciliationPage } from './ReconciliationPage';

const vmsId = '11111111-1111-4111-8111-111111111111';
const organizationUnitId = '22222222-2222-4222-8222-222222222222';
const areaId = '33333333-3333-4333-8333-333333333333';
const existingCameraId = '55555555-5555-4555-8555-555555555555';

const vms = vmsFixture({ id: vmsId, organizationUnitId, geographicAreaId: areaId });
const unreconciledRow = {
  targetId: vmsId, nativeCameraId: 'CAM-07', name: 'Gate 7', vendorModel: 'IPC-HFW1230S', firmware: '2.8',
  organizationUnitId, geographicAreaId: areaId, latitude: 19.076, longitude: 72.8777,
  lastSeen: '2026-09-04T10:00:00Z', streamReferences: ['rtsp://stream/7'],
};

afterEach(() => {
  vi.unstubAllGlobals();
  sessionStorage.clear();
});

function renderPage(permissions: string[]) {
  saveSession(sessionFixture('reconcile-user', permissions));
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <MemoryRouter>
      <AuthProvider>
        <QueryClientProvider client={queryClient}>
          <ReconciliationPage />
        </QueryClientProvider>
      </AuthProvider>
    </MemoryRouter>,
  );
}

function apiHandler(requests: Array<{ path: string; method: string; body?: unknown }>, options: {
  items?: typeof unreconciledRow[];
  searchResults?: Array<{ id: string; name: string; cameraCode: string }>;
} = {}) {
  const items = options.items ?? [unreconciledRow];
  return vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = new URL(String(input));
    requests.push({ path: `${url.pathname}${url.search}`, method: init?.method ?? 'GET', body: typeof init?.body === 'string' ? JSON.parse(init.body) : undefined });
    if (url.pathname === '/api/v1/vms') return Response.json([vms]);
    if (url.pathname === '/api/v1/cameras/unreconciled') return Response.json({ items, nextCursor: null });
    if (url.pathname === '/api/v1/cameras' && url.searchParams.get('q')) {
      return Response.json({ items: (options.searchResults ?? []).map((c) => ({ ...c, geographicAreaId: areaId, organizationUnitId, cameraType: 'FIXED', latitude: 19, longitude: 72, hasCoverage: false, operationalStatus: 'ACTIVE', connectivityStatus: 'ONLINE', maintenanceStatus: 'NORMAL', lastSeenAt: null, lastHealthCheckAt: null, retiredAt: null })), nextCursor: null });
    }
    if (url.pathname === `/api/v1/cameras/${existingCameraId}/reconcile` && init?.method === 'POST') return Response.json({ cameraId: existingCameraId, targetId: vmsId, nativeCameraId: 'CAM-07', vmsId });
    if (url.pathname === '/api/v1/cameras/from-federated' && init?.method === 'POST') return Response.json({ id: '99999999-9999-4999-8999-999999999999' }, { status: 201 });
    return new Response(null, { status: 404 });
  });
}

describe('ReconciliationPage', () => {
  it('shows the backlog with an empty-state message when nothing is unmatched', async () => {
    vi.stubGlobal('fetch', apiHandler([], { items: [] }));
    renderPage(['camera.reconcile']);

    expect(await screen.findByText(/nothing to reconcile/i)).toBeVisible();
  });

  it('links a discovered camera to an existing registry record', async () => {
    const requests: Array<{ path: string; method: string; body?: unknown }> = [];
    vi.stubGlobal('fetch', apiHandler(requests, { searchResults: [{ id: existingCameraId, name: 'Gate Seven', cameraCode: 'CAM-EXISTING-07' }] }));
    const user = userEvent.setup();
    renderPage(['camera.reconcile']);

    await user.click(await screen.findByRole('button', { name: /link existing/i }));
    await user.type(screen.getByLabelText(/search registry cameras/i), 'CAM-EX');
    await user.click(await screen.findByRole('button', { name: /^link$/i }));

    await waitFor(() => {
      const linkCall = requests.find(({ path, method }) => path === `/api/v1/cameras/${existingCameraId}/reconcile` && method === 'POST');
      expect(linkCall).toBeDefined();
      expect(linkCall!.body).toEqual({ targetId: vmsId, nativeCameraId: 'CAM-07', adoptStreamReference: true, adoptVmsId: true });
    });
  });

  it('does not offer "register new" without camera.create', async () => {
    vi.stubGlobal('fetch', apiHandler([]));
    renderPage(['camera.reconcile']);

    await screen.findByRole('button', { name: /link existing/i });
    expect(screen.queryByRole('button', { name: /register new/i })).not.toBeInTheDocument();
  });

  it('validates required fields before registering a new camera from a federated row', async () => {
    const requests: Array<{ path: string; method: string; body?: unknown }> = [];
    vi.stubGlobal('fetch', apiHandler(requests));
    const user = userEvent.setup();
    renderPage(['camera.reconcile', 'camera.create']);

    await user.click(await screen.findByRole('button', { name: /register new/i }));
    await user.click(screen.getByRole('button', { name: /register and link/i }));

    expect(await screen.findByText(/camera code is required/i)).toBeVisible();
    expect(requests.some(({ path }) => path === '/api/v1/cameras/from-federated')).toBe(false);
  });

  it('registers and links a new camera from a federated row once required fields are filled', async () => {
    const requests: Array<{ path: string; method: string; body?: unknown }> = [];
    vi.stubGlobal('fetch', apiHandler(requests));
    const user = userEvent.setup();
    renderPage(['camera.reconcile', 'camera.create']);

    await user.click(await screen.findByRole('button', { name: /register new/i }));
    await user.type(screen.getByLabelText(/^camera code/i), 'NVR-001-CAM-07');
    await user.selectOptions(screen.getByLabelText(/^camera type/i), 'FIXED');
    await user.click(screen.getByRole('button', { name: /register and link/i }));

    await waitFor(() => {
      const createCall = requests.find(({ path, method }) => path === '/api/v1/cameras/from-federated' && method === 'POST');
      expect(createCall).toBeDefined();
      expect(createCall!.body).toEqual(expect.objectContaining({
        targetId: vmsId, nativeCameraId: 'CAM-07', cameraCode: 'NVR-001-CAM-07', cameraType: 'FIXED',
        latitude: 19.076, longitude: 72.8777, geographicAreaId: areaId,
      }));
    });
  });
});
