import '@testing-library/jest-dom/vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { App } from '../../App';
import { AuthProvider } from '../../auth/AuthProvider';
import { readFileSync } from 'node:fs';
import { sessionFixture } from '../../test/fixtures';

function renderApp(path: string) {
  return render(<MemoryRouter initialEntries={[path]}><AuthProvider><App /></AuthProvider></MemoryRouter>);
}

function signIn() {
  sessionStorage.setItem('trinetra.auth.session', JSON.stringify(sessionFixture('importer', 'camera.import')));
}

const emptyPage = { items: [], page: 1, pageSize: 200, total: 0, totalPages: 1 };

/** Handles the reference-data lookup `BulkImportPage` fires on mount (organizations, that
 * organization's units, geographic areas, saved credentials — see `loadImportReferenceData`) so
 * every test doesn't have to stub it individually; `overrides` lets a test substitute non-empty
 * lists (to exercise CSV name resolution) or a specific `POST /cameras/bulk-import` response.
 * Returns the underlying `vi.fn()` so a test can inspect which paths were actually hit. */
function stubFetch(overrides: Partial<{
  organizations: unknown[];
  organizationUnits: Record<string, unknown[]>;
  geographicAreas: unknown[];
  credentials: unknown[];
  bulkImport: () => Response | Promise<Response>;
}> = {}) {
  const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = new URL(String(input instanceof Request ? input.url : input));
    const method = init?.method ?? 'GET';

    if (url.pathname === '/api/v1/organizations') {
      return Response.json({ ...emptyPage, items: overrides.organizations ?? [] });
    }
    const unitsMatch = /^\/api\/v1\/organizations\/([^/]+)\/units$/.exec(url.pathname);
    if (unitsMatch) {
      return Response.json({ ...emptyPage, items: overrides.organizationUnits?.[unitsMatch[1]] ?? [] });
    }
    if (url.pathname === '/api/v1/geographic-areas') {
      return Response.json({ ...emptyPage, items: overrides.geographicAreas ?? [] });
    }
    if (url.pathname === '/api/v1/credential-library') {
      return Response.json(overrides.credentials ?? []);
    }
    if (url.pathname === '/api/v1/cameras/bulk-import' && method === 'POST') {
      if (overrides.bulkImport) return overrides.bulkImport();
      return Response.json({ created: 0, updated: 0, failed: 0, rows: [] });
    }
    return new Response(null, { status: 404 });
  });
  vi.stubGlobal('fetch', fetchMock);
  return fetchMock;
}

function wasBulkImportCalled(fetchMock: ReturnType<typeof vi.fn>) {
  return fetchMock.mock.calls.some(([input, init]) => {
    const url = new URL(String((input as RequestInfo | URL) instanceof Request ? (input as Request).url : input));
    return url.pathname === '/api/v1/cameras/bulk-import' && ((init as RequestInit | undefined)?.method ?? 'GET') === 'POST';
  });
}

async function uploadJson(name: string, items: unknown[]) {
  const user = userEvent.setup();
  const file = new File([JSON.stringify({ mode: 'insert', items })], name, { type: 'application/json' });
  await user.upload(await screen.findByLabelText(/import csv or json file/i), file);
  return user;
}

async function uploadCsv(name: string, csv: string) {
  const user = userEvent.setup();
  const file = new File([csv], name, { type: 'text/csv' });
  await user.upload(await screen.findByLabelText(/import csv or json file/i), file);
  return user;
}

afterEach(() => {
  vi.unstubAllGlobals();
  sessionStorage.clear();
});

describe('BulkImportPage', () => {
  it('downloads the contract-valid JSON sample and revokes its temporary URL', async () => {
    signIn();
    stubFetch();
    const createObjectURL = vi.fn(() => 'blob:camera-import-sample');
    const revokeObjectURL = vi.fn();
    vi.stubGlobal('URL', { createObjectURL, revokeObjectURL });
    const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => undefined);
    const user = userEvent.setup();

    renderApp('/cameras/import');
    await user.click(await screen.findByRole('button', { name: /download JSON template/i }));

    expect(createObjectURL).toHaveBeenCalledWith(expect.any(Blob));
    expect(click).toHaveBeenCalled();
    expect(revokeObjectURL).toHaveBeenCalledWith('blob:camera-import-sample');
    click.mockRestore();
  });

  it('keeps import preview and server row failures visible with the mobile stylesheet', async () => {
    signIn();
    stubFetch({ bulkImport: () => Response.json({ created: 0, updated: 0, failed: 1,
      rows: [{ index: 0, cameraCode: 'MOBILE', status: 'error', cameraId: null, error: 'Site is outside your permitted scope.' }],
    }) });
    vi.stubGlobal('innerWidth', 375);
    const stylesheet = document.createElement('style');
    stylesheet.textContent = readFileSync(`${process.cwd()}/src/styles.css`, 'utf8');
    document.head.append(stylesheet);
    try {
      renderApp('/cameras/import');
      const user = await uploadJson('cameras.json', [{ cameraCode: 'MOBILE' }]);
      expect(await screen.findByText('MOBILE')).toBeVisible();
      await user.click(screen.getByRole('button', { name: /import cameras/i }));
      expect(await screen.findByText('Site is outside your permitted scope.')).toBeVisible();
    } finally { stylesheet.remove(); }
  });

  it('shows row errors supplied by the bulk import result', async () => {
    signIn();
    stubFetch({ bulkImport: () => Response.json({
      created: 0, updated: 0, failed: 1,
      rows: [{ index: 0, cameraCode: 'BAD', status: 'error', cameraId: null, error: 'cameraType is required.' }],
    }) });

    renderApp('/cameras/import');
    const user = await uploadJson('cameras.json', [{ cameraCode: 'BAD' }]);
    await user.click(await screen.findByRole('button', { name: /import cameras/i }));

    expect(await screen.findByText(/cameraType is required/i)).toBeVisible();
  });

  it('rejects uploads that are neither .csv nor .json before submitting', async () => {
    signIn();
    const fetchMock = stubFetch();
    renderApp('/cameras/import');
    const user = userEvent.setup({ applyAccept: false });
    await user.upload(await screen.findByLabelText(/import csv or json file/i), new File(['cameraCode,name'], 'cameras.txt', { type: 'text/plain' }));

    expect(await screen.findByText(/select a \.csv or \.json file/i)).toBeVisible();
    expect(wasBulkImportCalled(fetchMock)).toBe(false);
  });

  it('downloads the CSV sample templates', async () => {
    signIn();
    stubFetch();
    const createObjectURL = vi.fn(() => 'blob:camera-import-sample');
    const revokeObjectURL = vi.fn();
    vi.stubGlobal('URL', { createObjectURL, revokeObjectURL });
    const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => undefined);
    const user = userEvent.setup();

    renderApp('/cameras/import');
    await user.click(await screen.findByRole('button', { name: /download csv template/i }));

    expect(createObjectURL).toHaveBeenCalledWith(expect.any(Blob));
    expect(click).toHaveBeenCalled();
    expect(revokeObjectURL).toHaveBeenCalledWith('blob:camera-import-sample');
    click.mockRestore();
  });

  it('resolves CSV organization unit / geographic area names into the same preview a JSON upload produces', async () => {
    signIn();
    stubFetch({
      organizations: [{ id: 'org-1', code: 'ORG1', name: 'Org One', organizationType: 'STATE', description: null, status: 'ACTIVE' }],
      organizationUnits: { 'org-1': [{ id: 'unit-1', organizationId: 'org-1', parentUnitId: null, code: 'U1', name: 'North Gate Unit', unitType: 'DIVISION', status: 'ACTIVE' }] },
      geographicAreas: [{ id: 'area-1', parentAreaId: null, code: 'A1', name: 'North Zone', areaType: 'ZONE', status: 'ACTIVE' }],
    });
    renderApp('/cameras/import');

    const csv = 'cameraCode,name,organizationUnit,geographicArea,cameraType,latitude,longitude\r\n'
      + 'CAM-CSV-1,North Gate,North Gate Unit,North Zone,FIXED,19.076,72.8777\r\n';
    await uploadCsv('cameras.csv', csv);

    expect(await screen.findByText('CAM-CSV-1')).toBeVisible();
    expect(await screen.findByText('North Gate Unit')).toBeVisible();
    expect(await screen.findByText('North Zone')).toBeVisible();
    expect(await screen.findByRole('button', { name: /import cameras/i })).toBeVisible();
    expect(screen.queryByText(/did not match exactly one known/i)).not.toBeInTheDocument();
  });

  it('leaves an unresolved organization unit / geographic area name blank and warns, without failing the whole file', async () => {
    signIn();
    stubFetch();
    renderApp('/cameras/import');

    const csv = 'cameraCode,name,organizationUnit,geographicArea,cameraType,latitude,longitude\r\n'
      + 'CAM-CSV-1,North Gate,No Such Unit,No Such Zone,FIXED,19.076,72.8777\r\n';
    await uploadCsv('cameras.csv', csv);

    expect(await screen.findByText('CAM-CSV-1')).toBeVisible();
    expect(await screen.findByText(/"No Such Unit" did not match exactly one known organization unit/i)).toBeVisible();
    expect(await screen.findByText(/"No Such Zone" did not match exactly one known geographic area/i)).toBeVisible();
    // Left blank, not the raw unresolved name — the whole row still previews for the rest of its
    // fields, one "Not supplied" cell each for the organization unit and geographic area columns.
    expect(screen.getAllByText('Not supplied')).toHaveLength(2);
    expect(await screen.findByRole('button', { name: /import cameras/i })).toBeVisible();
  });

  it('reports the row number for an invalid CSV cell', async () => {
    signIn();
    stubFetch();
    renderApp('/cameras/import');

    const csv = 'cameraCode,vmsId\r\nCAM-1,not-a-uuid\r\n';
    await uploadCsv('cameras.csv', csv);

    expect(await screen.findByText(/row 2.*VMS ID must be a valid UUID/i)).toBeVisible();
  });

  it('rejects a CSV header with an unrecognized column', async () => {
    signIn();
    stubFetch();
    renderApp('/cameras/import');

    await uploadCsv('cameras.csv', 'cameraCode,bogusColumn\r\nCAM-1,x\r\n');

    expect(await screen.findByText(/unrecognized column\(s\).*bogusColumn/i)).toBeVisible();
  });

  it('previews only a locally valid 1–500 item import envelope', async () => {
    signIn();
    stubFetch();
    renderApp('/cameras/import');
    await uploadJson('cameras.json', []);

    expect(await screen.findByText(/between 1 and 500 items/i)).toBeVisible();
    expect(screen.queryByRole('button', { name: /import cameras/i })).not.toBeInTheDocument();
  });

  it.each([
    [{ vmsId: 'not-a-uuid' }, /VMS ID must be a valid UUID/i],
    [{ installationDate: '2026-02-30' }, /Installation date must use the YYYY-MM-DD format/i],
  ])('rejects malformed optional request values before sending: %o', async (item, message) => {
    signIn();
    const fetchMock = stubFetch();
    renderApp('/cameras/import');

    await uploadJson('cameras.json', [item]);

    expect(await screen.findByText(message)).toBeVisible();
    expect(screen.queryByRole('button', { name: /import cameras/i })).not.toBeInTheDocument();
    expect(wasBulkImportCalled(fetchMock)).toBe(false);
  });

  it('previews a .NET Guid VMS ID for server row validation', async () => {
    signIn();
    stubFetch();
    renderApp('/cameras/import');

    await uploadJson('cameras.json', [{ vmsId: '00000000-0000-0000-0000-000000000001' }]);

    expect(await screen.findByRole('button', { name: /import cameras/i })).toBeVisible();
  });
});
