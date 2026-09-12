import '@testing-library/jest-dom/vitest';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { AuthProvider } from '../../auth/AuthProvider';
import { saveSession } from '../../auth/session';
import { sessionFixture } from '../../test/fixtures';
import { HierarchyPage } from './HierarchyPage';

const organizationId = '10000000-0000-4000-8000-000000000001';
const unitId = '10000000-0000-4000-8000-000000000002';
const areaId = '10000000-0000-4000-8000-000000000003';
const newParentId = '10000000-0000-4000-8000-000000000004';

const organizations = [{
  id: organizationId, code: 'POLICE', name: 'State Police', organizationType: 'AGENCY', description: null, status: 'ACTIVE',
}];
const units = [{
  id: unitId, organizationId, parentUnitId: null, code: 'HQ', name: 'Headquarters', unitType: 'DEPARTMENT', status: 'ACTIVE',
}];
const areas = [
  { id: areaId, parentAreaId: null, code: 'NORTH', name: 'North zone', areaType: 'ZONE', status: 'ACTIVE' },
  { id: newParentId, parentAreaId: null, code: 'SOUTH', name: 'South zone', areaType: 'ZONE', status: 'ACTIVE' },
];

afterEach(() => {
  vi.unstubAllGlobals();
  sessionStorage.clear();
});

function renderHierarchy(permissions: string[]) {
  saveSession(sessionFixture('hierarchy-admin', permissions));
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <MemoryRouter>
      <AuthProvider>
        <QueryClientProvider client={queryClient}>
          <HierarchyPage />
        </QueryClientProvider>
      </AuthProvider>
    </MemoryRouter>,
  );
}

function hierarchyFetch(options: { areaConflict?: boolean } = {}) {
  let deactivationAttempts = 0;
  return vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = new URL(String(input));
    if (url.pathname === '/api/v1/organizations' && init?.method === 'PUT') return Response.json(organizations[0]);
    if (url.pathname === `/api/v1/organizations/${organizationId}` && init?.method === 'PUT') return Response.json(organizations[0]);
    if (url.pathname === `/api/v1/organization-units/${unitId}` && init?.method === 'PUT') return Response.json(units[0]);
    if (url.pathname === `/api/v1/geographic-areas/${areaId}` && init?.method === 'PUT') return Response.json(areas[0]);
    if (url.pathname === '/api/v1/organizations') return Response.json(organizations);
    if (url.pathname === `/api/v1/organizations/${organizationId}/units`) return Response.json(units);
    if (url.pathname === '/api/v1/geographic-areas/types') return Response.json([{ code: 'ZONE', name: 'Zone', levelOrder: 1 }]);
    if (url.pathname === '/api/v1/geographic-areas') return Response.json(areas);
    if (url.pathname === '/api/v1/sites') return Response.json([]);
    if (url.pathname === `/api/v1/geographic-areas/${areaId}/deactivate` && init?.method === 'POST') {
      deactivationAttempts += 1;
      if (options.areaConflict && deactivationAttempts === 1) {
        return Response.json({ title: 'Children must be handled', detail: 'Choose cascade or reparent.' }, { status: 409 });
      }
      return new Response(null, { status: 204 });
    }
    return new Response(null, { status: 404 });
  });
}

describe('HierarchyPage permission-aware actions', () => {
  it('shows create organization only to organization.manage users', async () => {
    vi.stubGlobal('fetch', hierarchyFetch());
    renderHierarchy(['organization.read']);

    expect((await screen.findAllByText('State Police')).length).toBeGreaterThan(0);
    expect(screen.queryByRole('button', { name: /create organization/i })).not.toBeInTheDocument();
  });

  it('does not expose geography mutations to geography.read users', async () => {
    vi.stubGlobal('fetch', hierarchyFetch());
    renderHierarchy(['geography.read']);

    expect(await screen.findByText('North zone')).toBeVisible();
    expect(screen.queryByRole('button', { name: /create geographic area/i })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /deactivate area/i })).not.toBeInTheDocument();
  });
});

describe('HierarchyPage deactivation conflicts', () => {
  it('requires an explicit child strategy after a hierarchy deactivation conflict', async () => {
    const fetch = hierarchyFetch({ areaConflict: true });
    vi.stubGlobal('fetch', fetch);
    const user = userEvent.setup();
    renderHierarchy(['geography.manage']);

    await user.click(await screen.findByRole('button', { name: /deactivate area north zone/i }));

    expect(await screen.findByText(/choose cascade or reparent/i)).toBeVisible();
    expect(screen.getByRole('radio', { name: /^cascade$/i })).toBeVisible();
    expect(screen.getByRole('radio', { name: /^reparent$/i })).toBeVisible();
    expect(screen.getByRole('button', { name: /confirm deactivation/i })).toBeDisabled();

    const firstDeactivate = fetch.mock.calls.find(([input, init]) => String(input).includes(`/geographic-areas/${areaId}/deactivate`) && (init as RequestInit | undefined)?.method === 'POST');
    expect(JSON.parse(String((firstDeactivate?.[1] as RequestInit).body))).toEqual({});
  });

  it('cannot repeat an unstrategized deactivation after a conflict', async () => {
    const fetch = hierarchyFetch({ areaConflict: true });
    vi.stubGlobal('fetch', fetch);
    const user = userEvent.setup();
    renderHierarchy(['geography.manage']);

    const deactivateArea = await screen.findByRole('button', { name: /deactivate area north zone/i });
    await user.click(deactivateArea);
    await screen.findByText(/choose cascade or reparent/i);

    expect(deactivateArea).toBeDisabled();
    await user.click(deactivateArea);
    const calls = fetch.mock.calls.filter(([input, init]) => String(input).includes(`/geographic-areas/${areaId}/deactivate`) && (init as RequestInit | undefined)?.method === 'POST');
    expect(calls).toHaveLength(1);
  });

  it('requires and sends a new parent when reparent is chosen', async () => {
    const fetch = hierarchyFetch({ areaConflict: true });
    vi.stubGlobal('fetch', fetch);
    const user = userEvent.setup();
    renderHierarchy(['geography.read', 'geography.manage']);

    await user.click(await screen.findByRole('button', { name: /deactivate area north zone/i }));
    await screen.findByText(/choose cascade or reparent/i);
    await user.click(screen.getByRole('radio', { name: /^reparent$/i }));
    expect(screen.getByRole('button', { name: /confirm deactivation/i })).toBeDisabled();
    await user.selectOptions(screen.getByLabelText(/new parent area/i), newParentId);
    await user.click(screen.getByRole('button', { name: /confirm deactivation/i }));

    await waitFor(() => {
      const calls = fetch.mock.calls.filter(([input, init]) => String(input).includes(`/geographic-areas/${areaId}/deactivate`) && (init as RequestInit | undefined)?.method === 'POST');
      expect(calls).toHaveLength(2);
      expect(JSON.parse(String((calls[1][1] as RequestInit).body))).toEqual({ childStrategy: 'reparent', newParentId });
    });
  });

  it('sends cascade only after the user deliberately selects it', async () => {
    const fetch = hierarchyFetch({ areaConflict: true });
    vi.stubGlobal('fetch', fetch);
    const user = userEvent.setup();
    renderHierarchy(['geography.read', 'geography.manage']);

    await user.click(await screen.findByRole('button', { name: /deactivate area north zone/i }));
    await screen.findByText(/choose cascade or reparent/i);
    await user.click(screen.getByRole('radio', { name: /^cascade$/i }));
    await user.click(screen.getByRole('button', { name: /confirm deactivation/i }));

    await waitFor(() => {
      const calls = fetch.mock.calls.filter(([input, init]) => String(input).includes(`/geographic-areas/${areaId}/deactivate`) && (init as RequestInit | undefined)?.method === 'POST');
      expect(JSON.parse(String((calls[1][1] as RequestInit).body))).toEqual({ childStrategy: 'cascade' });
    });
  });
});

describe('HierarchyPage supported creation', () => {
  it('uses live organizations and areas in the unit and site forms', async () => {
    vi.stubGlobal('fetch', hierarchyFetch());
    renderHierarchy(['organization.read', 'organization.manage', 'geography.read', 'geography.manage']);

    const unitForm = await screen.findByRole('form', { name: /create organization unit/i });
    expect(await within(unitForm).findByRole('option', { name: /state police/i })).toHaveValue(organizationId);
    const siteForm = screen.getByRole('form', { name: /create site/i });
    expect(await within(siteForm).findByRole('option', { name: /north zone/i })).toHaveValue(areaId);
  });
});

describe('HierarchyPage editing and tree navigation', () => {
  it('allows editing an organization for organization.manage users', async () => {
    const fetch = hierarchyFetch();
    vi.stubGlobal('fetch', fetch);
    const user = userEvent.setup();
    renderHierarchy(['organization.read', 'organization.manage']);

    const editOrgBtn = await screen.findByRole('button', { name: /edit organization/i });
    await user.click(editOrgBtn);

    const editForm = await screen.findByRole('form', { name: /edit organization/i });
    expect(within(editForm).getByLabelText(/^name$/i)).toHaveValue('State Police');

    await user.clear(within(editForm).getByLabelText(/^name$/i));
    await user.type(within(editForm).getByLabelText(/^name$/i), 'Gujarat Police');
    await user.click(within(editForm).getByRole('button', { name: /save changes/i }));

    await waitFor(() => {
      const putCalls = fetch.mock.calls.filter(([input, init]) => (
        String(input).includes(`/organizations/${organizationId}`) && (init as RequestInit | undefined)?.method === 'PUT'
      ));
      expect(putCalls.length).toBeGreaterThan(0);
      const sent = JSON.parse(String((putCalls[0][1] as RequestInit).body));
      expect(sent.name).toBe('Gujarat Police');
    });
  });

  it('allows editing an organization unit for organization.manage users', async () => {
    const fetch = hierarchyFetch();
    vi.stubGlobal('fetch', fetch);
    const user = userEvent.setup();
    renderHierarchy(['organization.read', 'organization.manage']);

    const editUnitBtn = await screen.findByRole('button', { name: /edit unit/i });
    await user.click(editUnitBtn);

    const editForm = await screen.findByRole('form', { name: /edit unit headquarters/i });
    expect(within(editForm).getByLabelText(/^name$/i)).toHaveValue('Headquarters');

    await user.clear(within(editForm).getByLabelText(/^name$/i));
    await user.type(within(editForm).getByLabelText(/^name$/i), 'Main HQ');
    await user.click(within(editForm).getByRole('button', { name: /save changes/i }));

    await waitFor(() => {
      const putCalls = fetch.mock.calls.filter(([input, init]) => (
        String(input).includes(`/organization-units/${unitId}`) && (init as RequestInit | undefined)?.method === 'PUT'
      ));
      expect(putCalls.length).toBeGreaterThan(0);
      const sent = JSON.parse(String((putCalls[0][1] as RequestInit).body));
      expect(sent.name).toBe('Main HQ');
    });
  });
});
