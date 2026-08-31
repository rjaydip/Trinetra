import '@testing-library/jest-dom/vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';

import { ApiProblem } from '../../api/client';
import { CameraForm, toCameraWriteRequest, type CameraFormValues } from './CameraForm';

function validForm(overrides: Partial<CameraFormValues> = {}): CameraFormValues {
  return {
    cameraCode: 'CAM-001',
    name: 'North Gate',
    organizationUnitId: 'c0a80101-0000-4000-8000-000000000010',
    siteId: 'c0a80101-0000-4000-8000-000000000020',
    cameraType: 'FIXED',
    latitude: '12.9716',
    longitude: '77.5946',
    manufacturer: '', model: '', serialNumber: '', altitude: '', mountingHeight: '',
    azimuth: '', tilt: '', horizontalFov: '', verticalFov: '', effectiveRange: '',
    ipAddress: '', port: '', protocol: '', vmsId: '', streamReference: '',
    installationDate: '', operationalStatus: '', connectivityStatus: '', maintenanceStatus: '',
    ...overrides,
  };
}

describe('CameraForm', () => {
  it('does not include an empty optional port in the request', () => {
    expect(toCameraWriteRequest(validForm({ port: '' })).port).toBeUndefined();
  });

  it('blocks submission until required camera identity and coordinates are supplied', async () => {
    const user = userEvent.setup();
    const submit = vi.fn();
    render(<CameraForm onSubmit={submit} organizationUnits={[]} sites={[]} />);

    await user.click(screen.getByRole('button', { name: /register camera/i }));

    expect(await screen.findByText(/camera code is required/i)).toBeVisible();
    expect(screen.getByText(/latitude is required/i)).toBeVisible();
    expect(submit).not.toHaveBeenCalled();
  });

  it('shows API validation detail as safe form feedback', async () => {
    const user = userEvent.setup();
    const submit = vi.fn().mockRejectedValue(new ApiProblem({ status: 400, title: 'Invalid camera', detail: 'cameraCode already exists.' }));
    render(<CameraForm onSubmit={submit} organizationUnits={[]} sites={[]} initialValues={validForm()} />);

    await user.click(screen.getByRole('button', { name: /register camera/i }));

    expect(await screen.findByRole('alert')).toHaveTextContent('cameraCode already exists.');
  });

  it('does not echo an unexpected error into form feedback', async () => {
    const user = userEvent.setup();
    const submit = vi.fn().mockRejectedValue(new Error('connection secret'));
    render(<CameraForm onSubmit={submit} organizationUnits={[]} sites={[]} initialValues={validForm()} />);

    await user.click(screen.getByRole('button', { name: /register camera/i }));

    expect(await screen.findByRole('alert')).toHaveTextContent('Unable to register the camera. Please try again.');
    expect(screen.queryByText('connection secret')).not.toBeInTheDocument();
  });

  it('does not render a credential-reference input', () => {
    render(<CameraForm onSubmit={vi.fn()} organizationUnits={[]} sites={[]} />);

    expect(screen.queryByLabelText(/credential reference/i)).not.toBeInTheDocument();
  });

  it('blocks submission when a populated VMS ID is not a UUID', async () => {
    const user = userEvent.setup();
    const submit = vi.fn();
    render(<CameraForm onSubmit={submit} organizationUnits={[]} sites={[]} initialValues={validForm({ vmsId: 'not-a-uuid' })} />);

    await user.click(screen.getByRole('button', { name: /register camera/i }));

    expect(await screen.findByText(/VMS ID must be a valid UUID/i)).toBeVisible();
    expect(submit).not.toHaveBeenCalled();
  });

  it('submits a VMS ID using the .NET Guid wire format', async () => {
    const user = userEvent.setup();
    const submit = vi.fn().mockResolvedValue(undefined);
    const vmsId = '00000000-0000-0000-0000-000000000001';
    render(<CameraForm onSubmit={submit} organizationUnits={[]} sites={[]} initialValues={validForm({ vmsId })} />);

    await user.click(screen.getByRole('button', { name: /register camera/i }));

    expect(submit).toHaveBeenCalledWith(expect.objectContaining({ vmsId }));
  });

  it('connects every invalid required field to its rendered error message', async () => {
    const user = userEvent.setup();
    render(<CameraForm onSubmit={vi.fn()} organizationUnits={[]} sites={[]} />);

    await user.click(screen.getByRole('button', { name: /register camera/i }));

    for (const label of ['Camera code', 'Name', 'Organization unit', 'Site', 'Camera type', 'Latitude', 'Longitude']) {
      const field = screen.getByLabelText(label);
      const errorId = field.getAttribute('aria-describedby');
      expect(field).toHaveAttribute('aria-invalid', 'true');
      expect(errorId).toBeTruthy();
      expect(document.getElementById(errorId!)).toBeVisible();
    }
  });
});
