import '@testing-library/jest-dom/vitest';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { AuthProvider } from '../../auth/AuthProvider';
import { saveSession } from '../../auth/session';
import { pageEnvelope, sessionFixture } from '../../test/fixtures';
import { HierarchyPage } from './HierarchyPage';

const organizationId = '10000000-0000-4000-8000-000000000001';
const unitId = '10000000-0000-4000-8000-000000000002';
const areaId = '10000000-0000-4000-8000-000000000003';
const newParentId = '10000000-0000-4000-8000-000000000004';
const secondOrganizationId = '10000000-0000-4000-8000-000000000005';
const secondOrganizationUnitId = '10000000-0000-4000-8000-000000000006';
const inactiveUnitId = '10000000-0000-4000-8000-000000000007';
const inactiveAreaId = '10000000-0000-4000-8000-000000000008';

const organizations = [{
  id: organizationId, code: 'POLICE', name: 'State Police', organizationType: 'AGENCY', description: null, status: 'ACTIVE',
}, {
  id: secondOrganizationId, code: 'FIRE', name: 'Fire Department', organizationType: 'AGENCY', description: null, status: 'ACTIVE',
}];
const units = [{
  id: unitId, organizationId, parentUnitId: null, code: 'HQ', name: 'Headquarters', unitType: 'DEPARTMENT', status: 'ACTIVE',
}];
const level2Id = '10000000-0000-4000-8000-000000000009';
const level3Id = '10000000-0000-4000-8000-00000000000a';
const level4Id = '10000000-0000-4000-8000-00000000000b';
const deepUnits = [
  { id: unitId, organizationId, parentUnitId: null, code: 'HQ', name: 'Headquarters', unitType: 'DEPARTMENT', status: 'ACTIVE' },
  { id: level2Id, organizationId, parentUnitId: unitId, code: 'ZONE-A', name: 'Zone A', unitType: 'ZONE', status: 'ACTIVE' },
  { id: level3Id, organizationId, parentUnitId: level2Id, code: 'STN-A1', name: 'Station A1', unitType: 'STATION', status: 'ACTIVE' },
  { id: level4Id, organizationId, parentUnitId: level3Id, code: 'BEAT-A1A', name: 'Beat A1A', unitType: 'BEAT', status: 'ACTIVE' },
];
const unitsWithInactive = [
  ...units,
  { id: inactiveUnitId, organizationId, parentUnitId: null, code: 'RETIRED', name: 'Retired Precinct', unitType: 'DEPARTMENT', status: 'INACTIVE' },
];
const secondOrganizationUnits = [{
  id: secondOrganizationUnitId, organizationId: secondOrganizationId, parentUnitId: null, code: 'FHQ', name: 'Fire Headquarters', unitType: 'DEPARTMENT', status: 'ACTIVE',
}];
const areas = [
  { id: areaId, parentAreaId: null, code: 'NORTH', name: 'North zone', areaType: 'ZONE', status: 'ACTIVE' },
  { id: newParentId, parentAreaId: null, code: 'SOUTH', name: 'South zone', areaType: 'ZONE', status: 'ACTIVE' },
];
const areasWithInactive = [
  ...areas,
  { id: inactiveAreaId, parentAreaId: null, code: 'EAST', name: 'East zone', areaType: 'ZONE', status: 'INACTIVE' },
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

function hierarchyFetch(options: { areaConflict?: boolean; moveConflict?: boolean; includeInactive?: boolean; unitConflict?: boolean; deep?: boolean } = {}) {
  let deactivationAttempts = 0;
  let unitDeactivationAttempts = 0;
  return vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = new URL(String(input));
    if (url.pathname === `/api/v1/organizations/${organizationId}/units` && init?.method === 'POST') {
      return Response.json({ id: '10000000-0000-4000-8000-0000000000ff' }, { status: 201 });
    }
    if (url.pathname === '/api/v1/organizations' && init?.method === 'PUT') return Response.json(organizations[0]);
    if (url.pathname === `/api/v1/organizations/${organizationId}` && init?.method === 'PUT') return Response.json(organizations[0]);
    if (url.pathname === `/api/v1/organization-units/${unitId}` && init?.method === 'PUT') return Response.json(units[0]);
    if (url.pathname === `/api/v1/organization-units/${unitId}/move` && init?.method === 'POST') {
      const confirmed = (JSON.parse(String(init.body)) as { confirmScopeImpact?: boolean }).confirmScopeImpact;
      if (options.moveConflict && !confirmed) {
        return Response.json({
          title: 'Move affects existing access-group scopes',
          detail: 'Access groups have an organization scope pointing into this subtree.',
        }, { status: 409 });
      }
      return Response.json({
        fromOrganizationId: organizationId, toOrganizationId: secondOrganizationId, subtreeSize: 1,
        camerasFollowing: 0, targetsFollowing: 0, affectedGroups: [],
      });
    }
    if (url.pathname === `/api/v1/geographic-areas/${areaId}` && init?.method === 'PUT') return Response.json(areas[0]);
    if (url.pathname === '/api/v1/organizations') return Response.json(pageEnvelope(organizations));
    if (url.pathname === `/api/v1/organizations/${organizationId}/units`) return Response.json(pageEnvelope(options.deep ? deepUnits : options.includeInactive ? unitsWithInactive : units));
    if (url.pathname === `/api/v1/organizations/${secondOrganizationId}/units`) return Response.json(pageEnvelope(secondOrganizationUnits));
    if (url.pathname === '/api/v1/geographic-areas/types') return Response.json([{ code: 'ZONE', name: 'Zone', levelOrder: 1 }]);
    if (url.pathname === '/api/v1/geographic-areas') return Response.json(pageEnvelope(options.includeInactive ? areasWithInactive : areas));
    if (url.pathname === `/api/v1/organization-units/${unitId}/deactivate` && init?.method === 'POST') {
      unitDeactivationAttempts += 1;
      if (options.unitConflict && unitDeactivationAttempts === 1) {
        return Response.json({ title: 'Children must be handled', detail: 'Choose cascade or reparent.' }, { status: 409 });
      }
      return new Response(null, { status: 204 });
    }
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
  it('uses live organizations and areas in the unit and geographic area forms', async () => {
    const user = userEvent.setup();
    vi.stubGlobal('fetch', hierarchyFetch());
    renderHierarchy(['organization.read', 'organization.manage', 'geography.read', 'geography.manage']);

    const unitForm = await screen.findByRole('form', { name: /create organization unit/i });
    expect(await within(unitForm).findByRole('option', { name: /state police/i })).toHaveValue(organizationId);

    await user.click(screen.getByRole('tab', { name: /^geographic$/i }));
    const areaForm = await screen.findByRole('form', { name: /create geographic area/i });
    expect(await within(areaForm).findByRole('option', { name: /north zone/i })).toHaveValue(areaId);
  });

  it('switches between the organization and geography tabs', async () => {
    const user = userEvent.setup();
    vi.stubGlobal('fetch', hierarchyFetch());
    renderHierarchy(['organization.read', 'organization.manage', 'geography.read', 'geography.manage']);

    await screen.findByRole('heading', { name: /organizations & units/i });
    expect(screen.queryByRole('heading', { name: /^geography$/i })).not.toBeInTheDocument();

    await user.click(screen.getByRole('tab', { name: /^geographic$/i }));
    expect(await screen.findByRole('heading', { name: /^geography$/i })).toBeVisible();
    expect(screen.queryByRole('heading', { name: /organizations & units/i })).not.toBeInTheDocument();
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

describe('HierarchyPage inactive-node handling', () => {
  it('offers Activate instead of Edit for an inactive unit, and excludes it from parent pickers', async () => {
    vi.stubGlobal('fetch', hierarchyFetch({ includeInactive: true }));
    const user = userEvent.setup();
    renderHierarchy(['organization.read', 'organization.manage']);

    const unitSelector = await screen.findByLabelText(/^select unit$/i);
    await within(unitSelector).findByRole('option', { name: /retired precinct/i });
    await user.selectOptions(unitSelector, inactiveUnitId);

    expect(await screen.findByRole('button', { name: /^activate unit$/i })).toBeVisible();
    expect(screen.queryByRole('button', { name: /^edit unit$/i })).not.toBeInTheDocument();

    // Switch to the active unit and open its edit form: the inactive sibling must not appear
    // as a selectable new parent, since the backend refuses a non-ACTIVE parent (400).
    await user.selectOptions(screen.getByLabelText(/^select unit$/i), unitId);
    await user.click(await screen.findByRole('button', { name: /^edit unit$/i }));
    const editForm = await screen.findByRole('form', { name: /edit unit headquarters/i });
    expect(within(editForm).queryByRole('option', { name: /retired precinct/i })).not.toBeInTheDocument();
  });

  it('offers Activate instead of Edit for an inactive geographic area, and excludes it from parent pickers', async () => {
    vi.stubGlobal('fetch', hierarchyFetch({ includeInactive: true }));
    const user = userEvent.setup();
    renderHierarchy(['geography.read', 'geography.manage']);

    await user.selectOptions(await screen.findByLabelText(/^select geographic area$/i), inactiveAreaId);

    expect(await screen.findByRole('button', { name: /^activate area$/i })).toBeVisible();
    expect(screen.queryByRole('button', { name: /^edit area$/i })).not.toBeInTheDocument();

    await user.selectOptions(screen.getByLabelText(/^select geographic area$/i), areaId);
    await user.click(await screen.findByRole('button', { name: /^edit area$/i }));
    const editForm = await screen.findByRole('form', { name: /edit geographic area north zone/i });
    expect(within(editForm).queryByRole('option', { name: /east zone/i })).not.toBeInTheDocument();
  });

  it('excludes an inactive unit from the deactivation reparent picker', async () => {
    vi.stubGlobal('fetch', hierarchyFetch({ includeInactive: true, unitConflict: true }));
    const user = userEvent.setup();
    renderHierarchy(['organization.read', 'organization.manage']);

    await user.click(await screen.findByRole('button', { name: /deactivate unit headquarters/i }));
    const conflict = (await screen.findByText(/choose cascade or reparent/i)).closest('fieldset')!;
    await user.click(within(conflict).getByRole('radio', { name: /^reparent$/i }));

    expect(within(conflict).queryByRole('option', { name: /retired precinct/i })).not.toBeInTheDocument();
  });

  it('excludes an inactive unit from the create-unit parent picker', async () => {
    vi.stubGlobal('fetch', hierarchyFetch({ includeInactive: true }));
    renderHierarchy(['organization.read', 'organization.manage']);

    const unitForm = await screen.findByRole('form', { name: /create organization unit/i });
    await within(unitForm).findByRole('option', { name: /^headquarters$/i });
    expect(within(unitForm).queryByRole('option', { name: /retired precinct/i })).not.toBeInTheDocument();
  });
});

describe('HierarchyPage cross-organization move', () => {
  it('moves a unit into another organization once a destination and parent are chosen', async () => {
    const fetch = hierarchyFetch();
    vi.stubGlobal('fetch', fetch);
    const user = userEvent.setup();
    renderHierarchy(['organization.read', 'organization.manage']);

    await user.click(await screen.findByRole('button', { name: /move headquarters to another organization/i }));
    await user.selectOptions(screen.getByLabelText(/destination organization/i), secondOrganizationId);
    await user.selectOptions(await screen.findByLabelText(/new parent unit/i), secondOrganizationUnitId);
    await user.click(screen.getByRole('button', { name: /^move unit$/i }));

    await waitFor(() => {
      const moveCall = fetch.mock.calls.find(([input, init]) => (
        String(input).includes(`/organization-units/${unitId}/move`) && (init as RequestInit | undefined)?.method === 'POST'
      ));
      expect(moveCall).toBeDefined();
      expect(JSON.parse(String((moveCall![1] as RequestInit).body))).toEqual({
        newParentUnitId: secondOrganizationUnitId, confirmScopeImpact: false,
      });
    });
  });

  it('requires explicit confirmation after a scope-impact conflict before retrying the move', async () => {
    const fetch = hierarchyFetch({ moveConflict: true });
    vi.stubGlobal('fetch', fetch);
    const user = userEvent.setup();
    renderHierarchy(['organization.read', 'organization.manage']);

    await user.click(await screen.findByRole('button', { name: /move headquarters to another organization/i }));
    await user.selectOptions(screen.getByLabelText(/destination organization/i), secondOrganizationId);
    await user.selectOptions(await screen.findByLabelText(/new parent unit/i), secondOrganizationUnitId);
    await user.click(screen.getByRole('button', { name: /^move unit$/i }));

    expect(await screen.findByText(/access groups have an organization scope/i)).toBeVisible();
    const confirmButton = screen.getByRole('button', { name: /confirm move/i });
    expect(confirmButton).toBeDisabled();

    await user.click(screen.getByRole('checkbox', { name: /understand.*move anyway/i }));
    await user.click(confirmButton);

    await waitFor(() => {
      const moveCalls = fetch.mock.calls.filter(([input, init]) => (
        String(input).includes(`/organization-units/${unitId}/move`) && (init as RequestInit | undefined)?.method === 'POST'
      ));
      expect(moveCalls).toHaveLength(2);
      expect(JSON.parse(String((moveCalls[1][1] as RequestInit).body))).toEqual({
        newParentUnitId: secondOrganizationUnitId, confirmScopeImpact: true,
      });
    });
  });
});

describe('HierarchyPage tree depth and inline add', () => {
  it('shows only the first 2-3 levels by default and expands a deeper level on demand', async () => {
    vi.stubGlobal('fetch', hierarchyFetch({ deep: true }));
    renderHierarchy(['organization.read', 'organization.manage']);

    const tree = await screen.findByRole('tree', { name: /organization units tree/i });
    await within(tree).findByText('Headquarters');
    within(tree).getByText('Zone A');
    within(tree).getByText('Station A1');
    expect(within(tree).queryByText('Beat A1A')).not.toBeInTheDocument();

    const user = userEvent.setup();
    await user.click(within(tree).getByRole('button', { name: /expand station a1/i }));

    expect(await within(tree).findByText('Beat A1A')).toBeVisible();
  });

  it('adds a child unit directly from the tree via the + control', async () => {
    const fetch = hierarchyFetch({ deep: true });
    vi.stubGlobal('fetch', fetch);
    const user = userEvent.setup();
    renderHierarchy(['organization.read', 'organization.manage']);

    const tree = await screen.findByRole('tree', { name: /organization units tree/i });
    await within(tree).findByText('Zone A');
    await user.click(within(tree).getByRole('button', { name: /add a unit under zone a/i }));

    const addForm = await screen.findByRole('form', { name: /add child unit/i });
    await user.type(within(addForm).getByLabelText(/^code$/i), 'STN-A2');
    await user.type(within(addForm).getByLabelText(/^name$/i), 'Station A2');
    await user.type(within(addForm).getByLabelText(/^unit type$/i), 'STATION');
    await user.click(within(addForm).getByRole('button', { name: /^add unit$/i }));

    await waitFor(() => {
      const createCall = fetch.mock.calls.find(([input, init]) => (
        String(input).includes(`/organizations/${organizationId}/units`) && (init as RequestInit | undefined)?.method === 'POST'
      ));
      expect(createCall).toBeDefined();
      expect(JSON.parse(String((createCall![1] as RequestInit).body))).toEqual({
        code: 'STN-A2', name: 'Station A2', unitType: 'STATION', parentUnitId: level2Id, organizationId,
      });
    });
  });
});
