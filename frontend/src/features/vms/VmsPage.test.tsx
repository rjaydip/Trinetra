import '@testing-library/jest-dom/vitest';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { App } from '../../App';
import { AuthProvider } from '../../auth/AuthProvider';
import { saveSession } from '../../auth/session';
import { AppShell } from '../../components/AppShell';
import { sessionFixture } from '../../test/fixtures';
import { VmsPage } from './VmsPage';

const organizationId = '11111111-1111-4111-8111-111111111111';
const organizationUnitId = '22222222-2222-4222-8222-222222222222';
const siteId = '33333333-3333-4333-8333-333333333333';
const vmsId = '44444444-4444-4444-8444-444444444444';

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
});
