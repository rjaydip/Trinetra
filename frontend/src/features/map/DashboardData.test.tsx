import '@testing-library/jest-dom/vitest';
import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import { afterEach, expect, it, vi } from 'vitest';
import { App } from '../../App';
import { AuthProvider } from '../../auth/AuthProvider';
import { saveSession } from '../../auth/session';
import { cameraFixture, sessionFixture } from '../../test/fixtures';

afterEach(() => { vi.unstubAllGlobals(); sessionStorage.clear(); });

function renderDashboard(overviewStatus = 200, emptyRegistry = false) {
  saveSession(sessionFixture('dashboard', ['vms.read', 'camera.read']));
  vi.stubGlobal('fetch', async (input: RequestInfo | URL) => {
    const url = new URL(String(input));
    if (url.pathname === '/api/v1/overview') return overviewStatus === 200
      ? Response.json({ targets: 12, activeTargets: 9, quarantinedTargets: 3, cameras: 247, unreachableCameras: 17 })
      : Response.json({ title: 'Forbidden', detail: 'Fleet summary unavailable.' }, { status: overviewStatus });
    if (url.pathname === '/api/v1/cameras') {
      return Response.json({ items: emptyRegistry ? [] : [cameraFixture({ name: url.searchParams.get('q') === 'South gate' ? 'South search result' : 'North viewport camera' })], nextCursor: null });
    }
    if (url.pathname === '/api/v1/gis/cameras' && !url.searchParams.has('q')) return Response.json({ type: 'FeatureCollection', features: [{ type: 'Feature', geometry: { type: 'Point', coordinates: [77.5946, 12.9716] }, properties: { cameraId: cameraFixture().id, name: 'North viewport camera' } }] });
    return new Response(null, { status: 400 });
  });
  render(<MemoryRouter initialEntries={['/dashboard']}><AuthProvider><App /></AuthProvider></MemoryRouter>);
}

it('searches the registry q endpoint without claiming to filter GIS cameras', async () => {
  renderDashboard();
  await screen.findByRole('button', { name: /open camera North viewport camera/i });
  const user = userEvent.setup();
  await user.type(screen.getByLabelText(/search registry/i), 'South gate');
  await user.click(screen.getByRole('button', { name: /^search cameras$/i }));
  expect(await screen.findByRole('link', { name: 'South search result' })).toHaveAttribute('href', `/cameras/${cameraFixture().id}`);
  expect(screen.getByRole('button', { name: /open camera North viewport camera/i })).toBeVisible();
  expect(screen.getByText(/search does not filter the map/i)).toBeVisible();
});

it('renders real overview counters with scope distinct from registry and map', async () => {
  renderDashboard();
  const summary = await screen.findByRole('region', { name: /fleet summary/i });
  expect(await within(summary).findByText('247')).toBeVisible();
  expect(within(summary).getByText('17')).toBeVisible();
  expect(within(summary).getByText(/VMS.*scope.*map/i)).toBeVisible();
});

it('does not invent zero totals when the summary endpoint is forbidden', async () => {
  renderDashboard(403);
  const summary = await screen.findByRole('region', { name: /fleet summary/i });
  expect(await within(summary).findByText(/unavailable for your permissions/i)).toBeVisible();
  expect(within(summary).queryByText(/^0$/)).not.toBeInTheDocument();
  expect(await screen.findByRole('button', { name: /open camera North viewport camera/i })).toBeVisible();
});

it('keeps search and counters available when the registry has no coordinates', async () => {
  renderDashboard(200, true);
  expect(await screen.findByRole('heading', { name: /no camera coordinates/i })).toBeVisible();
  expect(screen.getByLabelText(/search registry/i)).toBeVisible();
  expect(await screen.findByText('247')).toBeVisible();
});
