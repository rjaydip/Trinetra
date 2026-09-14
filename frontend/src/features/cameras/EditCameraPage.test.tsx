import '@testing-library/jest-dom/vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import { afterEach, expect, it, vi } from 'vitest';

import { App } from '../../App';
import { AuthProvider } from '../../auth/AuthProvider';
import { saveSession } from '../../auth/session';
import { cameraFixture, organizationFixture, pageEnvelope, sessionFixture } from '../../test/fixtures';

afterEach(() => { vi.unstubAllGlobals(); sessionStorage.clear(); });

const cameraId = 'c0a80101-0000-4000-8000-000000000001';
const organizationId = 'c0a80101-0000-4000-8000-000000000099';
const organizationUnitId = 'c0a80101-0000-4000-8000-000000000010';
const areaId = 'c0a80101-0000-4000-8000-000000000020';

const organization = organizationFixture({ id: organizationId });
const organizationUnit = { id: organizationUnitId, organizationId, parentUnitId: null, code: 'NORTH', name: 'North Unit', unitType: 'REGION', status: 'ACTIVE' };
const area = { id: areaId, parentAreaId: null, code: 'HQ', name: 'Headquarters', areaType: 'DISTRICT', status: 'ACTIVE' };
// `manufacturer`/`ipAddress`/`protocol`/`port` are required by the form schema whenever no VMS
// is linked (manual registration) — filled here so the update submits cleanly. Status fields must
// use the form's actual vocabulary (see `cameraVocabulary.ts`), which matches the `v1.6` schema
// CHECK constraints — not `cameraFixture`'s own default placeholder values.
const camera = cameraFixture({
  id: cameraId, cameraCode: 'CAM-001', name: 'North Gate', organizationUnitId, geographicAreaId: areaId,
  manufacturer: 'Axis', ipAddress: '10.0.0.8', port: 554, protocol: 'RTSP',
  operationalStatus: 'ONLINE', connectivityStatus: 'CONNECTED', maintenanceStatus: 'NORMAL',
});

function stubFetch(onPatch?: (id: string, body: unknown) => Response) {
  vi.stubGlobal('fetch', async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = new URL(String(input), 'http://localhost');
    if (url.pathname === `/api/v1/cameras/${cameraId}` && init?.method === 'PATCH') {
      const body: unknown = JSON.parse(String(init.body));
      return onPatch ? onPatch(cameraId, body) : Response.json({ ...camera, ...(body as Partial<typeof camera>) });
    }
    if (url.pathname === `/api/v1/cameras/${cameraId}`) return Response.json(camera);
    if (url.pathname === `/api/v1/organization-units/${organizationUnitId}`) return Response.json(organizationUnit);
    if (url.pathname === '/api/v1/organizations') return Response.json(pageEnvelope([organization]));
    if (url.pathname === `/api/v1/organizations/${organizationId}/units`) return Response.json(pageEnvelope([organizationUnit]));
    if (url.pathname === '/api/v1/geographic-areas') return Response.json(pageEnvelope([area]));
    if (url.pathname === '/api/v1/vms') return Response.json([]);
    return new Response(null, { status: 404 });
  });
}

it('loads the existing camera into the form, pre-scoped to its organization', async () => {
  saveSession(sessionFixture('editor', ['camera.update']));
  stubFetch();

  render(<MemoryRouter initialEntries={[`/cameras/${cameraId}/edit`]}><AuthProvider><App /></AuthProvider></MemoryRouter>);

  expect(await screen.findByDisplayValue('North Gate')).toBeInTheDocument();
  expect(await screen.findByRole('option', { name: /operations/i, hidden: true })).toBeDefined();
  await userEvent.click(screen.getByLabelText(/^organization unit/i));
  expect(await screen.findByRole('button', { name: /north unit/i })).toBeVisible();
});

it('submits a change via PATCH with the camera id and the updated body', async () => {
  saveSession(sessionFixture('editor', ['camera.update']));
  let patchedId: string | undefined;
  let patchBody: unknown;
  stubFetch((id, body) => {
    patchedId = id;
    patchBody = body;
    return Response.json({ ...camera, name: 'North Gate Updated' });
  });
  const user = userEvent.setup();

  render(<MemoryRouter initialEntries={[`/cameras/${cameraId}/edit`]}><AuthProvider><App /></AuthProvider></MemoryRouter>);

  const nameInput = await screen.findByDisplayValue('North Gate');
  await user.clear(nameInput);
  await user.type(nameInput, 'North Gate Updated');
  await user.click(screen.getByRole('button', { name: /save details/i }));

  await waitFor(() => expect(patchedId).toBe(cameraId));
  expect(patchBody).toMatchObject({ name: 'North Gate Updated' });
});
