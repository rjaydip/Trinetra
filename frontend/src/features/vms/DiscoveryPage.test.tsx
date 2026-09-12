import '@testing-library/jest-dom/vitest';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes, useNavigate } from 'react-router-dom';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { App } from '../../App';
import type { FederatedCameraResponse } from '../../api/models';
import { AuthProvider } from '../../auth/AuthProvider';
import { saveSession } from '../../auth/session';
import { sessionFixture } from '../../test/fixtures';
import { DiscoveryPage } from './DiscoveryPage';

const organizationId = '11111111-1111-4111-8111-111111111111';
const organizationUnitId = '22222222-2222-4222-8222-222222222222';
const areaId = '33333333-3333-4333-8333-333333333333';
const vmsId = '44444444-4444-4444-8444-444444444444';
const secondVmsId = '77777777-7777-4777-8777-777777777777';

const organization = { id: organizationId, code: 'OPS', name: 'Operations', organizationType: 'PUBLIC', description: null, status: 'ACTIVE' };
const organizationUnit = { id: organizationUnitId, organizationId, parentUnitId: null, code: 'NORTH', name: 'North Unit', unitType: 'REGION', status: 'ACTIVE' };
const area = { id: areaId, parentAreaId: null, code: 'HQ', name: 'Headquarters', areaType: 'DISTRICT', status: 'ACTIVE' };
const vms = {
  id: vmsId,
  code: 'NVR-001',
  organizationUnitId,
  geographicAreaId: areaId,
  displayName: 'North NVR',
  vendor: 'DahuaCgi',
  runtimeClass: 'Managed',
  endpoint: 'https://nvr.example.test',
  credentialReference: 'vms/north-nvr',
  verifyTls: true,
  state: 'Active',
  expectedCameraCount: 24,
};
const cameras: FederatedCameraResponse[] = [{
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
  rows: [{ index: 0, cameraCode: 'NVR-001-CAM-07', status: 'error', cameraId: null, error: 'Geographic area is outside your authorized scope.' }],
}, bulkStatus = 200, target = vms, discoveredCameras = cameras) {
  return vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = new URL(String(input));
    const method = init?.method ?? 'GET';
    requests.push({ path: `${url.pathname}${url.search}`, method, body: typeof init?.body === 'string' ? JSON.parse(init.body) : undefined });
    if (url.pathname === `/api/v1/vms/${vmsId}`) return Response.json(target);
    if (url.pathname === `/api/v1/vms/${vmsId}/cameras`) return Response.json(discoveredCameras);
    if (url.pathname === '/api/v1/organizations') return Response.json([organization]);
    if (url.pathname === `/api/v1/organizations/${organizationId}/units`) return Response.json([organizationUnit]);
    if (url.pathname === '/api/v1/geographic-areas') return Response.json([area]);
    if (url.pathname === '/api/v1/gis/cameras') return Response.json({ type: 'FeatureCollection', features: [] });
    if (url.pathname === '/api/v1/cameras/bulk-import' && method === 'POST') return Response.json(result, { status: bulkStatus });
    return new Response(null, { status: 404 });
  });
}

function renderDiscoveryPage(requests: Array<{ path: string; method: string; body?: unknown }>, result?: object, bulkStatus?: number, target = vms, discoveredCameras = cameras) {
  saveSession(sessionFixture('discovery-user', ['vms.read', 'camera.import']));
  vi.stubGlobal('fetch', apiHandler(requests, result, bulkStatus, target, discoveredCameras));
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
  await user.selectOptions(screen.getByLabelText(/^geographic area for gate 7$/i), areaId);
  await user.selectOptions(screen.getByLabelText(/^camera type for gate 7$/i), 'FIXED');
  await user.type(screen.getByLabelText(/^latitude for gate 7$/i), '19.076012345');
  await user.type(screen.getByLabelText(/^longitude for gate 7$/i), '72.877700049');
  await user.type(screen.getByLabelText(/^horizontal field of view for gate 7$/i), '82.5');
}

describe('DiscoveryPage', () => {
  it('rejects 501 selected cameras locally before validating rows or sending a request', async () => {
    const requests: Array<{ path: string; method: string; body?: unknown }> = [];
    const inventory = Array.from({ length: 501 }, (_, index) => ({
      ...cameras[0],
      nativeCameraId: `CAM-${index + 1}`,
      name: `Camera ${index + 1}`,
      streamReferences: [`rtsp://stream/${index + 1}`],
    }));
    const { container } = renderDiscoveryPage(requests, undefined, undefined, vms, inventory);

    await waitFor(() => expect(container.querySelector('input[aria-label="Select all importable cameras"]')).not.toBeNull());
    fireEvent.click(container.querySelector('input[aria-label="Select all importable cameras"]')!);
    fireEvent.click(container.querySelector('button[type="submit"]')!);

    await waitFor(() => expect(container.querySelector('[role="alert"]')).toHaveTextContent('Select no more than 500 cameras per import. 501 are selected.'));
    expect(requests.some(({ path }) => path === '/api/v1/cameras/bulk-import')).toBe(false);
  });

  it('does not allow an already linked discovered camera to be selected or imported again', async () => {
    const requests: Array<{ path: string; method: string; body?: unknown }> = [];
    const linkedCameraId = '88888888-8888-4888-8888-888888888888';
    const user = userEvent.setup();
    renderDiscoveryPage(requests, undefined, undefined, vms, [{ ...cameras[0], cameraId: linkedCameraId }]);

    const linkedSelection = await screen.findByRole('checkbox', { name: /gate 7/i });
    expect(linkedSelection).toBeDisabled();
    expect(screen.getByText(/already linked.*cannot be imported again/i)).toBeVisible();
    await user.click(linkedSelection);
    await user.click(screen.getByRole('button', { name: /import selected/i }));

    expect(await screen.findByRole('alert')).toHaveTextContent('Select at least one discovered camera to import.');
    expect(linkedSelection).not.toBeChecked();
    expect(requests.some(({ path }) => path === '/api/v1/cameras/bulk-import')).toBe(false);
  });

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
        geographicAreaId: areaId,
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
    expect(within(result).getByText('Geographic area is outside your authorized scope.')).toBeVisible();
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

  it('shows an inline error when the proposed generated camera code exceeds 100 characters', async () => {
    const requests: Array<{ path: string; method: string; body?: unknown }> = [];
    const user = userEvent.setup();
    renderDiscoveryPage(requests, undefined, undefined, { ...vms, code: 'N'.repeat(94) });

    await user.click(await screen.findByRole('checkbox', { name: /gate 7/i }));
    await completeGate7(user);
    await user.click(screen.getByRole('button', { name: /import selected/i }));

    expect(requests.some(({ path }) => path === '/api/v1/cameras/bulk-import')).toBe(false);
    expect(await screen.findByText('Camera code must be at most 100 characters.')).toBeVisible();
    expect(screen.getByLabelText(/^camera code for gate 7$/i)).toHaveAttribute('aria-invalid', 'true');
  });

  it('shows an accessible inline error when an edited name exceeds 255 characters', async () => {
    const requests: Array<{ path: string; method: string; body?: unknown }> = [];
    const user = userEvent.setup();
    renderDiscoveryPage(requests);

    await user.click(await screen.findByRole('checkbox', { name: /gate 7/i }));
    await completeGate7(user);
    const name = screen.getByLabelText(/^name for gate 7$/i);
    await user.clear(name);
    await user.type(name, 'N'.repeat(256));
    await user.click(screen.getByRole('button', { name: /import selected/i }));

    const error = await screen.findByText('Name must be at most 255 characters.');
    expect(requests.some(({ path }) => path === '/api/v1/cameras/bulk-import')).toBe(false);
    expect(error).toBeVisible();
    expect(name).toHaveAttribute('aria-invalid', 'true');
    expect(name).toHaveAttribute('aria-describedby', error.id);
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
      if (path === '/api/v1/geographic-areas') return Response.json([area]);
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
