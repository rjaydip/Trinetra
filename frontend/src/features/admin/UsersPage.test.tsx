import '@testing-library/jest-dom/vitest';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import { afterEach, expect, it, vi } from 'vitest';

import { AuthProvider } from '../../auth/AuthProvider';
import { saveSession } from '../../auth/session';
import { sessionFixture } from '../../test/fixtures';
import { UsersPage } from './UsersPage';

const userId = '30000000-0000-4000-8000-000000000001';

const user = {
  id: userId, username: 'ravi', displayName: 'Ravi Kumar', email: 'ravi@example.test',
  mustChangePassword: false, status: 'ACTIVE', lastLoginAt: null, isSystem: false,
};

afterEach(() => {
  vi.unstubAllGlobals();
  sessionStorage.clear();
});

function renderUsers(permissions: string[]) {
  saveSession(sessionFixture('user-admin', permissions));
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <MemoryRouter>
      <AuthProvider>
        <QueryClientProvider client={queryClient}>
          <UsersPage />
        </QueryClientProvider>
      </AuthProvider>
    </MemoryRouter>,
  );
}

it('lists user accounts with pagination and shows detail on selection', async () => {
  vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => {
    const url = new URL(String(input));
    if (url.pathname === '/api/v1/users') return Response.json({ items: [user], page: 1, pageSize: 20, total: 1, totalPages: 1 });
    if (url.pathname === `/api/v1/users/${userId}`) return Response.json(user);
    if (url.pathname === `/api/v1/users/${userId}/groups`) return Response.json([]);
    if (url.pathname === `/api/v1/users/${userId}/permissions`) return Response.json(['camera.read']);
    return new Response(null, { status: 404 });
  }));

  renderUsers(['user.read']);

  expect(await screen.findByText('Ravi Kumar')).toBeVisible();

  await userEvent.click(screen.getByRole('button', { name: /view ravi kumar/i }));

  expect(await screen.findByRole('heading', { name: 'Ravi Kumar' })).toBeVisible();
  expect(await screen.findByText('camera.read')).toBeVisible();
});

it('creates a user account', async () => {
  const requests: Array<{ path: string; method: string; body?: Record<string, unknown> }> = [];
  vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const path = new URL(String(input)).pathname;
    requests.push({
      path, method: init?.method ?? 'GET',
      body: typeof init?.body === 'string' ? JSON.parse(init.body) as Record<string, unknown> : undefined,
    });
    if (path === '/api/v1/users' && init?.method === 'POST') return Response.json({ id: userId }, { status: 201 });
    if (path === '/api/v1/users') return Response.json({ items: [], page: 1, pageSize: 20, total: 0, totalPages: 0 });
    return new Response(null, { status: 404 });
  }));

  renderUsers(['user.read', 'user.manage']);

  await screen.findByRole('heading', { name: /create user/i });
  await userEvent.type(screen.getByLabelText(/username/i), 'newuser');
  await userEvent.type(screen.getByLabelText(/display name/i), 'New User');
  await userEvent.type(screen.getByLabelText(/initial password/i), 'Sup3rSecret!');
  await userEvent.click(screen.getByRole('button', { name: /^create user$/i }));

  const create = requests.find((request) => request.path === '/api/v1/users' && request.method === 'POST');
  expect(create?.body).toMatchObject({ username: 'newuser', displayName: 'New User', password: 'Sup3rSecret!' });
});
