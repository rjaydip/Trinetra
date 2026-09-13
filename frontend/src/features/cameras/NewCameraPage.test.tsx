import '@testing-library/jest-dom/vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import { afterEach, expect, it, vi } from 'vitest';
import { App } from '../../App';
import { AuthProvider } from '../../auth/AuthProvider';
import { saveSession } from '../../auth/session';
import { pageEnvelope, sessionFixture } from '../../test/fixtures';

afterEach(() => { vi.unstubAllGlobals(); sessionStorage.clear(); });

// organizations/units/geographic-areas are fetched a page at a time (see fetchAllPages in
// endpoints.ts) and need the `{ items, page, pageSize, total, totalPages }` envelope; everything
// else this file stubs (vms, gis) still expects a bare array.
const REFERENCE_LIST_PATH = /\/organizations$|\/organizations\/[^/]+\/units$|\/geographic-areas$/;
function emptyListResponse(pathname: string) {
  return REFERENCE_LIST_PATH.test(pathname) ? Response.json(pageEnvelope([])) : Response.json([]);
}

const organizationId = 'c0a80101-0000-4000-8000-000000000001';
const organization = { id: organizationId, code: 'OPS', name: 'Operations', organizationType: 'PUBLIC', description: null, status: 'ACTIVE' };
const organizationUnit = { id: 'c0a80101-0000-4000-8000-000000000010', organizationId, parentUnitId: null, code: 'NORTH', name: 'North Unit', unitType: 'REGION', status: 'ACTIVE' };
const area = { id: 'c0a80101-0000-4000-8000-000000000020', parentAreaId: null, code: 'HQ', name: 'Headquarters', areaType: 'DISTRICT', status: 'ACTIVE' };

it('shows a retryable organization failure beside the selector', async () => {
  saveSession(sessionFixture('registrar', ['camera.create']));
  let organizationsUnavailable = true;
  vi.stubGlobal('fetch', async (input: RequestInfo | URL) => {
    const pathname = new URL(String(input)).pathname;
    if (pathname.endsWith('/organizations') && organizationsUnavailable) {
      return Response.json({ title: 'Unavailable', detail: 'Reference data is temporarily unavailable.' }, { status: 503 });
    }
    if (pathname.endsWith('/organizations')) return Response.json(pageEnvelope([organization]));
    return emptyListResponse(pathname);
  });
  render(<MemoryRouter initialEntries={['/cameras/new']}><AuthProvider><App /></AuthProvider></MemoryRouter>);
  const message = await screen.findByText('Reference data is temporarily unavailable.');
  expect(message.closest('[role="status"]')).toHaveAttribute('aria-live', 'polite');
  expect(screen.getByLabelText(/^organization$/i)).toBeDisabled();
  organizationsUnavailable = false;
  await userEvent.click(screen.getByRole('button', { name: /retry organizations/i }));
  expect(await screen.findByRole('option', { name: /operations/i })).toBeVisible();
});

it('preserves entered camera values while organization units fail and retry', async () => {
  saveSession(sessionFixture('registrar', ['camera.create']));
  let unitsUnavailable = true;
  vi.stubGlobal('fetch', async (input: RequestInfo | URL) => {
    const url = new URL(String(input));
    if (url.pathname === '/api/v1/organizations') return Response.json(pageEnvelope([organization]));
    if (url.pathname === `/api/v1/organizations/${organizationId}/units`) {
      if (unitsUnavailable) return Response.json({ title: 'Unavailable', detail: 'Organization units are temporarily unavailable.' }, { status: 503 });
      return Response.json(pageEnvelope([organizationUnit]));
    }
    if (url.pathname === '/api/v1/geographic-areas') return Response.json(pageEnvelope([area]));
    if (url.pathname === '/api/v1/vms') return Response.json([]);
    return new Response(null, { status: 404 });
  });
  const user = userEvent.setup();

  render(<MemoryRouter initialEntries={['/cameras/new']}><AuthProvider><App /></AuthProvider></MemoryRouter>);

  const cameraCode = await screen.findByLabelText(/camera code/i);
  await user.type(cameraCode, 'CAM-PRESERVED');
  await user.type(screen.getByLabelText(/^name/i), 'Preserved camera');
  await user.selectOptions(screen.getByLabelText(/^organization$/i), organizationId);

  const error = await screen.findByText('Organization units are temporarily unavailable.');
  expect(error.closest('[role="status"]')).toHaveAttribute('aria-live', 'polite');
  expect(screen.getByLabelText(/camera code/i)).toHaveValue('CAM-PRESERVED');
  expect(screen.getByLabelText(/^name/i)).toHaveValue('Preserved camera');
  expect(screen.getByLabelText(/^organization unit/i)).toBeDisabled();

  unitsUnavailable = false;
  await user.click(screen.getByRole('button', { name: /retry organization units/i }));

  const organizationUnitTrigger = await screen.findByLabelText(/^organization unit/i);
  expect(organizationUnitTrigger).toBeEnabled();
  await user.click(organizationUnitTrigger);
  expect(await screen.findByRole('button', { name: /north unit/i })).toBeVisible();
  expect(screen.getByLabelText(/camera code/i)).toHaveValue('CAM-PRESERVED');
  expect(screen.getByLabelText(/^name/i)).toHaveValue('Preserved camera');
});

it('explains why organization and organization-unit selectors are unavailable when results are empty', async () => {
  saveSession(sessionFixture('registrar', ['camera.create']));
  vi.stubGlobal('fetch', async (input: RequestInfo | URL) => {
    const url = new URL(String(input));
    if (url.pathname === '/api/v1/geographic-areas') return Response.json(pageEnvelope([area]));
    return emptyListResponse(url.pathname);
  });

  render(<MemoryRouter initialEntries={['/cameras/new']}><AuthProvider><App /></AuthProvider></MemoryRouter>);

  expect(await screen.findByLabelText(/^organization$/i)).toBeDisabled();
  const organizationStatus = await screen.findByText(/no organizations are available/i);
  expect(organizationStatus.closest('[role="status"]')).toHaveAttribute('aria-live', 'polite');
  expect(screen.getByLabelText(/^organization unit/i)).toBeDisabled();
  expect(screen.getByText(/select an organization to load its organization units/i)).toBeVisible();
});

it('explains why an organization unit is unavailable when the selected organization has none', async () => {
  saveSession(sessionFixture('registrar', ['camera.create']));
  vi.stubGlobal('fetch', async (input: RequestInfo | URL) => {
    const url = new URL(String(input));
    if (url.pathname === '/api/v1/organizations') return Response.json(pageEnvelope([organization]));
    if (url.pathname === '/api/v1/geographic-areas') return Response.json(pageEnvelope([area]));
    return emptyListResponse(url.pathname);
  });
  const user = userEvent.setup();

  render(<MemoryRouter initialEntries={['/cameras/new']}><AuthProvider><App /></AuthProvider></MemoryRouter>);
  await screen.findByRole('option', { name: /operations/i });
  await user.selectOptions(screen.getByLabelText(/^organization$/i), organizationId);

  expect(await screen.findByLabelText(/^organization unit/i)).toBeDisabled();
  const unitStatus = screen.getByText(/no organization units are available for this organization/i);
  expect(unitStatus.closest('[role="status"]')).toHaveAttribute('aria-live', 'polite');
  expect(unitStatus).toHaveTextContent(/choose another organization or ask an administrator/i);
});

it('explains why the required geographic area selector is unavailable when none exist', async () => {
  saveSession(sessionFixture('registrar', ['camera.create']));
  vi.stubGlobal('fetch', async (input: RequestInfo | URL) => {
    const url = new URL(String(input));
    if (url.pathname === '/api/v1/organizations') return Response.json(pageEnvelope([organization]));
    return emptyListResponse(url.pathname);
  });

  render(<MemoryRouter initialEntries={['/cameras/new']}><AuthProvider><App /></AuthProvider></MemoryRouter>);

  expect(await screen.findByLabelText(/^geographic area/i)).toBeDisabled();
  const areaStatus = await screen.findByText(/no geographic areas are available/i);
  expect(areaStatus.closest('[role="status"]')).toHaveAttribute('aria-live', 'polite');
  expect(areaStatus).toHaveTextContent(/ask an administrator to create one/i);
});

it('loads map context only with a bounded bbox after coordinates are valid', async () => {
  saveSession(sessionFixture('registrar', ['camera.create']));
  const fetchMock = vi.fn(async (input: RequestInfo | URL) => {
    if (String(input).includes('/gis/cameras')) return Response.json({ type: 'FeatureCollection', features: [] });
    return emptyListResponse(new URL(String(input)).pathname);
  });
  vi.stubGlobal('fetch', fetchMock);
  render(<MemoryRouter initialEntries={['/cameras/new']}><AuthProvider><App /></AuthProvider></MemoryRouter>);

  const latitude = await screen.findByLabelText(/^Latitude/i);
  expect(fetchMock.mock.calls.some(([input]) => String(input).includes('/gis/cameras'))).toBe(false);

  await userEvent.type(latitude, '19.076');
  expect(fetchMock.mock.calls.some(([input]) => String(input).includes('/gis/cameras'))).toBe(false);
  await userEvent.type(screen.getByLabelText(/^Longitude/i), '72.8777');

  await waitFor(() => expect(fetchMock.mock.calls.some(([input]) => String(input).includes('/gis/cameras'))).toBe(true));
  const gisUrls = fetchMock.mock.calls.map(([input]) => String(input)).filter((url) => url.includes('/gis/cameras'));
  for (const url of gisUrls) {
    const bbox = new URL(url, 'http://localhost').searchParams.get('bbox')?.split(',').map(Number);
    expect(bbox).toHaveLength(4);
    expect(bbox![2] - bbox![0]).toBeLessThanOrEqual(0.02);
    expect(bbox![3] - bbox![1]).toBeLessThanOrEqual(0.02);
  }
});
