import '@testing-library/jest-dom/vitest';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { CredentialPanel } from './CredentialPanel';

const vmsId = '44444444-4444-4444-8444-444444444444';
const testId = '66666666-6666-4666-8666-666666666666';

afterEach(() => {
  vi.useRealTimers();
  vi.unstubAllGlobals();
});

function renderCredentialPanel(permissions = ['vms.read', 'credential.write', 'integration.manage']) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return {
    queryClient,
    ...render(
      <QueryClientProvider client={queryClient}>
        <CredentialPanel permissions={permissions} vmsId={vmsId} />
      </QueryClientProvider>,
    ),
  };
}

describe('CredentialPanel', () => {
  it('clears password and token after credential save and renders only safe confirmation data', async () => {
    const secret = 'never-render-again';
    const token = 'also-never-render-again';
    const updatedAt = '2026-09-04T10:30:00Z';
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const path = new URL(String(input)).pathname;
      if (path.endsWith('/credential/status')) return Response.json({ reference: 'vms/north-nvr', exists: false });
      if (path.endsWith('/credential') && init?.method === 'PUT') {
        expect(init.body).toBe(JSON.stringify({ username: 'operator', password: secret, token, description: 'Primary operator' }));
        return Response.json({ credentialReference: 'vms/north-nvr', updatedAt });
      }
      return new Response(null, { status: 404 });
    }));
    const user = userEvent.setup();

    const { queryClient } = renderCredentialPanel();
    await user.type(await screen.findByLabelText(/username/i), 'operator');
    await user.type(screen.getByLabelText(/^password/i), secret);
    await user.type(screen.getByLabelText(/^token/i), token);
    await user.type(screen.getByLabelText(/description/i), 'Primary operator');
    await user.click(screen.getByRole('button', { name: /save credential/i }));

    expect(await screen.findByText(/^credential set$/i)).toBeVisible();
    expect(screen.getByLabelText(/^password/i)).toHaveValue('');
    expect(screen.getByLabelText(/^token/i)).toHaveValue('');
    expect(document.body).not.toHaveTextContent(secret);
    expect(document.body).not.toHaveTextContent(token);
    expect(screen.getByText(/updated/i)).toBeVisible();
    expect(screen.queryByText(/vms\/north-nvr/i)).not.toBeInTheDocument();
    expect(JSON.stringify(queryClient.getQueryCache().getAll().map((query) => query.queryKey))).not.toContain(secret);
    expect(queryClient.getMutationCache().getAll()).toHaveLength(0);
  });

  it('requires a password or token before sending a credential', async () => {
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => {
      const path = new URL(String(input)).pathname;
      if (path.endsWith('/credential/status')) return Response.json({ reference: 'vms/north-nvr', exists: false });
      return new Response(null, { status: 500 });
    }));
    const user = userEvent.setup();

    renderCredentialPanel();
    await screen.findByText(/^credential not set$/i);
    await user.click(screen.getByRole('button', { name: /save credential/i }));

    expect(screen.getByRole('alert')).toHaveTextContent(/password or token is required/i);
    expect(vi.mocked(fetch)).toHaveBeenCalledTimes(1);
  });

  it('clears secret inputs when a credential save fails', async () => {
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const path = new URL(String(input)).pathname;
      if (path.endsWith('/credential/status')) return Response.json({ reference: 'vms/north-nvr', exists: false });
      if (path.endsWith('/credential') && init?.method === 'PUT') {
        return Response.json({ title: 'Unavailable', detail: 'The secret store is unavailable.' }, { status: 503 });
      }
      return new Response(null, { status: 404 });
    }));
    const user = userEvent.setup();

    renderCredentialPanel();
    await user.type(await screen.findByLabelText(/^password/i), 'temporary-secret');
    await user.click(screen.getByRole('button', { name: /save credential/i }));

    expect(await screen.findByRole('alert')).toHaveTextContent('The secret store is unavailable.');
    expect(screen.getByLabelText(/^password/i)).toHaveValue('');
    expect(document.body).not.toHaveTextContent('temporary-secret');
  });

  it('polls a created connection test through running until it is terminal', async () => {
    let polls = 0;
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const path = new URL(String(input)).pathname;
      if (path.endsWith('/credential/status')) return Response.json({ reference: 'vms/north-nvr', exists: true });
      if (path.endsWith('/test') && init?.method === 'POST') {
        return Response.json({ testId, status: 'pending', statusUrl: `/api/v1/vms/${vmsId}/test/${testId}` }, { status: 202 });
      }
      if (path.endsWith(`/test/${testId}`)) {
        polls += 1;
        return Response.json({
          testId,
          targetId: vmsId,
          status: polls === 1 ? 'running' : 'completed',
          requestedAt: '2026-09-04T10:30:00Z',
          completedAt: polls === 1 ? null : '2026-09-04T10:30:01Z',
          failureReason: null,
          result: polls === 1 ? null : { reachable: true },
        });
      }
      return new Response(null, { status: 404 });
    }));
    const user = userEvent.setup();

    renderCredentialPanel();
    await user.click(await screen.findByRole('button', { name: /test connection/i }));

    expect(await screen.findByText(/connection test completed/i, {}, { timeout: 4_000 })).toBeVisible();
    expect(polls).toBe(2);
  });

  it('allowlists cached connection-test fields when the server returns unexpected secret-like properties', async () => {
    const leakedPassword = 'unexpected-top-level-password';
    const leakedToken = 'unexpected-top-level-token';
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const path = new URL(String(input)).pathname;
      if (path.endsWith('/credential/status')) return Response.json({ reference: 'vms/north-nvr', exists: true });
      if (path.endsWith('/test') && init?.method === 'POST') {
        return Response.json({ testId, status: 'pending', statusUrl: `/api/v1/vms/${vmsId}/test/${testId}` }, { status: 202 });
      }
      if (path.endsWith(`/test/${testId}`)) {
        return Response.json({
          testId,
          targetId: vmsId,
          status: 'completed',
          requestedAt: '2026-09-04T10:30:00Z',
          completedAt: '2026-09-04T10:30:01Z',
          failureReason: null,
          result: { reachable: true },
          password: leakedPassword,
          token: leakedToken,
          credential: { value: 'unexpected-nested-secret' },
        });
      }
      return new Response(null, { status: 404 });
    }));
    const user = userEvent.setup();

    const { queryClient } = renderCredentialPanel();
    await user.click(await screen.findByRole('button', { name: /test connection/i }));
    expect(await screen.findByText(/connection test completed/i)).toBeVisible();

    const cachedTest = queryClient.getQueryCache().find({ queryKey: ['vms', vmsId, 'connection-test', testId] })?.state.data;
    expect(cachedTest).toEqual({
      testId,
      targetId: vmsId,
      status: 'completed',
      requestedAt: '2026-09-04T10:30:00Z',
      completedAt: '2026-09-04T10:30:01Z',
      failureReason: null,
      result: { reachable: true },
    });
    expect(JSON.stringify(cachedTest)).not.toContain(leakedPassword);
    expect(JSON.stringify(cachedTest)).not.toContain(leakedToken);
  });

  it('does not offer credential writes or connection tests without their permissions', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => Response.json({ reference: 'vms/north-nvr', exists: true })));

    renderCredentialPanel(['vms.read']);

    expect(await screen.findByText(/^credential set$/i)).toBeVisible();
    expect(screen.queryByLabelText(/^password/i)).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /test connection/i })).not.toBeInTheDocument();
    await waitFor(() => expect(fetch).toHaveBeenCalledTimes(1));
  });
});
