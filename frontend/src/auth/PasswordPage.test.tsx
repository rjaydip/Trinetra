import '@testing-library/jest-dom/vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { ApiProblem } from '../api/client';
import { PasswordPage } from './PasswordPage';

const { changePassword } = vi.hoisted(() => ({ changePassword: vi.fn() }));

vi.mock('./AuthProvider', () => ({
  useAuth: () => ({ changePassword }),
}));

function renderPasswordPage() {
  return render(<MemoryRouter><PasswordPage /></MemoryRouter>);
}

async function completeValidPasswordForm(user: ReturnType<typeof userEvent.setup>) {
  await user.type(screen.getByLabelText(/current password/i), 'current-secret');
  await user.type(screen.getByLabelText(/^new password/i), 'new-secret');
  await user.type(screen.getByLabelText(/confirm new password/i), 'new-secret');
}

afterEach(() => {
  changePassword.mockReset();
});

describe('PasswordPage', () => {
  it('does not call changePassword when the new password equals the current password', async () => {
    const user = userEvent.setup();
    renderPasswordPage();

    await user.type(screen.getByLabelText(/current password/i), 'same-secret');
    await user.type(screen.getByLabelText(/^new password/i), 'same-secret');
    await user.type(screen.getByLabelText(/confirm new password/i), 'same-secret');
    await user.click(screen.getByRole('button', { name: /change password/i }));

    expect(changePassword).not.toHaveBeenCalled();
    expect(screen.getByRole('alert')).toHaveTextContent('must be different');
  });

  it('renders the API problem detail when the server rejects a password', async () => {
    const user = userEvent.setup();
    changePassword.mockRejectedValue(new ApiProblem({ status: 400, title: 'Password unchanged', detail: 'Choose a different password.' }));
    renderPasswordPage();

    await completeValidPasswordForm(user);
    await user.click(screen.getByRole('button', { name: /change password/i }));

    expect(await screen.findByRole('alert')).toHaveTextContent('Choose a different password.');
  });

  it('does not call changePassword when the confirmation differs from the new password', async () => {
    const user = userEvent.setup();
    renderPasswordPage();

    await user.type(screen.getByLabelText(/current password/i), 'current-secret');
    await user.type(screen.getByLabelText(/^new password/i), 'new-secret');
    await user.type(screen.getByLabelText(/confirm new password/i), 'different-secret');
    await user.click(screen.getByRole('button', { name: /change password/i }));

    expect(changePassword).not.toHaveBeenCalled();
    expect(screen.getByRole('alert')).toHaveTextContent('must match');
  });

  it('connects every password input to the failure feedback', async () => {
    const user = userEvent.setup();
    renderPasswordPage();

    await user.type(screen.getByLabelText(/current password/i), 'same-secret');
    await user.type(screen.getByLabelText(/^new password/i), 'same-secret');
    await user.type(screen.getByLabelText(/confirm new password/i), 'same-secret');
    await user.click(screen.getByRole('button', { name: /change password/i }));

    for (const label of [/current password/i, /confirm new password/i]) {
      expect(screen.getByLabelText(label)).toHaveAttribute('aria-describedby', 'password-change-error');
    }
    expect(screen.getByLabelText(/^new password/i)).toHaveAttribute('aria-describedby', 'password-change-error new-password-hint');

    for (const label of [/current password/i, /^new password/i, /confirm new password/i]) {
      expect(screen.getByLabelText(label)).toHaveAttribute('aria-invalid', 'true');
    }
  });

  it('shows the password policy hint before any error occurs', () => {
    renderPasswordPage();

    expect(screen.getByText(/at least 12 characters/i)).toBeVisible();
    expect(screen.getByLabelText(/^new password/i)).toHaveAttribute('aria-describedby', 'new-password-hint');
  });

  it('toggles password visibility', async () => {
    const user = userEvent.setup();
    renderPasswordPage();

    const currentPassword = screen.getByLabelText(/current password/i);
    expect(currentPassword).toHaveAttribute('type', 'password');

    await user.click(screen.getAllByRole('button', { name: /show password/i })[0]);

    expect(currentPassword).toHaveAttribute('type', 'text');
  });
});
