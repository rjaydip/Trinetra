import '@testing-library/jest-dom/vitest';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { AuthProvider } from '../../auth/AuthProvider';
import { saveSession } from '../../auth/session';
import { sessionFixture } from '../../test/fixtures';
import { RolesPage } from './RolesPage';

const roleId = '30000000-0000-4000-8000-000000000001';
const customRoleId = '30000000-0000-4000-8000-000000000002';

const mockRoles = [
  {
    id: roleId,
    code: 'VIEWER',
    name: 'Viewer',
    description: 'Read-only access to camera feeds and health',
    isSystem: true,
    status: 'ACTIVE',
    customized: false,
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-01T00:00:00Z',
    usageCount: 2,
    permissions: ['camera.read'],
    permissionDetails: [
      { code: 'camera.read', name: 'Read cameras', category: 'Camera', description: 'View camera registry' },
    ],
    usedBy: [
      { id: 'group-1', code: 'OPERATORS', name: 'Operators', status: 'ACTIVE' },
    ],
  },
  {
    id: customRoleId,
    code: 'CUSTOM_PATROL',
    name: 'Custom Patrol',
    description: 'Field patrol team',
    isSystem: false,
    status: 'DRAFT',
    customized: false,
    createdAt: '2026-02-01T00:00:00Z',
    updatedAt: '2026-02-01T00:00:00Z',
    usageCount: 0,
    permissions: ['camera.read', 'camera.control'],
    permissionDetails: [],
    usedBy: [],
  },
];

const mockPermissions = [
  { code: 'camera.read', name: 'Read cameras', category: 'Camera', description: 'View camera registry' },
  { code: 'camera.control', name: 'Control cameras', category: 'Camera', description: 'PTZ operations' },
  { code: 'group.read', name: 'Read groups', category: 'Access', description: 'View access groups' },
];

afterEach(() => {
  vi.unstubAllGlobals();
  sessionStorage.clear();
});

function renderRolesPage(permissions: string[]) {
  saveSession(sessionFixture('roles-admin', permissions));
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <MemoryRouter>
      <AuthProvider>
        <QueryClientProvider client={queryClient}>
          <RolesPage />
        </QueryClientProvider>
      </AuthProvider>
    </MemoryRouter>,
  );
}

function rolesFetch() {
  return vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = new URL(String(input));
    if (url.pathname === '/api/v1/roles' && init?.method === 'POST') {
      return Response.json({ id: 'new-role-id' }, { status: 201 });
    }
    if (url.pathname === `/api/v1/roles/${roleId}` && init?.method === 'PUT') {
      return Response.json(mockRoles[0]);
    }
    if (url.pathname === `/api/v1/roles/${customRoleId}` && init?.method === 'PUT') {
      return Response.json(mockRoles[1]);
    }
    if (url.pathname === `/api/v1/roles/${customRoleId}` && init?.method === 'DELETE') {
      return new Response(null, { status: 204 });
    }
    if (url.pathname === '/api/v1/roles') return Response.json(mockRoles);
    if (url.pathname === `/api/v1/roles/${roleId}`) return Response.json(mockRoles[0]);
    if (url.pathname === `/api/v1/roles/${customRoleId}`) return Response.json(mockRoles[1]);
    if (url.pathname === '/api/v1/permissions') return Response.json(mockPermissions);
    return new Response(null, { status: 404 });
  });
}

describe('RolesPage role catalogue maintenance', () => {
  it('renders role catalogue and permission catalogue with details to read-only users', async () => {
    vi.stubGlobal('fetch', rolesFetch());
    renderRolesPage(['group.read']);

    expect(await screen.findByRole('heading', { name: /^roles & permissions$/i })).toBeVisible();
    expect(screen.getByRole('heading', { name: /^role catalogue$/i })).toBeVisible();
    expect(screen.getByRole('heading', { name: /^permission catalogue$/i })).toBeVisible();

    expect((await screen.findAllByText('Viewer')).length).toBeGreaterThan(0);
    expect(screen.getByText('Custom Patrol')).toBeVisible();
    expect(screen.getAllByText('ACTIVE').length).toBeGreaterThan(0);
    expect(screen.getByText('DRAFT')).toBeVisible();

    // Read-only user should not have creation or edit buttons
    expect(screen.queryByRole('button', { name: /create custom role/i })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /edit role/i })).not.toBeInTheDocument();
  });

  it('allows role.manage users to create a new custom role with permissions', async () => {
    const fetch = rolesFetch();
    vi.stubGlobal('fetch', fetch);
    const user = userEvent.setup();
    renderRolesPage(['role.manage']);

    const createBtn = await screen.findByRole('button', { name: /create custom role/i });
    await user.click(createBtn);

    const form = await screen.findByRole('form', { name: /create custom role/i });
    await user.type(within(form).getByLabelText(/role code/i), 'DISPATCH_LEAD');
    await user.type(within(form).getByLabelText(/display name/i), 'Dispatch Lead');

    // Click permission checkbox
    const readCheck = within(form).getByRole('checkbox', { name: /camera\.read/i });
    await user.click(readCheck);

    await user.click(within(form).getByRole('button', { name: /create role/i }));

    await waitFor(() => {
      const postCalls = fetch.mock.calls.filter(([input, init]) => (
        String(input).includes('/api/v1/roles') && (init as RequestInit | undefined)?.method === 'POST'
      ));
      expect(postCalls.length).toBe(1);
      const payload = JSON.parse(String((postCalls[0][1] as RequestInit).body));
      expect(payload.code).toBe('DISPATCH_LEAD');
      expect(payload.name).toBe('Dispatch Lead');
      expect(payload.permissions).toContain('camera.read');
    });
  });

  it('allows role.manage users to edit an existing role', async () => {
    const fetch = rolesFetch();
    vi.stubGlobal('fetch', fetch);
    const user = userEvent.setup();
    renderRolesPage(['role.manage']);

    // Detail view for first role
    const editBtn = await screen.findByRole('button', { name: /edit role/i });
    await user.click(editBtn);

    const form = await screen.findByRole('form', { name: /edit role viewer/i });
    expect(within(form).getByLabelText(/display name/i)).toHaveValue('Viewer');

    await user.clear(within(form).getByLabelText(/display name/i));
    await user.type(within(form).getByLabelText(/display name/i), 'Senior Viewer');
    await user.click(within(form).getByRole('button', { name: /save changes/i }));

    await waitFor(() => {
      const putCalls = fetch.mock.calls.filter(([input, init]) => (
        String(input).includes(`/api/v1/roles/${roleId}`) && (init as RequestInit | undefined)?.method === 'PUT'
      ));
      expect(putCalls.length).toBeGreaterThan(0);
      const payload = JSON.parse(String((putCalls[0][1] as RequestInit).body));
      expect(payload.name).toBe('Senior Viewer');
    });
  });

  it('allows activating a draft role', async () => {
    const fetch = rolesFetch();
    vi.stubGlobal('fetch', fetch);
    const user = userEvent.setup();
    renderRolesPage(['role.manage']);

    // Select Custom Patrol role
    const viewButtons = await screen.findAllByRole('button', { name: /view details/i });
    await user.click(viewButtons[1]); // second row is Custom Patrol

    const activateBtn = await screen.findByRole('button', { name: /activate role/i });
    await user.click(activateBtn);

    await waitFor(() => {
      const putCalls = fetch.mock.calls.filter(([input, init]) => (
        String(input).includes(`/api/v1/roles/${customRoleId}`) && (init as RequestInit | undefined)?.method === 'PUT'
      ));
      expect(putCalls.length).toBeGreaterThan(0);
      const payload = JSON.parse(String((putCalls[0][1] as RequestInit).body));
      expect(payload.status).toBe('ACTIVE');
    });
  });
});
