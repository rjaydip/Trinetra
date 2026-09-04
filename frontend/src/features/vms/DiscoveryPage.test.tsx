import '@testing-library/jest-dom/vitest';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes, useNavigate } from 'react-router-dom';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { App } from '../../App';
import { AuthProvider } from '../../auth/AuthProvider';
import { saveSession } from '../../auth/session';
import { sessionFixture } from '../../test/fixtures';
import { DiscoveryPage } from './DiscoveryPage';

const organizationId = '11111111-1111-4111-8111-111111111111';
const organizationUnitId = '22222222-2222-4222-8222-222222222222';
const siteId = '33333333-3333-4333-8333-333333333333';
const vmsId = '44444444-4444-4444-8444-444444444444';
const secondVmsId = '77777777-7777-4777-8777-777777777777';

const organization = { id: organizationId, code: 'OPS', name: 'Operations', organizationType: 'PUBLIC', description: null, status: 'ACTIVE' };
const organizationUnit = { id: organizationUnitId, organizationId, parentUnitId: null, code: 'NORTH', name: 'North Unit', unitType: 'REGION', status: 'ACTIVE' };
const site = { id: siteId, code: 'HQ', name: 'Headquarters', geographicAreaId: '55555555-5555-4555-8555-555555555555', siteType: 'OFFICE', address: null, latitude: null, longitude: null, status: 'ACTIVE' };
const vms = {
  id: vmsId,
  code: 'NVR-001',
  organizationUnitId,
  siteId,
  displayName: 'North NVR',
  vendor: 'DahuaCgi',
  runtimeClass: 'Managed',
  endpoint: 'https://nvr.example.test',
  credentialReference: 'vms/north-nvr',
  verifyTls: true,
  state: 'Active',
  expectedCameraCount: 24,
};
const cameras = [{
  nativeCameraId: 'CAM-07', cameraId: null, name: 'Gate 7', vendorModel: 'IPC-HFW1230S', firmware: '2.8',
  isEnabled: true, isRecording: true, health: 'ONLINE', lastSeen: '2026-09-04T10:00:00Z',
  streamReferences: ['rtsp://stream/7'], statusChangedAt: '2026-09-04T09:30:00Z',
}, {
  nativeCameraId: 'CAM-08', cameraId: null, name: 'Gate 8', vendorModel: 'IPC-HFW1230S', firmware: '2.8',
  isEnabled: true, isRecording: false, health: 'DEGRADED', lastSeen: null,
  streamReferences: ['rtsp://stream/8'], statusChangedAt: null,
}];

afterEach(() => {
  vi.unstubAllGlobals();
  sessionStorage.clear();
});

function apiHandler(requests: Array<{ path: string; method: string; body?: unknown }>, result: object = {
  created: 0,
  updated: 0,
  failed: 1,
  rows: [{ index: 0, cameraCode: 'NVR-001-CAM-07', status: 'error', cameraId: null, error: 'Site is outside your authorized scope.' }],
}, bulkStatus = 200) {
  return vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = new URL(String(input));
    const method = init?.method ?? 'GET';
    requests.push({ path: `${url.pathname}${url.search}`, method, body: typeof init?.body === 'string' ? JSON.parse(init.body) : undefined });
    if (url.pathname === `/api/v1/vms/${vmsId}`) return Response.json(vms);
    if (url.pathname === `/api/v1/vms/${vmsId}/cameras`) return Response.json(cameras);
    if (url.pathname === '/api/v1/organizations') return Response.json([organization]);
    if (url.pathname === `/api/v1/organizations/${organizationId}/units`) return Response.json([organizationUnit]);
    if (url.pathname === '/api/v1/sites') return Response.json([site]);
    if (url.pathname === '/api/v1/gis/cameras') return Response.json({ type: 'FeatureCollection', features: [] });
    if (url.pathname === '/api/v1/cameras/bulk-import' && method === 'POST') return Response.json(result, { status: bulkStatus });
    return new Response(null, { status: 404 });
  });
}

function renderDiscoveryPage(requests: Array<{ path: string; method: string; body?: unknown }>, result?: object, bulkStatus?: number) {
  saveSession(sessionFixture('discovery-user', ['vms.read', 'camera.import']));
  vi.stubGlobal('fetch', apiHandler(requests, result, bulkStatus));
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <MemoryRouter initialEntries={[`/vms/${vmsId}/discovery`]}>
      <AuthProvider>
        <QueryClientProvider client={queryClient}>
          <Routes><Route path="/vms/:vmsId/discovery" element={<DiscoveryPage />} /></Routes>
        </QueryClientProvider>
      </AuthProvider>
    </MemoryRouter>,
  );
}

function DiscoveryRouteSwitcher() {
  const navigate = useNavigate();
  return <button type="button" onClick={() => navigate(`/vms/${secondVmsId}/discovery`)}>Open second discovery</button>;
}

async function completeGate7(user: ReturnType<typeof userEvent.setup>) {
  await user.selectOptions(screen.getByLabelText(/^organization for gate 7$/i), organizationId);
  await user.selectOptions(await screen.findByLabelText(/^organization unit for gate 7$/i), organizationUnitId);
  await user.selectOptions(screen.getByLabelText(/^site for gate 7$/i), siteId);
  await user.selectOptions(screen.getByLabelText(/^camera type for gate 7$/i), 'FIXED');
  await user.type(screen.getByLabelText(/^latitude for gate 7$/i), '19.076012345');
  await user.type(screen.getByLabelText(/^longitude for gate 7$/i), '72.877700049');
  await user.type(screen.getByLabelText(/^horizontal field of view for gate 7$/i), '82.5');
}

describe('DiscoveryPage', () => {
  it('rejects an incomplete selected row before calling bulk import', async () => {
    const requests: Array<{ path: string; method: string; body?: unknown }> = [];
    const user = userEvent.setup();
    renderDiscoveryPage(requests);

    await user.click(await screen.findByRole('checkbox', { name: /gate 7/i }));
    await user.click(screen.getByRole('button', { name: /import selected/i }));

    expect(requests.some(({ path }) => path === '/api/v1/cameras/bulk-import')).toBe(false);
    expect(screen.getByText(/latitude is required/i)).toBeVisible();
  });

  it('submits only selected enriched rows as an exact upsert payload and renders API row failures', async () => {
    const requests: Array<{ path: string; method: string; body?: unknown }> = [];
    const user = userEvent.setup();
    renderDiscoveryPage(requests);

    await user.click(await screen.findByRole('checkbox', { name: /gate 7/i }));
    await completeGate7(user);
    await user.click(screen.getByRole('checkbox', { name: /gate 8/i }));
    await user.type(screen.getByLabelText(/^latitude for gate 8$/i), '12.34');
    await user.click(screen.getByRole('checkbox', { name: /gate 8/i }));
    expect(screen.getByLabelText(/^latitude for gate 8$/i)).toHaveValue('12.34');
    await user.click(screen.getByRole('button', { name: /import selected/i }));

    await waitFor(() => expect(requests.some(({ path, method }) => path === '/api/v1/cameras/bulk-import' && method === 'POST')).toBe(true));
    const bulkRequest = requests.find(({ path, method }) => path === '/api/v1/cameras/bulk-import' && method === 'POST');
    expect(bulkRequest?.body).toEqual({
      mode: 'upsert',
      items: [{
        cameraCode: 'NVR-001-CAM-07',
        name: 'Gate 7',
        organizationUnitId,
        siteId,
        cameraType: 'FIXED',
        latitude: 19.0760123,
        longitude: 72.8777,
        model: 'IPC-HFW1230S',
        horizontalFov: 82.5,
        vmsId,
        streamReference: 'rtsp://stream/7',
      }],
    });

    const result = await screen.findByRole('region', { name: /import results/i });
    expect(within(result).getByText('Created: 0. Updated: 0. Failed: 1.')).toBeVisible();
    expect(within(result).getByText('Site is outside your authorized scope.')).toBeVisible();
  });

  it('renders a request-level API problem without losing the selected row', async () => {
    const requests: Array<{ path: string; method: string; body?: unknown }> = [];
    const user = userEvent.setup();
    renderDiscoveryPage(requests, { title: 'Forbidden', detail: 'Camera import is not permitted for this scope.' }, 403);

    await user.click(await screen.findByRole('checkbox', { name: /gate 7/i }));
    await completeGate7(user);
    await user.click(screen.getByRole('button', { name: /import selected/i }));

    expect(await screen.findByRole('alert')).toHaveTextContent('Camera import is not permitted for this scope.');
    expect(screen.getByRole('checkbox', { name: /gate 7/i })).toBeChecked();
  });

  it('shows an inline error for invalid optional coverage metadata', async () => {
    const requests: Array<{ path: string; method: string; body?: unknown }> = [];
    const user = userEvent.setup();
    renderDiscoveryPage(requests);

    await user.click(await screen.findByRole('checkbox', { name: /gate 7/i }));
    await completeGate7(user);
    const horizontalFov = screen.getByLabelText(/^horizontal field of view for gate 7$/i);
    await user.clear(horizontalFov);
    await user.type(horizontalFov, '500');
    await user.click(screen.getByRole('button', { name: /import selected/i }));

    expect(requests.some(({ path }) => path === '/api/v1/cameras/bulk-import')).toBe(false);
    expect(await screen.findByText('Horizontal field of view must be between 0.001 and 360.')).toBeVisible();
  });

  it('keeps one enrichment panel expanded at a time', async () => {
    const requests: Array<{ path: string; method: string; body?: unknown }> = [];
    const user = userEvent.setup();
    renderDiscoveryPage(requests);

    await user.click(await screen.findByRole('checkbox', { name: /gate 7/i }));
    expect(screen.getByRole('region', { name: /onboarding details for gate 7/i })).toBeVisible();
    await user.click(screen.getByRole('checkbox', { name: /gate 8/i }));

    expect(screen.queryByRole('region', { name: /onboarding details for gate 7/i })).not.toBeInTheDocument();
    expect(screen.getByRole('region', { name: /onboarding details for gate 8/i })).toBeVisible();
  });

  it('loads bounded map context only after a selected row has valid coordinates', async () => {
    const requests: Array<{ path: string; method: string; body?: unknown }> = [];
    const user = userEvent.setup();
    renderDiscoveryPage(requests);
    const checkbox = await screen.findByRole('checkbox', { name: /gate 7/i });

    expect(requests.some(({ path }) => path.startsWith('/api/v1/gis/cameras'))).toBe(false);
    await user.click(checkbox);
    await user.type(screen.getByLabelText(/^latitude for gate 7$/i), '19.076');
    expect(requests.some(({ path }) => path.startsWith('/api/v1/gis/cameras'))).toBe(false);
    await user.type(screen.getByLabelText(/^longitude for gate 7$/i), '72.8777');

    await waitFor(() => expect(requests.some(({ path }) => path.startsWith('/api/v1/gis/cameras'))).toBe(true));
    const gisPath = requests.find(({ path }) => path.startsWith('/api/v1/gis/cameras'))!.path;
    const bbox = new URL(gisPath, 'http://localhost').searchParams.get('bbox')!.split(',').map(Number);
    expect(bbox[2] - bbox[0]).toBeCloseTo(0.01, 10);
    expect(bbox[3] - bbox[1]).toBeCloseTo(0.01, 10);
  });

  it('clears target-local selection and enrichment when the VMS route changes', async () => {
    const secondVms = { ...vms, id: secondVmsId, code: 'NVR-002', displayName: 'South NVR' };
    saveSession(sessionFixture('discovery-route-user', ['vms.read', 'camera.import']));
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => {
      const path = new URL(String(input)).pathname;
      if (path === `/api/v1/vms/${vmsId}`) return Response.json(vms);
      if (path === `/api/v1/vms/${secondVmsId}`) return Response.json(secondVms);
      if (path === `/api/v1/vms/${vmsId}/cameras` || path === `/api/v1/vms/${secondVmsId}/cameras`) return Response.json(cameras);
      if (path === '/api/v1/organizations') return Response.json([organization]);
      if (path === '/api/v1/sites') return Response.json([site]);
      return new Response(null, { status: 404 });
    }));
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const user = userEvent.setup();
    render(<MemoryRouter initialEntries={[`/vms/${vmsId}/discovery`]}><DiscoveryRouteSwitcher /><AuthProvider><QueryClientProvider client={queryClient}><Routes><Route path="/vms/:vmsId/discovery" element={<DiscoveryPage />} /></Routes></QueryClientProvider></AuthProvider></MemoryRouter>);

    await user.click(await screen.findByRole('checkbox', { name: /gate 7/i }));
    expect(screen.getByLabelText(/^camera code for gate 7$/i)).toHaveValue('NVR-001-CAM-07');
    await user.click(screen.getByRole('button', { name: /open second discovery/i }));

    expect(await screen.findByRole('heading', { name: /import cameras from south nvr/i })).toBeVisible();
    expect(screen.getByRole('checkbox', { name: /gate 7/i })).not.toBeChecked();
  });

  it('guards the app discovery route with camera.import as well as vms.read', async () => {
    saveSession(sessionFixture('read-only-vms-user', ['vms.read']));
    const fetchMock = vi.fn(async () => Response.json([]));
    vi.stubGlobal('fetch', fetchMock);

    render(<MemoryRouter initialEntries={[`/vms/${vmsId}/discovery`]}><AuthProvider><App /></AuthProvider></MemoryRouter>);

    expect(await screen.findByRole('heading', { name: /action unavailable/i })).toBeVisible();
    expect(fetchMock).not.toHaveBeenCalled();
  });
});
