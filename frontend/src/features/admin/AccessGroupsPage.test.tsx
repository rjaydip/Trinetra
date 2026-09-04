import '@testing-library/jest-dom/vitest';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { AuthProvider } from '../../auth/AuthProvider';
import { saveSession } from '../../auth/session';
import { sessionFixture } from '../../test/fixtures';
import { AccessGroupsPage } from './AccessGroupsPage';

const groupId = '20000000-0000-4000-8000-000000000001';
const roleId = '20000000-0000-4000-8000-000000000002';
const scopeId = '20000000-0000-4000-8000-000000000003';
const unitId = '20000000-0000-4000-8000-000000000004';
const areaId = '20000000-0000-4000-8000-000000000005';

const group = {
  id: groupId, code: 'OPERATORS', name: 'Operators', description: 'Camera operators', status: 'ACTIVE', roleCode: 'VIEWER',
  permissions: ['camera.read'], memberCount: 1,
  scopes: [{ id: scopeId, scopeType: 'ORGANIZATION', organizationUnitId: unitId, geographicAreaId: null, resourceType: null, resourceId: null, description: 'Headquarters' }],
};

afterEach(() => {
  vi.unstubAllGlobals();
  sessionStorage.clear();
});

function renderGroups(permissions: string[]) {
  saveSession(sessionFixture('group-admin', permissions));
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <MemoryRouter>
      <AuthProvider>
        <QueryClientProvider client={queryClient}>
          <AccessGroupsPage />
        </QueryClientProvider>
      </AuthProvider>
    </MemoryRouter>,
  );
}

function groupsFetch() {
  return vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = new URL(String(input));
    if (url.pathname === '/api/v1/access-groups' && init?.method === 'POST') return Response.json({ id: groupId }, { status: 201 });
    if (url.pathname === '/api/v1/access-groups') return Response.json([group]);
    if (url.pathname === `/api/v1/access-groups/${groupId}`) return Response.json(group);
    if (url.pathname === `/api/v1/access-groups/${groupId}/members`) return Response.json([{ userId: 'u1', username: 'ravi', expiresAt: null }]);
    if (url.pathname === `/api/v1/access-groups/${groupId}/scopes` && init?.method === 'POST') return Response.json({ id: 'new-scope' }, { status: 201 });
    if (url.pathname === `/api/v1/access-groups/${groupId}/scopes/${scopeId}` && init?.method === 'DELETE') return new Response(null, { status: 204 });
    if (url.pathname === '/api/v1/roles') return Response.json([{ id: roleId, code: 'VIEWER', name: 'Viewer', description: null, isSystem: true }]);
    if (url.pathname === '/api/v1/organizations') return Response.json([{ id: 'org-1', code: 'OPS', name: 'Operations', organizationType: 'AGENCY', description: null, status: 'ACTIVE' }]);
    if (url.pathname === '/api/v1/organizations/org-1/units') return Response.json([{ id: unitId, organizationId: 'org-1', parentUnitId: null, code: 'HQ', name: 'Headquarters', unitType: 'DEPARTMENT', status: 'ACTIVE' }]);
    if (url.pathname === '/api/v1/geographic-areas') return Response.json([{ id: areaId, parentAreaId: null, code: 'NORTH', name: 'North zone', areaType: 'ZONE', status: 'ACTIVE' }]);
    return new Response(null, { status: 404 });
  });
}

describe('AccessGroupsPage', () => {
  it('shows group detail, permissions, scopes, and members to group.read users without mutation controls', async () => {
    vi.stubGlobal('fetch', groupsFetch());
    const user = userEvent.setup();
    renderGroups(['group.read']);

    await user.click(await screen.findByRole('button', { name: /view operators/i }));

    expect(await screen.findByText('ravi')).toBeVisible();
    expect(screen.getByText('camera.read')).toBeVisible();
    expect(screen.getByText('Headquarters')).toBeVisible();
    expect(screen.queryByRole('button', { name: /create access group/i })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /add scope/i })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /remove scope/i })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /delete group|edit group|add member|remove member/i })).not.toBeInTheDocument();
  });

  it('supports only create and scope mutations for group.manage users', async () => {
    vi.stubGlobal('fetch', groupsFetch());
    const user = userEvent.setup();
    renderGroups(['group.read', 'group.manage']);

    const createForm = await screen.findByRole('form', { name: /create access group/i });
    expect(await within(createForm).findByRole('option', { name: /viewer/i })).toHaveValue(roleId);
    await user.click(screen.getByRole('button', { name: /view operators/i }));
    expect(await screen.findByRole('button', { name: /remove scope headquarters/i })).toBeVisible();
    expect(screen.getByRole('form', { name: /add access-group scope/i })).toBeVisible();
    expect(screen.queryByRole('button', { name: /delete group|edit group|add member|remove member/i })).not.toBeInTheDocument();
  });

  it('adds an organization scope with a live organization-unit reference', async () => {
    const fetch = groupsFetch();
    vi.stubGlobal('fetch', fetch);
    const user = userEvent.setup();
    renderGroups(['group.read', 'group.manage']);

    await user.click(await screen.findByRole('button', { name: /view operators/i }));
    const form = await screen.findByRole('form', { name: /add access-group scope/i });
    await user.selectOptions(within(form).getByLabelText(/^scope type$/i), 'ORGANIZATION');
    await user.selectOptions(within(form).getByLabelText(/^organization$/i), 'org-1');
    await user.selectOptions(await within(form).findByLabelText(/organization unit/i), unitId);
    await user.click(within(form).getByRole('button', { name: /add scope/i }));

    await waitFor(() => expect(fetch).toHaveBeenCalledWith(
      expect.stringContaining(`/api/v1/access-groups/${groupId}/scopes`),
      expect.objectContaining({ method: 'POST', body: JSON.stringify({ scopeType: 'ORGANIZATION', organizationUnitId: unitId }) }),
    ));
  });
});
