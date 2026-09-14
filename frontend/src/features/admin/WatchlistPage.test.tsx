import '@testing-library/jest-dom/vitest';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import { afterEach, expect, it, vi } from 'vitest';

import { AuthProvider } from '../../auth/AuthProvider';
import { saveSession } from '../../auth/session';
import { sessionFixture } from '../../test/fixtures';
import { WatchlistPage } from './WatchlistPage';

const entryId = '60000000-0000-4000-8000-000000000001';
const alertId = '60000000-0000-4000-8000-000000000002';

const entry = {
  id: entryId, organizationUnitId: '60000000-0000-4000-8000-000000000003', plateNumberNormalized: 'MH12AB1234',
  reason: 'Reported stolen', severity: 'High', isActive: true, createdAt: '2026-01-01T00:00:00Z',
};

const alert = {
  id: alertId, watchlistEntryId: entryId, plateNumberNormalized: 'MH12AB1234', reason: 'Reported stolen',
  severity: 'High', detectionEventId: '60000000-0000-4000-8000-000000000004', detectionOccurredAt: '2026-01-02T00:00:00Z',
  raisedAt: '2026-01-02T00:01:00Z', acknowledgedAt: null,
};

afterEach(() => {
  vi.unstubAllGlobals();
  sessionStorage.clear();
});

function renderWatchlist(permissions: string[]) {
  saveSession(sessionFixture('watchlist-admin', permissions));
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <MemoryRouter>
      <AuthProvider>
        <QueryClientProvider client={queryClient}>
          <WatchlistPage />
        </QueryClientProvider>
      </AuthProvider>
    </MemoryRouter>,
  );
}

it('lists active entries and unacknowledged alerts', async () => {
  vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => {
    const url = new URL(String(input));
    if (url.pathname === '/api/v1/watchlist/alerts') return Response.json({ items: [alert], page: 1, pageSize: 20, total: 1, totalPages: 1 });
    if (url.pathname === '/api/v1/watchlist') return Response.json({ items: [entry], page: 1, pageSize: 20, total: 1, totalPages: 1 });
    return new Response(null, { status: 404 });
  }));

  renderWatchlist(['alert.read']);

  const entriesRegion = await screen.findByRole('region', { name: /watchlist entries/i });
  expect(await within(entriesRegion).findByText('MH12AB1234')).toBeVisible();

  const alertsRegion = await screen.findByRole('region', { name: /unacknowledged alerts/i });
  expect(await within(alertsRegion).findByText('MH12AB1234')).toBeVisible();
});

it('shows an error when removing a watchlist entry fails', async () => {
  vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = new URL(String(input));
    if (url.pathname === `/api/v1/watchlist/${entryId}` && init?.method === 'DELETE') {
      return Response.json({ title: 'Forbidden', detail: 'This action requires the watchlist.manage permission.' }, { status: 403 });
    }
    if (url.pathname === '/api/v1/watchlist/alerts') return Response.json({ items: [], page: 1, pageSize: 20, total: 0, totalPages: 0 });
    if (url.pathname === '/api/v1/watchlist') return Response.json({ items: [entry], page: 1, pageSize: 20, total: 1, totalPages: 1 });
    return new Response(null, { status: 404 });
  }));

  renderWatchlist(['alert.read', 'watchlist.manage']);

  await userEvent.click(await screen.findByRole('button', { name: /^remove$/i }));

  expect(await screen.findByText(/requires the watchlist\.manage permission/i)).toBeVisible();
});

it('shows an error when acknowledging an alert fails', async () => {
  vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = new URL(String(input));
    if (url.pathname === `/api/v1/watchlist/alerts/${alertId}/acknowledge` && init?.method === 'POST') {
      return Response.json({ title: 'Forbidden', detail: 'This action requires the alert.acknowledge permission.' }, { status: 403 });
    }
    if (url.pathname === '/api/v1/watchlist/alerts') return Response.json({ items: [alert], page: 1, pageSize: 20, total: 1, totalPages: 1 });
    if (url.pathname === '/api/v1/watchlist') return Response.json({ items: [], page: 1, pageSize: 20, total: 0, totalPages: 0 });
    return new Response(null, { status: 404 });
  }));

  renderWatchlist(['alert.read', 'alert.acknowledge']);

  await userEvent.click(await screen.findByRole('button', { name: /acknowledge/i }));

  expect(await screen.findByText(/requires the alert\.acknowledge permission/i)).toBeVisible();
});

it('reports how many historical alerts a new watchlist entry backfilled', async () => {
  vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = new URL(String(input));
    if (url.pathname === '/api/v1/watchlist' && init?.method === 'POST') {
      return Response.json({ id: entryId, historicalAlertsRaised: 3, historicalMatchesCapped: false }, { status: 201 });
    }
    if (url.pathname === '/api/v1/watchlist/alerts') return Response.json({ items: [], page: 1, pageSize: 20, total: 0, totalPages: 0 });
    if (url.pathname === '/api/v1/watchlist') return Response.json({ items: [], page: 1, pageSize: 20, total: 0, totalPages: 0 });
    if (url.pathname === '/api/v1/organizations') return Response.json({ items: [], page: 1, pageSize: 200, total: 0, totalPages: 0 });
    return new Response(null, { status: 404 });
  }));

  renderWatchlist(['alert.read', 'watchlist.manage']);

  await userEvent.type(await screen.findByLabelText(/plate number/i), 'MH12AB1234');
  await userEvent.click(screen.getByRole('button', { name: /^add entry$/i }));

  expect(await screen.findByText(/already seen 3 times before/i)).toBeVisible();
});

it('acknowledges an alert for alert.acknowledge users', async () => {
  const requests: Array<{ path: string; method: string }> = [];
  vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const path = new URL(String(input)).pathname;
    requests.push({ path, method: init?.method ?? 'GET' });
    if (path === `/api/v1/watchlist/alerts/${alertId}/acknowledge` && init?.method === 'POST') return new Response(null, { status: 204 });
    if (path === '/api/v1/watchlist/alerts') return Response.json({ items: [alert], page: 1, pageSize: 20, total: 1, totalPages: 1 });
    if (path === '/api/v1/watchlist') return Response.json({ items: [], page: 1, pageSize: 20, total: 0, totalPages: 0 });
    return new Response(null, { status: 404 });
  }));

  renderWatchlist(['alert.read', 'alert.acknowledge']);

  await userEvent.click(await screen.findByRole('button', { name: /acknowledge/i }));

  expect(requests.some((request) => request.path === `/api/v1/watchlist/alerts/${alertId}/acknowledge` && request.method === 'POST')).toBe(true);
});
