import '@testing-library/jest-dom/vitest';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { pageEnvelope, vmsFixture } from '../../test/fixtures';
import { VmsCapabilitiesPanel, VmsDeleteControl, VmsEditForm, VmsHealthPanel, VmsStateControl } from './VmsManagement';

const vmsId = '44444444-4444-4444-8444-444444444444';

const target = vmsFixture({ id: vmsId });

function renderWithProviders(node: React.ReactElement) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <MemoryRouter initialEntries={['/vms']}>
      <QueryClientProvider client={queryClient}>{node}</QueryClientProvider>
      <Routes><Route path="/vms" element={<p>VMS list</p>} /></Routes>
    </MemoryRouter>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('VmsStateControl', () => {
  it('sends the chosen state and disables the button matching the current state', async () => {
    const requests: Array<{ body: unknown }> = [];
    vi.stubGlobal('fetch', vi.fn(async (_input: RequestInfo | URL, init?: RequestInit) => {
      requests.push({ body: typeof init?.body === 'string' ? JSON.parse(init.body) : undefined });
      return new Response(null, { status: 204 });
    }));
    const user = userEvent.setup();
    renderWithProviders(<VmsStateControl target={target} onSuccess={() => undefined} />);

    expect(screen.getByRole('button', { name: /^activate$/i })).toBeDisabled();
    await user.click(screen.getByRole('button', { name: /^quarantine$/i }));

    await waitFor(() => expect(requests).toHaveLength(1));
    expect(requests[0].body).toEqual({ state: 'Quarantined' });
  });
});

describe('VmsDeleteControl', () => {
  it('disables delete while the target is active', () => {
    renderWithProviders(<VmsDeleteControl target={target} />);

    expect(screen.getByRole('button', { name: /delete vms/i })).toBeDisabled();
  });

  it('requires an explicit confirmation before deleting an inactive target', async () => {
    const requests: Array<{ method?: string }> = [];
    vi.stubGlobal('fetch', vi.fn(async (_input: RequestInfo | URL, init?: RequestInit) => {
      requests.push({ method: init?.method });
      return new Response(null, { status: 204 });
    }));
    const user = userEvent.setup();
    renderWithProviders(<VmsDeleteControl target={{ ...target, state: 'Quarantined' }} />);

    await user.click(screen.getByRole('button', { name: /delete vms/i }));
    expect(requests).toHaveLength(0);
    expect(screen.getByText(/this cannot be undone/i)).toBeVisible();

    await user.click(screen.getByRole('button', { name: /confirm delete/i }));

    await waitFor(() => expect(requests).toHaveLength(1));
    expect(requests[0].method).toBe('DELETE');
  });
});

describe('VmsEditForm', () => {
  it('pre-fills current values and sends a full replacement on submit', async () => {
    const requests: Array<{ body: unknown }> = [];
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = new URL(String(input));
      if (url.pathname === `/api/v1/organization-units/${target.organizationUnitId}`) {
        return Response.json({ id: target.organizationUnitId, organizationId: 'org-1', parentUnitId: null, code: 'NORTH', name: 'North Unit', unitType: 'REGION', status: 'ACTIVE' });
      }
      if (url.pathname === '/api/v1/organizations/org-1/units') {
        return Response.json(pageEnvelope([{ id: target.organizationUnitId, organizationId: 'org-1', parentUnitId: null, code: 'NORTH', name: 'North Unit', unitType: 'REGION', status: 'ACTIVE' }]));
      }
      if (url.pathname === '/api/v1/geographic-areas') {
        return Response.json(pageEnvelope([{ id: target.geographicAreaId, parentAreaId: null, code: 'HQ', name: 'Headquarters', areaType: 'DISTRICT', status: 'ACTIVE' }]));
      }
      requests.push({ body: typeof init?.body === 'string' ? JSON.parse(init.body) : undefined });
      return new Response(null, { status: 204 });
    }));
    const user = userEvent.setup();
    renderWithProviders(<VmsEditForm target={target} onSuccess={() => undefined} />);

    expect(screen.getByLabelText(/display name/i)).toHaveValue('North NVR');
    await user.clear(screen.getByLabelText(/display name/i));
    await user.type(screen.getByLabelText(/display name/i), 'North NVR (renamed)');
    await user.click(screen.getByRole('button', { name: /save changes/i }));

    await waitFor(() => expect(requests).toHaveLength(1));
    expect(requests[0].body).toEqual(expect.objectContaining({
      code: 'NORTH-NVR',
      displayName: 'North NVR (renamed)',
      organizationUnitId: target.organizationUnitId,
      geographicAreaId: target.geographicAreaId,
      verifyTls: true,
    }));
  });
});

describe('VmsHealthPanel', () => {
  it('renders the connector health history', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => Response.json([{
      checkedAt: '2026-09-12T10:00:00Z', status: 'OK', latencyMs: 120, cameraCount: 24,
      consecutiveFailures: 0, circuitOpen: false, lastError: null, eventsSinceCheck: 5, cursorLagSeconds: 2,
    }])));
    renderWithProviders(<VmsHealthPanel vmsId={vmsId} />);

    expect(await screen.findByText('OK')).toBeVisible();
    expect(screen.getByText('120')).toBeVisible();
  });

  it('explains an empty health history', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => Response.json([])));
    renderWithProviders(<VmsHealthPanel vmsId={vmsId} />);

    expect(await screen.findByText(/no health checks recorded yet/i)).toBeVisible();
  });
});

describe('VmsCapabilitiesPanel', () => {
  it('decodes the capability bitmask into readable names', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => Response.json({
      supported: (1 << 0) | (1 << 2), adapterVersion: '1.4.0', probedAt: '2026-09-12T10:00:00Z', notes: { note: 'seasonal firmware' },
    })));
    renderWithProviders(<VmsCapabilitiesPanel vmsId={vmsId} />);

    expect(await screen.findByText('Camera inventory')).toBeVisible();
    expect(screen.getByText('Live streams')).toBeVisible();
    expect(screen.queryByText('Pan/tilt/zoom control')).not.toBeInTheDocument();
    expect(screen.getByText('seasonal firmware')).toBeVisible();
  });

  it('explains an unprobed target instead of showing a raw 404', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => new Response(null, { status: 404 })));
    renderWithProviders(<VmsCapabilitiesPanel vmsId={vmsId} />);

    expect(await screen.findByText(/hasn't reached this target/i)).toBeVisible();
  });
});
