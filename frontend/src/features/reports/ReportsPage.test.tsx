import '@testing-library/jest-dom/vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { App } from '../../App';
import { AuthProvider } from '../../auth/AuthProvider';
import { pageEnvelope } from '../../test/fixtures';

function renderApp(path: string) {
  return render(
    <MemoryRouter initialEntries={[path]}>
      <AuthProvider><App /></AuthProvider>
    </MemoryRouter>,
  );
}

function signIn() {
  sessionStorage.setItem('trinetra.auth.session', JSON.stringify({
    token: 'reports-token',
    expiresAt: new Date(Date.now() + 60_000).toISOString(),
    mustChangePassword: false,
  }));
}

afterEach(() => {
  vi.unstubAllGlobals();
  sessionStorage.clear();
});

describe('ReportsPage', () => {
  it('does not present unavailable coverage gaps as zero', async () => {
    signIn();
    vi.stubGlobal('fetch', async (input: RequestInfo | URL) => {
      const url = new URL(String(input));
      if (url.pathname === '/api/v1/overview') return Response.json({
        targets: 10, activeTargets: 8, quarantinedTargets: 2, cameras: 12, unreachableCameras: 3,
      });
      if (url.pathname === '/api/v1/organizations' || url.pathname === '/api/v1/geographic-areas') return Response.json(pageEnvelope([]));
      return new Response(null, { status: 404 });
    });

    renderApp('/reports');

    expect(await screen.findByText(/coverage-gap analysis is not available yet/i)).toBeVisible();
    expect(screen.queryByText(/^0 gaps$/i)).not.toBeInTheDocument();
    expect(screen.getByText(/ageing infrastructure reporting is not available yet/i)).toBeVisible();
  });

  it('renders backend fleet totals and selected coverage buckets without calling gaps', async () => {
    signIn();
    const fetch = vi.fn(async (input: RequestInfo | URL) => {
      const url = new URL(String(input));
      if (url.pathname === '/api/v1/overview') {
        return Response.json({ targets: 10, activeTargets: 8, quarantinedTargets: 2, cameras: 12, unreachableCameras: 3 });
      }
      if (url.pathname === '/api/v1/gis/coverage') {
        return Response.json({ buckets: { operationalStatus: { ACTIVE: 9, INACTIVE: 3 } } });
      }
      if (url.pathname === '/api/v1/geographic-areas') {
        return Response.json(pageEnvelope([{ id: 'c0a80101-0000-4000-8000-000000000030', parentAreaId: null, code: 'MUM', name: 'Mumbai', areaType: 'CITY', status: 'ACTIVE' }]));
      }
      if (url.pathname === '/api/v1/organizations') return Response.json(pageEnvelope([]));
      return new Response(null, { status: 404 });
    });
    vi.stubGlobal('fetch', fetch);
    const user = userEvent.setup();

    renderApp('/reports');

    expect(await screen.findByText('12')).toBeVisible();
    expect(screen.getByText(/3 unreachable/i)).toBeVisible();
    const areaTrigger = await screen.findByRole('combobox', { name: /coverage geographic area/i });
    await user.click(areaTrigger);
    await user.click(await screen.findByRole('button', { name: /^mumbai/i }));
    await user.click(screen.getByRole('button', { name: /load coverage summary/i }));

    expect(await screen.findByText('ACTIVE')).toBeVisible();
    expect(screen.getByText('9')).toBeVisible();
    expect(fetch.mock.calls.map(([input]) => String(input))).toContainEqual(expect.stringContaining('geographicAreaId=c0a80101-0000-4000-8000-000000000030'));
    expect(screen.queryByLabelText(/coverage bounding box/i)).not.toBeInTheDocument();
    await waitFor(() => expect(fetch.mock.calls.map(([input]) => String(input))).not.toContainEqual(expect.stringContaining('/api/v1/gis/gaps')));
    expect(screen.getAllByText(/estimated planning aid/i)).not.toHaveLength(0);
  });

  it('requires an API-backed coverage selector before loading a summary', async () => {
    signIn();
    vi.stubGlobal('fetch', async (input: RequestInfo | URL) => {
      const url = new URL(String(input));
      if (url.pathname === '/api/v1/overview') return Response.json({
        targets: 10, activeTargets: 8, quarantinedTargets: 2, cameras: 12, unreachableCameras: 3,
      });
      if (url.pathname === '/api/v1/organizations' || url.pathname === '/api/v1/geographic-areas') return Response.json(pageEnvelope([]));
      return new Response(null, { status: 404 });
    });
    const user = userEvent.setup();

    renderApp('/reports');

    await screen.findByText('12');
    await user.click(screen.getByRole('button', { name: /load coverage summary/i }));

    const geographicArea = await screen.findByRole('combobox', { name: /coverage geographic area/i });
    expect(geographicArea).toHaveAttribute('aria-describedby', 'coverage-scope-error');
    expect(geographicArea).toHaveAttribute('aria-invalid', 'true');
    expect(screen.getByRole('alert')).toHaveAttribute('id', 'coverage-scope-error');
  });

  it('does not load coverage when an organization unit is selected without a geographic boundary', async () => {
    signIn();
    const fetch = vi.fn(async (input: RequestInfo | URL) => {
      const url = new URL(String(input));
      if (url.pathname === '/api/v1/overview') return Response.json({
        targets: 10, activeTargets: 8, quarantinedTargets: 2, cameras: 12, unreachableCameras: 3,
      });
      if (url.pathname === '/api/v1/organizations') return Response.json(pageEnvelope([
        { id: 'c0a80101-0000-4000-8000-000000000001', code: 'OPS', name: 'Operations', organizationType: 'PUBLIC', description: null, status: 'ACTIVE' },
      ]));
      if (url.pathname === '/api/v1/organizations/c0a80101-0000-4000-8000-000000000001/units') return Response.json(pageEnvelope([
        { id: 'c0a80101-0000-4000-8000-000000000010', organizationId: 'c0a80101-0000-4000-8000-000000000001', parentUnitId: null, code: 'NORTH', name: 'North Unit', unitType: 'REGION', status: 'ACTIVE' },
      ]));
      if (url.pathname === '/api/v1/geographic-areas') return Response.json(pageEnvelope([
        { id: 'c0a80101-0000-4000-8000-000000000030', parentAreaId: null, code: 'MUM', name: 'Mumbai', areaType: 'CITY', status: 'ACTIVE' },
      ]));
      return new Response(null, { status: 404 });
    });
    vi.stubGlobal('fetch', fetch);
    const user = userEvent.setup();

    renderApp('/reports');

    await screen.findByText('12');
    expect(screen.getByRole('group', { name: /optional organization-unit narrowing/i })).toBeVisible();
    expect(screen.queryByLabelText(/^coverage organization$/i)).not.toBeInTheDocument();
    await user.selectOptions(screen.getByLabelText(/^organization$/i), 'c0a80101-0000-4000-8000-000000000001');
    const unitTrigger = await screen.findByRole('combobox', { name: /coverage organization unit/i });
    await user.click(unitTrigger);
    await user.click(await screen.findByRole('button', { name: /^north unit/i }));
    await user.click(screen.getByRole('button', { name: /load coverage summary/i }));

    expect(screen.getByRole('alert')).toHaveTextContent(/select a geographic area/i);
    expect(fetch.mock.calls.map(([input]) => String(input))).not.toContainEqual(expect.stringContaining('/api/v1/gis/coverage'));
  });

  it('shows the full fleet summary including VMS target counts', async () => {
    signIn();
    vi.stubGlobal('fetch', async (input: RequestInfo | URL) => {
      const url = new URL(String(input));
      if (url.pathname === '/api/v1/overview') return Response.json({
        targets: 10, activeTargets: 8, quarantinedTargets: 2, cameras: 12, unreachableCameras: 3,
      });
      if (url.pathname === '/api/v1/organizations' || url.pathname === '/api/v1/geographic-areas') return Response.json(pageEnvelope([]));
      return new Response(null, { status: 404 });
    });

    renderApp('/reports');

    expect(await screen.findByText('12')).toBeVisible();
    expect(screen.getByText('8 / 10')).toBeVisible();
    expect(screen.getByText('2')).toBeVisible();
  });

  it('does not let a fleet-summary permission failure block the coverage report', async () => {
    signIn();
    vi.stubGlobal('fetch', async (input: RequestInfo | URL) => {
      const url = new URL(String(input));
      if (url.pathname === '/api/v1/overview') {
        return Response.json({ type: 'about:blank', title: 'Forbidden', status: 403, detail: "This action requires the 'vms.read' permission." }, { status: 403 });
      }
      if (url.pathname === '/api/v1/organizations' || url.pathname === '/api/v1/geographic-areas') return Response.json(pageEnvelope([]));
      return new Response(null, { status: 404 });
    });

    renderApp('/reports');

    expect(await screen.findByText(/vms.read/i)).toBeVisible();
    expect(screen.getByRole('button', { name: /retry fleet summary/i })).toBeVisible();
    expect(await screen.findByRole('combobox', { name: /coverage geographic area/i })).toBeVisible();
    expect(screen.getByRole('heading', { name: /coverage summary/i })).toBeVisible();
  });

  it('uses an organization unit only to narrow a selected geographic boundary', async () => {
    signIn();
    const fetch = vi.fn(async (input: RequestInfo | URL) => {
      const url = new URL(String(input));
      if (url.pathname === '/api/v1/overview') return Response.json({
        targets: 10, activeTargets: 8, quarantinedTargets: 2, cameras: 12, unreachableCameras: 3,
      });
      if (url.pathname === '/api/v1/organizations') return Response.json(pageEnvelope([
        { id: 'c0a80101-0000-4000-8000-000000000001', code: 'OPS', name: 'Operations', organizationType: 'PUBLIC', description: null, status: 'ACTIVE' },
      ]));
      if (url.pathname === '/api/v1/organizations/c0a80101-0000-4000-8000-000000000001/units') return Response.json(pageEnvelope([
        { id: 'c0a80101-0000-4000-8000-000000000010', organizationId: 'c0a80101-0000-4000-8000-000000000001', parentUnitId: null, code: 'NORTH', name: 'North Unit', unitType: 'REGION', status: 'ACTIVE' },
      ]));
      if (url.pathname === '/api/v1/geographic-areas') return Response.json(pageEnvelope([
        { id: 'c0a80101-0000-4000-8000-000000000030', parentAreaId: null, code: 'MUM', name: 'Mumbai', areaType: 'CITY', status: 'ACTIVE' },
      ]));
      if (url.pathname === '/api/v1/gis/coverage') return Response.json({ buckets: {} });
      return new Response(null, { status: 404 });
    });
    vi.stubGlobal('fetch', fetch);
    const user = userEvent.setup();

    renderApp('/reports');

    await screen.findByText('12');
    await user.selectOptions(screen.getByLabelText(/^organization$/i), 'c0a80101-0000-4000-8000-000000000001');
    const unitTrigger = await screen.findByRole('combobox', { name: /coverage organization unit/i });
    await user.click(unitTrigger);
    await user.click(await screen.findByRole('button', { name: /^north unit/i }));
    const areaTrigger = await screen.findByRole('combobox', { name: /coverage geographic area/i });
    await user.click(areaTrigger);
    await user.click(await screen.findByRole('button', { name: /^mumbai/i }));
    await user.click(screen.getByRole('button', { name: /load coverage summary/i }));

    await waitFor(() => expect(fetch.mock.calls.map(([input]) => String(input))).toContainEqual(expect.stringMatching(
      /\/api\/v1\/gis\/coverage\?(?=.*organizationUnitId=c0a80101-0000-4000-8000-000000000010)(?=.*geographicAreaId=c0a80101-0000-4000-8000-000000000030)/,
    )));
  });
});
