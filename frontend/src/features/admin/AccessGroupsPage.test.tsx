import '@testing-library/jest-dom/vitest';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { AuthProvider } from '../../auth/AuthProvider';
import { saveSession } from '../../auth/session';
import { pageEnvelope, sessionFixture } from '../../test/fixtures';
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

const draftGroupId = '20000000-0000-4000-8000-000000000006';
const draftGroup = { ...group, id: draftGroupId, code: 'DRAFT_GROUP', name: 'Draft Group', status: 'DRAFT', scopes: [] };

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
    if (url.pathname === '/api/v1/access-groups') return Response.json({ items: [group], page: 1, pageSize: 20, total: 1, totalPages: 1 });
    if (url.pathname === `/api/v1/access-groups/${groupId}` && init?.method === 'PUT') return Response.json({ ...group, name: 'Senior Operators' });
    if (url.pathname === `/api/v1/access-groups/${groupId}/disable` && init?.method === 'POST') return new Response(null, { status: 204 });
    if (url.pathname === `/api/v1/access-groups/${groupId}/activate` && init?.method === 'POST') return new Response(null, { status: 204 });
    if (url.pathname === `/api/v1/access-groups/${groupId}`) return Response.json(group);
    if (url.pathname === `/api/v1/access-groups/${groupId}/members`) return Response.json({ items: [{ userId: 'u1', username: 'ravi', expiresAt: null }], page: 1, pageSize: 20, total: 1, totalPages: 1 });
    if (url.pathname === `/api/v1/access-groups/${groupId}/scopes` && init?.method === 'POST') return Response.json({ id: 'new-scope' }, { status: 201 });
    if (url.pathname === `/api/v1/access-groups/${groupId}/scopes/${scopeId}` && init?.method === 'DELETE') return new Response(null, { status: 204 });
    if (url.pathname === '/api/v1/roles') return Response.json([{ id: roleId, code: 'VIEWER', name: 'Viewer', description: null, isSystem: true }]);
    if (url.pathname === '/api/v1/organizations') return Response.json(pageEnvelope([{ id: 'org-1', code: 'OPS', name: 'Operations', organizationType: 'AGENCY', description: null, status: 'ACTIVE' }]));
    if (url.pathname === '/api/v1/organizations/org-1/units') return Response.json(pageEnvelope([{ id: unitId, organizationId: 'org-1', parentUnitId: null, code: 'HQ', name: 'Headquarters', unitType: 'DEPARTMENT', status: 'ACTIVE' }]));
    if (url.pathname === '/api/v1/geographic-areas') return Response.json(pageEnvelope([{ id: areaId, parentAreaId: null, code: 'NORTH', name: 'North zone', areaType: 'ZONE', status: 'ACTIVE' }]));
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
    expect(screen.queryByRole('button', { name: /delete group|edit group|disable group|activate group|add member|remove member/i })).not.toBeInTheDocument();
  });

  it('supports group edit, disable/activate, and scope mutations for group.manage users', async () => {
    vi.stubGlobal('fetch', groupsFetch());
    const user = userEvent.setup();
    renderGroups(['group.read', 'group.manage']);

    const createForm = await screen.findByRole('form', { name: /create access group/i });
    expect(await within(createForm).findByRole('option', { name: /viewer/i })).toHaveValue(roleId);
    await user.click(screen.getByRole('button', { name: /view operators/i }));
    expect(await screen.findByRole('button', { name: /remove scope headquarters/i })).toBeVisible();
    expect(screen.getByRole('form', { name: /add access-group scope/i })).toBeVisible();
    expect(screen.getByRole('button', { name: /edit group/i })).toBeVisible();
    expect(screen.getByRole('button', { name: /disable group/i })).toBeVisible();
    expect(screen.queryByRole('button', { name: /delete group|add member|remove member/i })).not.toBeInTheDocument();
  });

  it('allows editing an access group', async () => {
    const fetch = groupsFetch();
    vi.stubGlobal('fetch', fetch);
    const user = userEvent.setup();
    renderGroups(['group.read', 'group.manage']);

    await user.click(await screen.findByRole('button', { name: /view operators/i }));
    await user.click(screen.getByRole('button', { name: /edit group/i }));

    const editForm = await screen.findByRole('form', { name: /edit access group/i });
    const nameInput = within(editForm).getByLabelText(/^name$/i);
    await user.clear(nameInput);
    await user.type(nameInput, 'Senior Operators');
    await user.click(within(editForm).getByRole('button', { name: /save changes/i }));

    await waitFor(() => expect(fetch).toHaveBeenCalledWith(
      expect.stringContaining(`/api/v1/access-groups/${groupId}`),
      expect.objectContaining({
        method: 'PUT',
        body: expect.stringContaining('"name":"Senior Operators"'),
      }),
    ));
  });

  it('allows disabling an active access group', async () => {
    const fetch = groupsFetch();
    vi.stubGlobal('fetch', fetch);
    const user = userEvent.setup();
    renderGroups(['group.read', 'group.manage']);

    await user.click(await screen.findByRole('button', { name: /view operators/i }));
    await user.click(screen.getByRole('button', { name: /disable group/i }));

    await waitFor(() => expect(fetch).toHaveBeenCalledWith(
      expect.stringContaining(`/api/v1/access-groups/${groupId}/disable`),
      expect.objectContaining({ method: 'POST' }),
    ));
  });

  it('shows a dead-end error, not the estate-wide confirm flow, when activation fails because the role is not active', async () => {
    vi.stubGlobal('fetch', async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = new URL(String(input));
      if (url.pathname === `/api/v1/access-groups/${draftGroupId}/activate` && init?.method === 'POST') {
        return Response.json({
          title: 'Role is not active',
          detail: 'This group’s role VIEWER is DRAFT. Activate the role, or repoint the group, before activating the group.',
        }, { status: 409 });
      }
      if (url.pathname === '/api/v1/access-groups') return Response.json({ items: [draftGroup], page: 1, pageSize: 20, total: 1, totalPages: 1 });
      if (url.pathname === `/api/v1/access-groups/${draftGroupId}`) return Response.json(draftGroup);
      if (url.pathname === `/api/v1/access-groups/${draftGroupId}/members`) return Response.json({ items: [], page: 1, pageSize: 20, total: 0, totalPages: 1 });
      if (url.pathname === '/api/v1/roles') return Response.json([{ id: roleId, code: 'VIEWER', name: 'Viewer', description: null, isSystem: true }]);
      if (url.pathname === '/api/v1/organizations') return Response.json(pageEnvelope([]));
      if (url.pathname === '/api/v1/geographic-areas') return Response.json(pageEnvelope([]));
      return new Response(null, { status: 404 });
    });
    const user = userEvent.setup();
    renderGroups(['group.read', 'group.manage']);

    await user.click(await screen.findByRole('button', { name: /view draft group/i }));
    await user.click(await screen.findByRole('button', { name: /activate group/i }));

    expect(await screen.findByText(/role vieweris draft|role viewer is draft/i)).toBeVisible();
    expect(screen.queryByRole('checkbox', { name: /estate-wide/i })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /confirm estate-wide activation/i })).not.toBeInTheDocument();
  });

  it('offers the estate-wide confirm flow when activation is blocked by an unconstrained dimension', async () => {
    let confirmedRequest: unknown;
    vi.stubGlobal('fetch', async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = new URL(String(input));
      if (url.pathname === `/api/v1/access-groups/${draftGroupId}/activate` && init?.method === 'POST') {
        const body = init.body ? JSON.parse(String(init.body)) : {};
        if (body.confirmUnscoped) {
          confirmedRequest = body;
          return new Response(null, { status: 204 });
        }
        return Response.json({
          title: 'Confirm the estate-wide grant',
          detail: 'This group is unrestricted on organization, so activating it grants its permissions across every department. Re-send with confirmUnscoped: true to proceed.',
        }, { status: 409 });
      }
      if (url.pathname === '/api/v1/access-groups') return Response.json({ items: [draftGroup], page: 1, pageSize: 20, total: 1, totalPages: 1 });
      if (url.pathname === `/api/v1/access-groups/${draftGroupId}`) return Response.json(draftGroup);
      if (url.pathname === `/api/v1/access-groups/${draftGroupId}/members`) return Response.json({ items: [], page: 1, pageSize: 20, total: 0, totalPages: 1 });
      if (url.pathname === '/api/v1/roles') return Response.json([{ id: roleId, code: 'VIEWER', name: 'Viewer', description: null, isSystem: true }]);
      if (url.pathname === '/api/v1/organizations') return Response.json(pageEnvelope([]));
      if (url.pathname === '/api/v1/geographic-areas') return Response.json(pageEnvelope([]));
      return new Response(null, { status: 404 });
    });
    const user = userEvent.setup();
    renderGroups(['group.read', 'group.manage']);

    await user.click(await screen.findByRole('button', { name: /view draft group/i }));
    await user.click(await screen.findByRole('button', { name: /activate group/i }));

    expect(await screen.findByText(/unrestricted on organization/i)).toBeVisible();
    const checkbox = screen.getByRole('checkbox', { name: /estate-wide/i });
    await user.click(checkbox);
    await user.click(screen.getByRole('button', { name: /confirm estate-wide activation/i }));

    await waitFor(() => expect(confirmedRequest).toEqual({ confirmUnscoped: true }));
  });

  it('shows an error when disabling a group fails', async () => {
    vi.stubGlobal('fetch', async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = new URL(String(input));
      if (url.pathname === `/api/v1/access-groups/${groupId}/disable` && init?.method === 'POST') {
        return Response.json({ title: 'Forbidden', detail: 'This action requires the group.manage permission.' }, { status: 403 });
      }
      if (url.pathname === '/api/v1/access-groups') return Response.json({ items: [group], page: 1, pageSize: 20, total: 1, totalPages: 1 });
      if (url.pathname === `/api/v1/access-groups/${groupId}`) return Response.json(group);
      if (url.pathname === `/api/v1/access-groups/${groupId}/members`) return Response.json({ items: [], page: 1, pageSize: 20, total: 0, totalPages: 1 });
      if (url.pathname === '/api/v1/roles') return Response.json([{ id: roleId, code: 'VIEWER', name: 'Viewer', description: null, isSystem: true }]);
      if (url.pathname === '/api/v1/organizations') return Response.json(pageEnvelope([]));
      if (url.pathname === '/api/v1/geographic-areas') return Response.json(pageEnvelope([]));
      return new Response(null, { status: 404 });
    });
    const user = userEvent.setup();
    renderGroups(['group.read', 'group.manage']);

    await user.click(await screen.findByRole('button', { name: /view operators/i }));
    await user.click(await screen.findByRole('button', { name: /disable group/i }));

    expect(await screen.findByText(/requires the group\.manage permission/i)).toBeVisible();
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
    const unitTrigger = await within(form).findByRole('combobox', { name: /organization unit/i });
    await user.click(unitTrigger);
    await user.click(await within(form).findByRole('button', { name: /^headquarters/i }));
    await user.click(within(form).getByRole('button', { name: /add scope/i }));

    await waitFor(() => expect(fetch).toHaveBeenCalledWith(
      expect.stringContaining(`/api/v1/access-groups/${groupId}/scopes`),
      expect.objectContaining({ method: 'POST', body: JSON.stringify({ scopeType: 'ORGANIZATION', organizationUnitId: unitId }) }),
    ));
  });
});
