import '@testing-library/jest-dom/vitest';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import { afterEach, expect, it, vi } from 'vitest';

import { AuthProvider } from '../../auth/AuthProvider';
import { saveSession } from '../../auth/session';
import { sessionFixture } from '../../test/fixtures';
import { WorkerHealthPage } from './WorkerHealthPage';

const workerRecordId = '50000000-0000-4000-8000-000000000001';

const liveWorker = {
  id: workerRecordId, apiKeyId: '50000000-0000-4000-8000-000000000002', apiKeyName: 'AI worker',
  workerId: 'ai-worker-1-of-2', hostname: 'gpu-node-1', firstSeenAt: '2026-01-01T00:00:00Z',
  lastHeartbeatAt: new Date().toISOString(), reportedAt: new Date().toISOString(), clockDriftSeconds: 1,
};

afterEach(() => {
  vi.unstubAllGlobals();
  sessionStorage.clear();
});

function renderWorkers(permissions: string[]) {
  saveSession(sessionFixture('worker-admin', permissions));
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <MemoryRouter>
      <AuthProvider>
        <QueryClientProvider client={queryClient}>
          <WorkerHealthPage />
        </QueryClientProvider>
      </AuthProvider>
    </MemoryRouter>,
  );
}

it('lists workers with a live status derived from the last heartbeat', async () => {
  vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => {
    const url = new URL(String(input));
    if (url.pathname === '/api/v1/worker-health') return Response.json({ items: [liveWorker], page: 1, pageSize: 20, total: 1, totalPages: 1 });
    return new Response(null, { status: 404 });
  }));

  renderWorkers(['worker.read']);

  expect(await screen.findByText('ai-worker-1-of-2')).toBeVisible();
  expect(screen.getByText(/^live$/i)).toBeVisible();
});

it('retires a worker record for worker.manage users', async () => {
  const requests: Array<{ path: string; method: string }> = [];
  vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const path = new URL(String(input)).pathname;
    requests.push({ path, method: init?.method ?? 'GET' });
    if (path === `/api/v1/worker-health/${workerRecordId}` && init?.method === 'DELETE') return new Response(null, { status: 204 });
    if (path === '/api/v1/worker-health') return Response.json({ items: [liveWorker], page: 1, pageSize: 20, total: 1, totalPages: 1 });
    return new Response(null, { status: 404 });
  }));

  renderWorkers(['worker.read', 'worker.manage']);

  await userEvent.click(await screen.findByRole('button', { name: /retire/i }));

  expect(requests.some((request) => request.path === `/api/v1/worker-health/${workerRecordId}` && request.method === 'DELETE')).toBe(true);
});
