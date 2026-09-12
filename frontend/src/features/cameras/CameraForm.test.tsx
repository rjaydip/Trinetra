import '@testing-library/jest-dom/vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';

import { ApiProblem } from '../../api/client';
import { CameraForm, toCameraWriteRequest, type CameraFormValues } from './CameraForm';

const emptySelectors = { organizations: [], vms: [], onOrganizationChange: vi.fn() };

function validForm(overrides: Partial<CameraFormValues> = {}): CameraFormValues {
  return {
    cameraCode: 'CAM-001',
    name: 'North Gate',
    organizationId: 'c0a80101-0000-4000-8000-000000000001',
    organizationUnitId: 'c0a80101-0000-4000-8000-000000000010',
    siteId: 'c0a80101-0000-4000-8000-000000000020',
    cameraType: 'FIXED',
    latitude: '12.9716',
    longitude: '77.5946',
    manufacturer: 'Axis', model: '', serialNumber: '', altitude: '', mountingHeight: '',
    azimuth: '', tilt: '', horizontalFov: '', verticalFov: '', effectiveRange: '',
    ipAddress: '10.0.0.8', port: '554', protocol: 'RTSP', vmsId: '', streamReference: '',
    installationDate: '', operationalStatus: '', connectivityStatus: '', maintenanceStatus: '',
    ...overrides,
  };
}

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

  it('updates the coordinate fields when a map location is selected', async () => {
    const user = userEvent.setup();
    render(<CameraForm {...emptySelectors} onSubmit={vi.fn()} organizationUnits={[]} sites={[]} initialValues={validForm()} />);

    await user.click(screen.getByRole('button', { name: /additional details/i }));
    await user.click(screen.getByRole('button', { name: /set location to 19.076012345/i }));

    expect(screen.getByLabelText(/^Latitude/i)).toHaveValue('19.0760123');
    expect(screen.getByLabelText(/^Longitude/i)).toHaveValue('72.8777');
  });

  it('uses the accessible picker input as the form azimuth value', async () => {
    const user = userEvent.setup();
    const submit = vi.fn().mockResolvedValue(undefined);
    render(<CameraForm {...emptySelectors} onSubmit={submit} organizationUnits={[]} sites={[]} initialValues={validForm()} />);

    await user.click(screen.getByRole('button', { name: /additional details/i }));
    const azimuth = screen.getByLabelText(/^Azimuth/i);
    await user.type(azimuth, '45.125');
    expect(azimuth).toHaveValue(45.125);
    await user.click(screen.getByRole('button', { name: /register camera/i }));

    expect(submit).toHaveBeenCalledWith(expect.objectContaining({ azimuth: 45.125 }));
  });

  it('keeps optional fields hidden until Additional details is expanded', async () => {
    const user = userEvent.setup();
    render(<CameraForm {...emptySelectors} onSubmit={vi.fn()} organizationUnits={[]} sites={[]} />);

    expect(screen.queryByLabelText(/mounting height/i)).not.toBeInTheDocument();
    const details = screen.getByRole('button', { name: /additional details/i });
    expect(details).toHaveAttribute('aria-expanded', 'false');
    await user.click(details);

    expect(details).toHaveAttribute('aria-expanded', 'true');
    expect(screen.getByLabelText(/mounting height/i)).toBeVisible();
  });

  it('blocks submission until required camera identity and coordinates are supplied', async () => {
    const user = userEvent.setup();
    const submit = vi.fn();
    render(<CameraForm {...emptySelectors} onSubmit={submit} organizationUnits={[]} sites={[]} />);

    await user.click(screen.getByRole('button', { name: /register camera/i }));

    expect(await screen.findByText(/camera code is required/i)).toBeVisible();
    expect(screen.getByText(/latitude is required/i)).toBeVisible();
    expect(submit).not.toHaveBeenCalled();
  });

  it('requires network details for a manual registration', async () => {
    const user = userEvent.setup();
    const submit = vi.fn();
    render(<CameraForm {...emptySelectors} onSubmit={submit} organizationUnits={[]} sites={[]} initialValues={validForm({ manufacturer: '', ipAddress: '', port: '', protocol: '' })} />);

    await user.click(screen.getByRole('button', { name: /register camera/i }));

    expect(await screen.findByText('Manufacturer is required for manual registration.')).toBeVisible();
    expect(screen.getByText('IP address is required for manual registration.')).toBeVisible();
    expect(screen.getByText('Port is required for manual registration.')).toBeVisible();
    expect(screen.getByText('Protocol is required for manual registration.')).toBeVisible();
    expect(submit).not.toHaveBeenCalled();
  });

  it('shows API validation detail as safe form feedback', async () => {
    const user = userEvent.setup();
    const submit = vi.fn().mockRejectedValue(new ApiProblem({ status: 400, title: 'Invalid camera', detail: 'cameraCode already exists.' }));
    render(<CameraForm {...emptySelectors} onSubmit={submit} organizationUnits={[]} sites={[]} initialValues={validForm()} />);

    await user.click(screen.getByRole('button', { name: /register camera/i }));

    expect(await screen.findByRole('alert')).toHaveTextContent('cameraCode already exists.');
  });

  it('does not echo an unexpected error into form feedback', async () => {
    const user = userEvent.setup();
    const submit = vi.fn().mockRejectedValue(new Error('connection secret'));
    render(<CameraForm {...emptySelectors} onSubmit={submit} organizationUnits={[]} sites={[]} initialValues={validForm()} />);

    await user.click(screen.getByRole('button', { name: /register camera/i }));

    expect(await screen.findByRole('alert')).toHaveTextContent('Unable to register the camera. Please try again.');
    expect(screen.queryByText('connection secret')).not.toBeInTheDocument();
  });

  it('does not render a credential-reference input', () => {
    render(<CameraForm {...emptySelectors} onSubmit={vi.fn()} organizationUnits={[]} sites={[]} />);

    expect(screen.queryByLabelText(/credential reference/i)).not.toBeInTheDocument();
  });

  it('blocks submission when a populated VMS ID is not a UUID', async () => {
    const user = userEvent.setup();
    const submit = vi.fn();
    render(<CameraForm {...emptySelectors} onSubmit={submit} organizationUnits={[]} sites={[]} initialValues={validForm({ vmsId: 'not-a-uuid' })} />);

    await user.click(screen.getByRole('button', { name: /additional details/i }));
    await user.click(screen.getByRole('button', { name: /register camera/i }));

    expect(await screen.findByText(/VMS ID must be a valid UUID/i)).toBeVisible();
    expect(submit).not.toHaveBeenCalled();
  });

  it('submits a VMS ID using the .NET Guid wire format', async () => {
    const user = userEvent.setup();
    const submit = vi.fn().mockResolvedValue(undefined);
    const vmsId = '00000000-0000-0000-0000-000000000001';
    render(<CameraForm {...emptySelectors} onSubmit={submit} organizationUnits={[]} sites={[]} vms={[{
      id: vmsId, code: 'VMS-001', organizationUnitId: 'c0a80101-0000-4000-8000-000000000010', siteId: null,
      displayName: 'North Gate NVR', vendor: 'GENERIC', runtimeClass: 'ONVIF', endpoint: 'https://nvr.example.test',
      credentialReference: 'vault://nvr', verifyTls: true, state: 'ACTIVE', expectedCameraCount: null,
    }]} initialValues={validForm({ manufacturer: '', ipAddress: '', port: '', protocol: '' })} />);

    await user.click(screen.getByRole('button', { name: /additional details/i }));
    await user.selectOptions(screen.getByLabelText(/vms/i), vmsId);

    await user.click(screen.getByRole('button', { name: /register camera/i }));

    expect(submit).toHaveBeenCalledWith(expect.objectContaining({ vmsId }));
  });

  it('connects every invalid required field to its rendered error message', async () => {
    const user = userEvent.setup();
    render(<CameraForm {...emptySelectors} onSubmit={vi.fn()} organizationUnits={[]} sites={[]} />);

    await user.click(screen.getByRole('button', { name: /register camera/i }));

    for (const label of [/^Camera code/i, /^Name/i, /^Organization unit/i, /^Site/i, /^Camera type/i, /^Latitude/i, /^Longitude/i]) {
      const field = screen.getByLabelText(label);
      const errorId = field.getAttribute('aria-describedby');
      expect(field).toHaveAttribute('aria-invalid', 'true');
      expect(errorId).toBeTruthy();
      expect(document.getElementById(errorId!)).toBeVisible();
    }
  });
});
