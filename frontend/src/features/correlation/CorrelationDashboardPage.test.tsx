import '@testing-library/jest-dom/vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { App } from '../../App';
import { AuthProvider } from '../../auth/AuthProvider';
import { sessionFixture } from '../../test/fixtures';

function renderApp(permissions: string[]) {
  sessionStorage.setItem('trinetra.auth.session', JSON.stringify(sessionFixture('correlation-viewer', permissions)));
  return render(<MemoryRouter initialEntries={['/correlation']}><AuthProvider><App /></AuthProvider></MemoryRouter>);
}

const group = {
  id: 'g0000000-0000-4000-8000-000000000001', ruleCode: 'SAME_PLATE_ADJACENT_CAMERAS',
  naturalKey: 'MH12AB1234', windowBucket: '2026-09-15T00:00:00Z', confidence: 0.82,
  memberCount: 3, firstOccurredAt: '2026-09-15T00:00:00Z', lastOccurredAt: '2026-09-15T00:10:00Z',
  createdAt: '2026-09-15T00:10:05Z',
};

afterEach(() => {
  vi.unstubAllGlobals();
  sessionStorage.clear();
});

describe('CorrelationDashboardPage', () => {
  it('renders correlation groups and federated analytics for a caller with correlation.read', async () => {
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => {
      const url = new URL(String(input));
      if (url.pathname === '/api/v1/correlation/groups') return Response.json([group]);
      return new Response(null, { status: 404 });
    }));
    renderApp(['correlation.read']);

    expect(await screen.findByRole('heading', { name: /correlation & alert dashboard/i })).toBeVisible();
    // Appears twice: once in the groups table, once in the federated-analytics rule breakdown.
    expect(await screen.findAllByText('SAME_PLATE_ADJACENT_CAMERAS')).toHaveLength(2);
    expect(screen.getByText(/82% possible match/i)).toBeVisible();
    // Federated analytics section aggregates the same groups response — 3 correlated member
    // events (memberCount), and an average confidence of 82% (the only group).
    expect(screen.getByText('3', { selector: 'dd' })).toBeVisible();
    expect(screen.getAllByText(/82%/)).not.toHaveLength(0);
  });

  it('expands a group to load its member events on demand', async () => {
    const fetchMock = vi.fn(async (input: RequestInfo | URL) => {
      const url = new URL(String(input));
      if (url.pathname === '/api/v1/correlation/groups') return Response.json([group]);
      if (url.pathname === `/api/v1/correlation/groups/${group.id}`) {
        return Response.json({
          group,
          members: [{ federationEventId: 'evt-1', occurredAt: '2026-09-15T00:00:00Z', cameraId: 'cam-1', sourceVmsId: 'vms-1', organizationUnitId: 'org-1', geographicAreaId: null }],
        });
      }
      return new Response(null, { status: 404 });
    });
    vi.stubGlobal('fetch', fetchMock);
    const user = userEvent.setup();
    renderApp(['correlation.read']);

    await user.click(await screen.findByRole('button', { name: /show members/i }));

    expect(await screen.findByText('cam-1')).toBeVisible();
    expect(fetchMock.mock.calls.some(([input]) => String(input).includes(`/api/v1/correlation/groups/${group.id}`))).toBe(true);
  });

  it('does not fetch alerts or events without their read permissions', async () => {
    const fetchMock = vi.fn(async (input: RequestInfo | URL) => {
      const url = new URL(String(input));
      if (url.pathname === '/api/v1/correlation/groups') return Response.json([]);
      return new Response(null, { status: 404 });
    });
    vi.stubGlobal('fetch', fetchMock);
    renderApp(['correlation.read']);

    expect(await screen.findByRole('heading', { name: /correlation & alert dashboard/i })).toBeVisible();
    expect(screen.queryByRole('heading', { name: /recent watchlist alerts/i })).not.toBeInTheDocument();
    expect(screen.queryByRole('heading', { name: /recent events/i })).not.toBeInTheDocument();
    expect(fetchMock.mock.calls.some(([input]) => String(input).includes('/api/v1/watchlist/alerts'))).toBe(false);
    expect(fetchMock.mock.calls.some(([input]) => String(input).includes('/api/v1/events'))).toBe(false);
  });
});
