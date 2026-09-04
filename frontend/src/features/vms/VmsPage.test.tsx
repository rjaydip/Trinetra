import '@testing-library/jest-dom/vitest';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes, useNavigate } from 'react-router-dom';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { App } from '../../App';
import { AuthProvider } from '../../auth/AuthProvider';
import { saveSession } from '../../auth/session';
import { AppShell } from '../../components/AppShell';
import { sessionFixture } from '../../test/fixtures';
import { VmsForm } from './VmsForm';
import { VmsPage } from './VmsPage';

const organizationId = '11111111-1111-4111-8111-111111111111';
const organizationUnitId = '22222222-2222-4222-8222-222222222222';
const siteId = '33333333-3333-4333-8333-333333333333';
const vmsId = '44444444-4444-4444-8444-444444444444';
const secondVmsId = '77777777-7777-4777-8777-777777777777';
const connectionTestId = '66666666-6666-4666-8666-666666666666';

const organization = { id: organizationId, code: 'OPS', name: 'Operations', organizationType: 'PUBLIC', description: null, status: 'ACTIVE' };
const organizationUnit = { id: organizationUnitId, organizationId, parentUnitId: null, code: 'NORTH', name: 'North Unit', unitType: 'REGION', status: 'ACTIVE' };
const site = { id: siteId, code: 'HQ', name: 'Headquarters', geographicAreaId: '55555555-5555-4555-8555-555555555555', siteType: 'OFFICE', address: null, latitude: null, longitude: null, status: 'ACTIVE' };
const vms = {
  id: vmsId,
  code: 'NORTH-NVR',
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

function RouteSwitcher() {
  const navigate = useNavigate();
  return <button type="button" onClick={() => navigate(`/vms/${secondVmsId}`)}>Open second VMS</button>;
}

afterEach(() => {
  vi.unstubAllGlobals();
  sessionStorage.clear();
});

function renderShell(permissions: string[] = []) {
  saveSession(sessionFixture('navigation-user', permissions));
  return render(
    <MemoryRouter initialEntries={['/dashboard']}>
      <AuthProvider>
        <Routes>
          <Route element={<AppShell />}>
            <Route path="/dashboard" element={<p>Dashboard content</p>} />
          </Route>
        </Routes>
      </AuthProvider>
    </MemoryRouter>,
  );
}

function renderPage(permissions: string[]) {
  saveSession(sessionFixture('vms-user', permissions));
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <MemoryRouter initialEntries={['/vms']}>
      <AuthProvider>
        <QueryClientProvider client={queryClient}>
          <VmsPage />
        </QueryClientProvider>
      </AuthProvider>
    </MemoryRouter>,
  );
}

function renderVmsForm() {
  const onSubmit = vi.fn(async () => undefined);
  render(<VmsForm
    organizations={[organization]}
    organizationUnits={[organizationUnit]}
    sites={[site]}
    selectorStates={{ organizations: { state: 'ready' }, organizationUnits: { state: 'ready' }, sites: { state: 'ready' } }}
    onOrganizationChange={() => undefined}
    onSubmit={onSubmit}
  />);
  return onSubmit;
}

async function fillBoundedVmsForm(user: ReturnType<typeof userEvent.setup>, values: {
  code: string;
  displayName: string;
  inventoryPollSeconds: string;
  statusPollSeconds: string;
}) {
  fireEvent.change(screen.getByLabelText(/vms code/i), { target: { value: values.code } });
  fireEvent.change(screen.getByLabelText(/display name/i), { target: { value: values.displayName } });
  await user.selectOptions(screen.getByLabelText(/^organization$/i), organizationId);
  await user.selectOptions(screen.getByLabelText(/organization unit/i), organizationUnitId);
  await user.selectOptions(screen.getByLabelText(/^vendor/i), 'DahuaCgi');
  fireEvent.change(screen.getByLabelText(/^endpoint/i), { target: { value: 'https://nvr.example.test' } });
  fireEvent.change(screen.getByLabelText(/credential reference/i), { target: { value: 'vms/north-nvr' } });
  await user.click(screen.getByRole('button', { name: /connection tuning/i }));
  fireEvent.change(screen.getByLabelText(/inventory poll seconds/i), { target: { value: values.inventoryPollSeconds } });
  fireEvent.change(screen.getByLabelText(/status poll seconds/i), { target: { value: values.statusPollSeconds } });
  await user.click(screen.getByRole('button', { name: /register vms/i }));
}

describe('VMS navigation and routing', () => {
  it('does not render VMS navigation for a user without vms.read', () => {
    renderShell([]);

    expect(screen.queryByRole('link', { name: /vms/i })).not.toBeInTheDocument();
  });

  it('renders VMS navigation for a user with vms.read', () => {
    renderShell(['vms.read']);

    expect(screen.getByRole('link', { name: /vms/i })).toHaveAttribute('href', '/vms');
  });

  it('keeps the direct VMS route unavailable without vms.read', async () => {
    saveSession(sessionFixture('denied-user', ['camera.read']));
    vi.stubGlobal('fetch', vi.fn(async () => Response.json([])));

    render(<MemoryRouter initialEntries={['/vms']}><AuthProvider><App /></AuthProvider></MemoryRouter>);

    expect(await screen.findByRole('heading', { name: /action unavailable/i })).toBeVisible();
    expect(screen.queryByRole('heading', { name: /vms integrations/i })).not.toBeInTheDocument();
    expect(fetch).not.toHaveBeenCalled();
  });
});

describe('VmsPage', () => {
  it('exposes the exact persisted VMS identity and poll interval bounds on its controls', async () => {
    const user = userEvent.setup();
    renderVmsForm();

    expect(screen.getByLabelText(/vms code/i)).toHaveAttribute('maxlength', '100');
    expect(screen.getByLabelText(/display name/i)).toHaveAttribute('maxlength', '255');
    await user.click(screen.getByRole('button', { name: /connection tuning/i }));
    expect(screen.getByLabelText(/inventory poll seconds/i)).toHaveAttribute('min', '30');
    expect(screen.getByLabelText(/status poll seconds/i)).toHaveAttribute('min', '5');
  });

  it('submits values at the exact persisted VMS identity and poll interval bounds', async () => {
    const user = userEvent.setup();
    const onSubmit = renderVmsForm();

    await fillBoundedVmsForm(user, {
      code: 'C'.repeat(100),
      displayName: 'N'.repeat(255),
      inventoryPollSeconds: '30',
      statusPollSeconds: '5',
    });

    await waitFor(() => expect(onSubmit).toHaveBeenCalledWith(expect.objectContaining({
      code: 'C'.repeat(100),
      displayName: 'N'.repeat(255),
      inventoryPollSeconds: 30,
      statusPollSeconds: 5,
    })));
  });

  it.each([
    { field: 'code', value: 'C'.repeat(101), error: 'VMS code must be at most 100 characters.' },
    { field: 'displayName', value: 'N'.repeat(256), error: 'Display name must be at most 255 characters.' },
    { field: 'inventoryPollSeconds', value: '29', error: 'Inventory poll seconds must be 30 or more.' },
    { field: 'statusPollSeconds', value: '4', error: 'Status poll seconds must be 5 or more.' },
  ])('rejects an out-of-range persisted VMS $field value locally', async ({ field, value, error }) => {
    const user = userEvent.setup();
    const onSubmit = renderVmsForm();
    const values = {
      code: 'VMS-1',
      displayName: 'North NVR',
      inventoryPollSeconds: '30',
      statusPollSeconds: '5',
      [field]: value,
    };

    await fillBoundedVmsForm(user, values);

    expect(await screen.findByRole('alert')).toHaveTextContent(error);
    expect(onSubmit).not.toHaveBeenCalled();
  });

  it('shows discovery onboarding only to users who can import cameras', async () => {
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => {
      const path = new URL(String(input)).pathname;
      if (path === `/api/v1/vms/${vmsId}`) return Response.json(vms);
      if (path === `/api/v1/vms/${vmsId}/credential/status`) return Response.json({ reference: 'vms/north-nvr', exists: false });
      return new Response(null, { status: 404 });
    }));
    saveSession(sessionFixture('vms-import-user', ['vms.read', 'camera.import']));
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    queryClient.setQueryData(['vms', vmsId], vms);

    render(<MemoryRouter initialEntries={[`/vms/${vmsId}`]}><AuthProvider><QueryClientProvider client={queryClient}><Routes><Route path="/vms/:vmsId" element={<VmsPage />} /></Routes></QueryClientProvider></AuthProvider></MemoryRouter>);

    expect(await screen.findByRole('link', { name: /discover cameras/i })).toHaveAttribute('href', `/vms/${vmsId}/discovery`);
  });

  it('lists live VMS records as selectable links without exposing credential values', async () => {
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => {
      const path = new URL(String(input)).pathname;
      if (path === '/api/v1/vms') return Response.json([vms]);
      return new Response(null, { status: 404 });
    }));

    renderPage(['vms.read']);

    expect(await screen.findByRole('link', { name: /north nvr/i })).toHaveAttribute('href', `/vms/${vmsId}`);
    expect(screen.queryByRole('button', { name: /register vms/i })).not.toBeInTheDocument();
    expect(document.body).not.toHaveTextContent(/password|token/i);
  });

  it('registers a VMS with live organization, unit, and site selections', async () => {
    const requests: Array<{ path: string; method: string; body?: Record<string, unknown> }> = [];
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const path = new URL(String(input)).pathname;
      requests.push({
        path,
        method: init?.method ?? 'GET',
        body: typeof init?.body === 'string' ? JSON.parse(init.body) as Record<string, unknown> : undefined,
      });
      if (path === '/api/v1/organizations') return Response.json([organization]);
      if (path === `/api/v1/organizations/${organizationId}/units`) return Response.json([organizationUnit]);
      if (path === '/api/v1/sites') return Response.json([site]);
      if (path === '/api/v1/vms' && init?.method === 'POST') return Response.json({ id: vmsId }, { status: 201 });
      if (path === '/api/v1/vms') return Response.json([]);
      return new Response(null, { status: 404 });
    }));
    const user = userEvent.setup();

    renderPage(['vms.read', 'vms.create']);

    await user.type(await screen.findByLabelText(/vms code/i), ' NORTH-NVR ');
    await user.type(screen.getByLabelText(/display name/i), ' North NVR ');
    await user.selectOptions(screen.getByLabelText(/^organization$/i), organizationId);
    await user.selectOptions(await screen.findByLabelText(/organization unit/i), organizationUnitId);
    await user.selectOptions(screen.getByLabelText(/^site/i), siteId);
    await user.selectOptions(screen.getByLabelText(/^vendor/i), 'DahuaCgi');
    await user.type(screen.getByLabelText(/^endpoint/i), ' https://nvr.example.test ');
    await user.type(screen.getByLabelText(/credential reference/i), ' vms/north-nvr ');
    await user.click(screen.getByRole('button', { name: /connection tuning/i }));
    await user.type(screen.getByLabelText(/rate limit per second/i), '2.5');
    await user.type(screen.getByLabelText(/inventory poll seconds/i), '600');
    await user.type(screen.getByLabelText(/expected camera count/i), '24');
    await user.click(screen.getByRole('button', { name: /register vms/i }));

    await waitFor(() => expect(requests.some((request) => request.method === 'POST')).toBe(true));
    const create = requests.find((request) => request.path === '/api/v1/vms' && request.method === 'POST');
    expect(create?.body).toEqual(expect.objectContaining({
      code: 'NORTH-NVR',
      organizationUnitId,
      siteId,
      displayName: 'North NVR',
      vendor: 'DahuaCgi',
      endpoint: 'https://nvr.example.test',
      credentialReference: 'vms/north-nvr',
      verifyTls: true,
      rateLimitPerSecond: 2.5,
      inventoryPollSeconds: 600,
      expectedCameraCount: 24,
    }));
    expect(await screen.findByRole('link', { name: /continue to credentials/i })).toHaveAttribute('href', `/vms/${vmsId}`);
  });

  it('clears target-local secrets and status when the detail route changes', async () => {
    let firstTargetPolls = 0;
    const secondVms = { ...vms, id: secondVmsId, code: 'SOUTH-NVR', displayName: 'South NVR', credentialReference: 'vms/south-nvr' };
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const path = new URL(String(input)).pathname;
      if (path === `/api/v1/vms/${vmsId}`) return Response.json(vms);
      if (path === `/api/v1/vms/${secondVmsId}`) return Response.json(secondVms);
      if (path === `/api/v1/vms/${vmsId}/credential/status`) return Response.json({ reference: 'vms/north-nvr', exists: false });
      if (path === `/api/v1/vms/${secondVmsId}/credential/status`) return Response.json({ reference: 'vms/south-nvr', exists: false });
      if (path === `/api/v1/vms/${vmsId}/credential` && init?.method === 'PUT') {
        return Response.json({ credentialReference: 'vms/north-nvr', updatedAt: '2026-09-04T10:30:00Z' });
      }
      if (path === `/api/v1/vms/${vmsId}/test` && init?.method === 'POST') {
        return Response.json({ testId: connectionTestId, status: 'pending', statusUrl: `${path}/${connectionTestId}` }, { status: 202 });
      }
      if (path === `/api/v1/vms/${vmsId}/test/${connectionTestId}`) {
        firstTargetPolls += 1;
        return Response.json({ testId: connectionTestId, targetId: vmsId, status: 'pending', requestedAt: '2026-09-04T10:30:00Z', completedAt: null, failureReason: null, result: null });
      }
      if (path === `/api/v1/vms/${secondVmsId}/test/${connectionTestId}`) {
        return Response.json({ testId: connectionTestId, targetId: secondVmsId, status: 'pending', requestedAt: '2026-09-04T10:30:00Z', completedAt: null, failureReason: null, result: null });
      }
      return new Response(null, { status: 404 });
    }));
    saveSession(sessionFixture('vms-route-user', ['vms.read', 'credential.write', 'integration.manage']));
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    queryClient.setQueryData(['vms', vmsId], vms);
    queryClient.setQueryData(['vms', secondVmsId], secondVms);
    const user = userEvent.setup();

    render(<MemoryRouter initialEntries={[`/vms/${vmsId}`]}><RouteSwitcher /><AuthProvider><QueryClientProvider client={queryClient}><Routes><Route path="/vms/:vmsId" element={<VmsPage />} /></Routes></QueryClientProvider></AuthProvider></MemoryRouter>);

    await user.type(await screen.findByLabelText(/^password/i), 'first-target-secret');
    await user.click(screen.getByRole('button', { name: /save credential/i }));
    expect(await screen.findByText(/^credential set$/i)).toBeVisible();
    expect(screen.getByText(/credential updated/i)).toBeVisible();
    await user.type(screen.getByLabelText(/^password/i), 'unsaved-first-target-secret');
    await user.click(screen.getByRole('button', { name: /test connection/i }));
    await waitFor(() => expect(firstTargetPolls).toBe(1));

    await user.click(screen.getByRole('button', { name: /open second vms/i }));

    expect(await screen.findByRole('heading', { name: 'South NVR' })).toBeVisible();
    expect(await screen.findByText(/^credential not set$/i)).toBeVisible();
    expect(screen.getByLabelText(/^password/i)).toHaveValue('');
    expect(screen.queryByText(/credential updated/i)).not.toBeInTheDocument();
    expect(screen.queryByText(/connection test pending/i)).not.toBeInTheDocument();
    expect(vi.mocked(fetch).mock.calls.some(([input]) => new URL(String(input)).pathname === `/api/v1/vms/${secondVmsId}/test/${connectionTestId}`)).toBe(false);
  });
});
