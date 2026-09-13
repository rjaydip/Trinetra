import '@testing-library/jest-dom/vitest';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import { afterEach, expect, it, vi } from 'vitest';

import { AuthProvider } from '../../auth/AuthProvider';
import { saveSession } from '../../auth/session';
import { sessionFixture } from '../../test/fixtures';
import { ApiKeysPage } from './ApiKeysPage';

const keyId = '40000000-0000-4000-8000-000000000001';
const groupId = '40000000-0000-4000-8000-000000000002';

const apiKey = {
  id: keyId, keyId: 'ak_abcdef1234567890', displayName: 'AI worker', groupId, groupCode: 'ANALYTICS',
  createdAt: '2026-01-01T00:00:00Z', expiresAt: null, lastUsedAt: null, revokedAt: null,
};

const group = { id: groupId, code: 'ANALYTICS', name: 'Analytics workers', description: null, status: 'ACTIVE', roleCode: 'DETECTION_WORKER', permissions: [], memberCount: 0, scopes: [] };

afterEach(() => {
  vi.unstubAllGlobals();
  sessionStorage.clear();
});

function renderKeys(permissions: string[]) {
  saveSession(sessionFixture('key-admin', permissions));
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <MemoryRouter>
      <AuthProvider>
        <QueryClientProvider client={queryClient}>
          <ApiKeysPage />
        </QueryClientProvider>
      </AuthProvider>
    </MemoryRouter>,
  );
}

it('lists provisioned keys without exposing key material', async () => {
  vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => {
    const url = new URL(String(input));
    if (url.pathname === '/api/v1/api-keys') return Response.json({ items: [apiKey], page: 1, pageSize: 20, total: 1, totalPages: 1 });
    return new Response(null, { status: 404 });
  }));

  renderKeys(['apikey.read']);

  expect(await screen.findByText('AI worker')).toBeVisible();
  expect(screen.getByText(/active/i)).toBeVisible();
  expect(document.body).not.toHaveTextContent(/rawKey/i);
});

it('provisions a key through the selected access group and shows the raw value once', async () => {
  const requests: Array<{ path: string; method: string; body?: Record<string, unknown> }> = [];
  vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const path = new URL(String(input)).pathname;
    requests.push({
      path, method: init?.method ?? 'GET',
      body: typeof init?.body === 'string' ? JSON.parse(init.body) as Record<string, unknown> : undefined,
    });
    if (path === '/api/v1/access-groups') return Response.json([group]);
    if (path === '/api/v1/api-keys' && init?.method === 'POST') return Response.json({ id: keyId, keyId: apiKey.keyId, rawKey: 'raw-secret-value' }, { status: 201 });
    if (path === '/api/v1/api-keys') return Response.json({ items: [], page: 1, pageSize: 20, total: 0, totalPages: 0 });
    return new Response(null, { status: 404 });
  }));

  renderKeys(['apikey.read', 'apikey.manage']);

  await userEvent.type(await screen.findByLabelText(/display name/i), 'New key');
  await userEvent.selectOptions(await screen.findByLabelText(/access group/i), groupId);
  await userEvent.click(screen.getByRole('button', { name: /provision key/i }));

  expect(await screen.findByText('raw-secret-value')).toBeVisible();
  const create = requests.find((request) => request.path === '/api/v1/api-keys' && request.method === 'POST');
  expect(create?.body).toMatchObject({ displayName: 'New key', groupId });
});
