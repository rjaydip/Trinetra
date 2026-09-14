import '@testing-library/jest-dom/vitest';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render as rtlRender, screen, waitFor, type RenderResult } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ReactElement } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { ApiProblem } from '../../api/client';
import { CameraForm, toCameraWriteRequest, type CameraFormValues } from './CameraForm';

const emptySelectors = { organizations: [], vms: [], onOrganizationChange: vi.fn() };
const cameraId = 'c0a80101-0000-4000-8000-000000000099';

/** `CameraForm` now renders `CredentialLibraryPicker`, which needs a QueryClient — every test
 * still just calls `render(<CameraForm .../>)`, so the provider is wired in here once. */
function render(ui: ReactElement): RenderResult {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return rtlRender(<QueryClientProvider client={queryClient}>{ui}</QueryClientProvider>);
}

function validForm(overrides: Partial<CameraFormValues> = {}): CameraFormValues {
  return {
    cameraCode: 'CAM-001',
    name: 'North Gate',
    organizationId: 'c0a80101-0000-4000-8000-000000000001',
    organizationUnitId: 'c0a80101-0000-4000-8000-000000000010',
    geographicAreaId: 'c0a80101-0000-4000-8000-000000000020',
    cameraType: 'FIXED',
    latitude: '12.9716',
    longitude: '77.5946',
    manufacturer: 'Axis', model: '', serialNumber: '', altitude: '', mountingHeight: '',
    azimuth: '', tilt: '', horizontalFov: '', verticalFov: '', effectiveRange: '',
    ipAddress: '10.0.0.8', port: '554', protocol: 'RTSP', username: '', password: '', recordEvents: true, vmsId: '', streamReference: '',
    streamPreference: 'RTSP', nativeHlsUrl: '', nativeWebrtcUrl: '',
    installationDate: '', operationalStatus: '', connectivityStatus: '', maintenanceStatus: '',
    ...overrides,
  };
}

/** Stubs the create → credential-test (→ poll) sequence the "Test connection" button drives.
 * `authOutcome` controls what the server reports; `onCreate`/`onUpdate` let a test observe which
 * camera write happened. */
function stubCredentialTestFlow({
  authOutcome = 'authenticated' as 'authenticated' | 'not_verifiable' | 'credential_rejected' | 'unreachable',
  detail,
  onCreate,
  onUpdate,
}: {
  authOutcome?: 'authenticated' | 'not_verifiable' | 'credential_rejected' | 'unreachable';
  detail?: string;
  onCreate?: () => void;
  onUpdate?: () => void;
} = {}) {
  vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const path = new URL(String(input)).pathname;
    if (path === '/api/v1/cameras' && init?.method === 'POST') {
      onCreate?.();
      return Response.json({ id: cameraId }, { status: 201 });
    }
    if (path === `/api/v1/cameras/${cameraId}` && init?.method === 'PATCH') {
      onUpdate?.();
      return Response.json({ id: cameraId }, { status: 200 });
    }
    if (path === `/api/v1/cameras/${cameraId}/credential` && init?.method === 'PUT') {
      return Response.json({ sealed: true }, { status: 200 });
    }
    if (path === `/api/v1/cameras/${cameraId}/credential-test` && init?.method === 'POST') {
      return Response.json({ testId: 'ct1', status: 'pending', statusUrl: `/api/v1/cameras/${cameraId}/credential-test/ct1` }, { status: 202 });
    }
    if (path === `/api/v1/cameras/${cameraId}/credential-test/ct1`) {
      return Response.json({
        id: 'ct1', cameraId, protocol: 'RTSP', ipAddress: '10.0.0.8', port: 554, status: 'completed',
        requestedAt: '2026-09-13T09:00:00Z', completedAt: '2026-09-13T09:00:01Z', failureReason: null,
        result: { reachable: authOutcome !== 'unreachable', authOutcome, detail },
      });
    }
    return new Response(null, { status: 404 });
  }));
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('CameraForm', () => {
  it('does not include an empty optional port in the request', () => {
    expect(toCameraWriteRequest(validForm({ port: '' })).port).toBeUndefined();
  });

  it('rounds coordinates to seven decimal places in the request', () => {
    expect(toCameraWriteRequest(validForm({ latitude: '19.076012345', longitude: '-72.123456789' }))).toMatchObject({
      latitude: 19.0760123,
      longitude: -72.1234568,
    });
  });

  it('starts on the connect step with identity, location, and network fields plus a test-connection control', () => {
    render(<CameraForm {...emptySelectors} onSubmit={vi.fn()} organizationUnits={[]} geographicAreas={[]} />);

    expect(screen.getByLabelText(/^camera code/i)).toBeVisible();
    expect(screen.getByLabelText(/^name/i)).toBeVisible();
    expect(screen.getByLabelText(/^latitude/i)).toBeVisible();
    expect(screen.getByLabelText(/^protocol/i)).toBeVisible();
    expect(screen.getByLabelText(/^ip address/i)).toBeVisible();
    expect(screen.getByLabelText(/^port/i)).toBeVisible();
    expect(screen.getByRole('button', { name: /^test connection$/i })).toBeVisible();
    expect(screen.queryByLabelText(/^manufacturer/i)).not.toBeInTheDocument();
  });

  it('collects a real username and password on the connect step, sealed after the credential test rather than stored as plain text', () => {
    render(<CameraForm {...emptySelectors} onSubmit={vi.fn()} organizationUnits={[]} geographicAreas={[]} />);

    expect(screen.getByLabelText(/^username/i)).toBeVisible();
    expect(screen.getByLabelText(/^password/i)).toHaveAttribute('type', 'password');
    expect(screen.queryByLabelText(/credential reference/i)).not.toBeInTheDocument();
  });

  it('disables Test connection until the backend-required fields are filled', () => {
    render(<CameraForm {...emptySelectors} onSubmit={vi.fn()} organizationUnits={[]} geographicAreas={[]} />);
    expect(screen.getByRole('button', { name: /^test connection$/i })).toBeDisabled();
  });

  it('enables Test connection once every backend-required field is filled', () => {
    render(<CameraForm {...emptySelectors} onSubmit={vi.fn()} organizationUnits={[]} geographicAreas={[]} initialValues={validForm()} />);
    expect(screen.getByRole('button', { name: /^test connection$/i })).toBeEnabled();
  });

  it('creates the camera, saves the credential, tests it, and advances on a successful authentication', async () => {
    let created = false;
    stubCredentialTestFlow({ authOutcome: 'authenticated', onCreate: () => { created = true; } });
    const user = userEvent.setup();
    render(<CameraForm {...emptySelectors} onSubmit={vi.fn()} organizationUnits={[]} geographicAreas={[]} initialValues={validForm({ username: 'operator', password: 'device-secret' })} />);

    await user.click(screen.getByRole('button', { name: /^test connection$/i }));

    expect(await screen.findByLabelText(/^manufacturer/i)).toBeVisible();
    expect(created).toBe(true);
    expect(screen.getByRole('button', { name: /save details/i })).toBeVisible();
  });

  it('warns but allows the operator to proceed when the credential cannot be verified', async () => {
    stubCredentialTestFlow({ authOutcome: 'not_verifiable', detail: 'This protocol cannot authenticate a credential.' });
    const user = userEvent.setup();
    render(<CameraForm {...emptySelectors} onSubmit={vi.fn()} organizationUnits={[]} geographicAreas={[]} initialValues={validForm()} />);

    await user.click(screen.getByRole('button', { name: /^test connection$/i }));

    expect(await screen.findByText(/credential not verified/i)).toBeVisible();
    expect(screen.getByText(/this protocol cannot authenticate a credential/i)).toBeVisible();
    expect(screen.queryByLabelText(/^manufacturer/i)).not.toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: /continue anyway/i }));
    expect(screen.getByLabelText(/^manufacturer/i)).toBeVisible();
  });

  it('does not advance when the credential is rejected, and lets the operator retry against the same camera', async () => {
    let createCount = 0;
    stubCredentialTestFlow({ authOutcome: 'credential_rejected', detail: 'The device rejected this credential.', onCreate: () => { createCount += 1; } });
    const user = userEvent.setup();
    render(<CameraForm {...emptySelectors} onSubmit={vi.fn()} organizationUnits={[]} geographicAreas={[]} initialValues={validForm()} />);

    await user.click(screen.getByRole('button', { name: /^test connection$/i }));
    expect(await screen.findByText(/credential rejected/i)).toBeVisible();
    expect(screen.queryByLabelText(/^manufacturer/i)).not.toBeInTheDocument();
    expect(createCount).toBe(1);

    let updated = false;
    stubCredentialTestFlow({ authOutcome: 'authenticated', onCreate: () => { createCount += 1; }, onUpdate: () => { updated = true; } });
    await user.click(screen.getByRole('button', { name: /^test connection$/i }));

    expect(await screen.findByLabelText(/^manufacturer/i)).toBeVisible();
    expect(updated).toBe(true);
    expect(createCount).toBe(1);
  });

  it('does not advance when the device is unreachable', async () => {
    stubCredentialTestFlow({ authOutcome: 'unreachable', detail: 'The device did not respond.' });
    const user = userEvent.setup();
    render(<CameraForm {...emptySelectors} onSubmit={vi.fn()} organizationUnits={[]} geographicAreas={[]} initialValues={validForm()} />);

    await user.click(screen.getByRole('button', { name: /^test connection$/i }));

    expect(await screen.findByText(/not reachable/i)).toBeVisible();
    expect(screen.queryByLabelText(/^manufacturer/i)).not.toBeInTheDocument();
  });

  it('passes the created camera id and patch request to onSubmit on the final save', async () => {
    stubCredentialTestFlow({ authOutcome: 'authenticated' });
    const user = userEvent.setup();
    const submit = vi.fn().mockResolvedValue(undefined);
    render(<CameraForm {...emptySelectors} onSubmit={submit} organizationUnits={[]} geographicAreas={[]} initialValues={validForm()} />);

    await user.click(screen.getByRole('button', { name: /^test connection$/i }));
    await screen.findByLabelText(/^manufacturer/i);
    await user.click(screen.getByRole('button', { name: /save details/i }));

    expect(submit).toHaveBeenCalledWith(
      cameraId,
      expect.not.objectContaining({ cameraCode: expect.anything() }),
    );
  });

  it('updates the coordinate fields when a map location is selected', async () => {
    stubCredentialTestFlow({ authOutcome: 'authenticated' });
    const user = userEvent.setup();
    render(<CameraForm {...emptySelectors} onSubmit={vi.fn()} organizationUnits={[]} geographicAreas={[]} initialValues={validForm()} />);
    await user.click(screen.getByRole('button', { name: /set location to 19.076012345/i }));
    expect(screen.getByLabelText(/^Latitude/i)).toHaveValue('19.0760123');
    expect(screen.getByLabelText(/^Longitude/i)).toHaveValue('72.8777');
  });

  it('uses the accessible picker input as the form azimuth value', async () => {
    stubCredentialTestFlow({ authOutcome: 'authenticated' });
    const user = userEvent.setup();
    const submit = vi.fn().mockResolvedValue(undefined);
    render(<CameraForm {...emptySelectors} onSubmit={submit} organizationUnits={[]} geographicAreas={[]} initialValues={validForm()} />);
    const azimuth = screen.getByLabelText(/^Azimuth/i);
    await user.type(azimuth, '45.125');
    expect(azimuth).toHaveValue(45.125);

    await user.click(screen.getByRole('button', { name: /^test connection$/i }));
    await screen.findByLabelText(/^manufacturer/i);
    await user.click(screen.getByRole('button', { name: /save details/i }));

    expect(submit).toHaveBeenCalledWith(cameraId, expect.objectContaining({ azimuth: 45.125 }));
  });

  it('keeps optional fields hidden until Additional details is expanded', async () => {
    stubCredentialTestFlow({ authOutcome: 'authenticated' });
    const user = userEvent.setup();
    render(<CameraForm {...emptySelectors} onSubmit={vi.fn()} organizationUnits={[]} geographicAreas={[]} initialValues={validForm()} />);
    await user.click(screen.getByRole('button', { name: /^test connection$/i }));
    await screen.findByLabelText(/^manufacturer/i);

    expect(screen.queryByLabelText(/mounting height/i)).not.toBeInTheDocument();
    const details = screen.getByRole('button', { name: /additional details/i });
    expect(details).toHaveAttribute('aria-expanded', 'false');
    await user.click(details);

    expect(details).toHaveAttribute('aria-expanded', 'true');
    expect(screen.getByLabelText(/mounting height/i)).toBeVisible();
  });

  it('defaults new cameras to saving events, with an option to turn it off', async () => {
    stubCredentialTestFlow({ authOutcome: 'authenticated' });
    const user = userEvent.setup();
    const submit = vi.fn().mockResolvedValue(undefined);
    render(<CameraForm {...emptySelectors} onSubmit={submit} organizationUnits={[]} geographicAreas={[]} initialValues={validForm()} />);
    await user.click(screen.getByRole('button', { name: /^test connection$/i }));
    await screen.findByLabelText(/^manufacturer/i);

    const recordEvents = screen.getByRole('checkbox', { name: /save camera events/i });
    expect(recordEvents).toBeChecked();
    await user.click(recordEvents);
    await user.click(screen.getByRole('button', { name: /save details/i }));

    expect(submit).toHaveBeenCalledWith(cameraId, expect.objectContaining({ recordEvents: false }));
  });

  it('requires network details for a manual registration before Test connection unlocks', () => {
    render(<CameraForm {...emptySelectors} onSubmit={vi.fn()} organizationUnits={[]} geographicAreas={[]} initialValues={validForm({ manufacturer: '', ipAddress: '', port: '', protocol: '' })} />);

    expect(screen.getByRole('button', { name: /^test connection$/i })).toBeDisabled();
  });

  it('shows API validation detail as safe form feedback', async () => {
    stubCredentialTestFlow({ authOutcome: 'authenticated' });
    const user = userEvent.setup();
    const submit = vi.fn().mockRejectedValue(new ApiProblem({ status: 400, title: 'Invalid camera', detail: 'cameraCode already exists.' }));
    render(<CameraForm {...emptySelectors} onSubmit={submit} organizationUnits={[]} geographicAreas={[]} initialValues={validForm()} />);
    await user.click(screen.getByRole('button', { name: /^test connection$/i }));
    await screen.findByLabelText(/^manufacturer/i);

    await user.click(screen.getByRole('button', { name: /save details/i }));

    expect(await screen.findByRole('alert')).toHaveTextContent('cameraCode already exists.');
  });

  it('does not echo an unexpected error into form feedback', async () => {
    stubCredentialTestFlow({ authOutcome: 'authenticated' });
    const user = userEvent.setup();
    const submit = vi.fn().mockRejectedValue(new Error('connection secret'));
    render(<CameraForm {...emptySelectors} onSubmit={submit} organizationUnits={[]} geographicAreas={[]} initialValues={validForm()} />);
    await user.click(screen.getByRole('button', { name: /^test connection$/i }));
    await screen.findByLabelText(/^manufacturer/i);

    await user.click(screen.getByRole('button', { name: /save details/i }));

    expect(await screen.findByRole('alert')).toHaveTextContent('Unable to save the camera. Please try again.');
    expect(screen.queryByText('connection secret')).not.toBeInTheDocument();
  });

  it('does not render a credential-reference input anywhere in the form', async () => {
    stubCredentialTestFlow({ authOutcome: 'authenticated' });
    const user = userEvent.setup();
    render(<CameraForm {...emptySelectors} onSubmit={vi.fn()} organizationUnits={[]} geographicAreas={[]} initialValues={validForm()} />);
    await user.click(screen.getByRole('button', { name: /^test connection$/i }));
    await screen.findByLabelText(/^manufacturer/i);
    await user.click(screen.getByRole('button', { name: /additional details/i }));

    expect(screen.queryByLabelText(/credential reference/i)).not.toBeInTheDocument();
  });

  it('blocks submission when a populated VMS ID is not a UUID', async () => {
    stubCredentialTestFlow({ authOutcome: 'authenticated' });
    const user = userEvent.setup();
    const submit = vi.fn();
    render(<CameraForm {...emptySelectors} onSubmit={submit} organizationUnits={[]} geographicAreas={[]} initialValues={validForm({ vmsId: 'not-a-uuid' })} />);
    await user.click(screen.getByRole('button', { name: /^test connection$/i }));
    await screen.findByLabelText(/^manufacturer/i);

    await user.click(screen.getByRole('button', { name: /additional details/i }));
    await user.click(screen.getByRole('button', { name: /save details/i }));

    expect(await screen.findByText(/VMS ID must be a valid UUID/i)).toBeVisible();
    expect(submit).not.toHaveBeenCalled();
  });

  it('submits a VMS ID using the .NET Guid wire format', async () => {
    stubCredentialTestFlow({ authOutcome: 'authenticated' });
    const user = userEvent.setup();
    const submit = vi.fn().mockResolvedValue(undefined);
    const vmsId = '00000000-0000-0000-0000-000000000001';
    render(<CameraForm {...emptySelectors} onSubmit={submit} organizationUnits={[]} geographicAreas={[]} vms={[{
      id: vmsId, code: 'VMS-001', organizationUnitId: 'c0a80101-0000-4000-8000-000000000010', geographicAreaId: null,
      displayName: 'North Gate NVR', vendor: 'GENERIC', runtimeClass: 'ONVIF', endpoint: 'https://nvr.example.test',
      credentialReference: 'vault://nvr', verifyTls: true, state: 'ACTIVE', expectedCameraCount: null,
      lastInventoryPollAt: null, lastInventoryCameraCount: null,
    }]} initialValues={validForm({ manufacturer: '' })} />);
    await user.click(screen.getByRole('button', { name: /^test connection$/i }));
    await screen.findByLabelText(/^manufacturer/i);

    await user.click(screen.getByRole('button', { name: /additional details/i }));
    await user.selectOptions(screen.getByLabelText(/vms/i), vmsId);

    await user.click(screen.getByRole('button', { name: /save details/i }));

    expect(submit).toHaveBeenCalledWith(cameraId, expect.objectContaining({ vmsId }));
  });

  it('connects an invalid required field to its rendered error message once Test connection is clickable', async () => {
    const user = userEvent.setup();
    // cameraCode/name are truthy (so Test-connection readiness is satisfied and the button is
    // clickable) but exceed the schema's max length, so `trigger()` still reports them invalid.
    render(<CameraForm {...emptySelectors} onSubmit={vi.fn()} organizationUnits={[]} geographicAreas={[]} initialValues={validForm({ cameraCode: 'x'.repeat(101), name: 'x'.repeat(256) })} />);

    await user.click(screen.getByRole('button', { name: /^test connection$/i }));

    for (const label of [/^Camera code/i, /^Name/i]) {
      const field = screen.getByLabelText(label);
      const errorId = field.getAttribute('aria-describedby');
      expect(field).toHaveAttribute('aria-invalid', 'true');
      expect(errorId).toBeTruthy();
      await waitFor(() => expect(document.getElementById(errorId!.split(' ')[0])).toBeVisible());
    }
  });

  it('shows an already-shared saved credential as selected when editing a camera that has one', async () => {
    const reference = 'saved-credential:11111111-1111-4111-8111-111111111111';
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => {
      const url = new URL(String(input));
      if (url.pathname === '/api/v1/credential-library') {
        return Response.json([{
          id: '11111111-1111-4111-8111-111111111111', name: 'Site NVR admin', description: null,
          credentialReference: reference, createdAt: '2026-01-01T00:00:00Z', updatedAt: '2026-01-01T00:00:00Z', usageCount: 1,
        }]);
      }
      return new Response(null, { status: 404 });
    }));

    render(<CameraForm {...emptySelectors} onSubmit={vi.fn()} organizationUnits={[]} geographicAreas={[]} initialValues={validForm()} initialCredentialReference={reference} />);

    const select = await screen.findByLabelText(/saved credential/i) as HTMLSelectElement;
    await waitFor(() => expect(select).toHaveValue(reference));
    expect(screen.queryByLabelText(/^username/i)).not.toBeInTheDocument();
  });

  it('falls back to manual entry when the camera\'s existing reference is not a shared library entry', async () => {
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => {
      const url = new URL(String(input));
      if (url.pathname === '/api/v1/credential-library') return Response.json([]);
      return new Response(null, { status: 404 });
    }));

    render(<CameraForm {...emptySelectors} onSubmit={vi.fn()} organizationUnits={[]} geographicAreas={[]} initialValues={validForm()} initialCredentialReference="camera:99999999-9999-4999-8999-999999999999" />);

    const select = await screen.findByLabelText(/saved credential/i) as HTMLSelectElement;
    await waitFor(() => expect(select).toHaveValue(''));
    expect(screen.getByLabelText(/^username/i)).toBeVisible();
  });
});
