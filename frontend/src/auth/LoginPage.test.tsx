import '@testing-library/jest-dom/vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { App } from '../App';
import { AuthProvider } from './AuthProvider';

function renderApp(path: string) {
  return render(
    <MemoryRouter initialEntries={[path]}>
      <AuthProvider>
        <App />
      </AuthProvider>
    </MemoryRouter>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
  sessionStorage.clear();
});

describe('LoginPage', () => {
  it('stores the returned token after a successful login', async () => {
    vi.stubGlobal('fetch', async () => new Response(JSON.stringify({
      token: 'signed-in-token',
      expiresAt: '2030-01-01T00:00:00Z',
      mustChangePassword: false,
    }), {
      status: 200,
      headers: { 'content-type': 'application/json' },
    }));

    const user = userEvent.setup();
    renderApp('/login');

    await user.type(screen.getByLabelText(/username/i), 'admin');
    await user.type(screen.getByLabelText(/^password/i), 'valid password');
    await user.click(screen.getByRole('button', { name: /sign in/i }));

    expect(await screen.findByRole('heading', { name: /camera map/i })).toBeVisible();
    expect(sessionStorage.getItem('trinetra.auth.session')).toContain('signed-in-token');
  });

  it('routes a password-rotation session to the password form', async () => {
    vi.stubGlobal('fetch', async () => new Response(JSON.stringify({
      token: 'rotation-token',
      expiresAt: '2030-01-01T00:00:00Z',
      mustChangePassword: true,
    }), {
      status: 200,
      headers: { 'content-type': 'application/json' },
    }));

    const user = userEvent.setup();
    renderApp('/login');

    await user.type(screen.getByLabelText(/username/i), 'admin');
    await user.type(screen.getByLabelText(/^password/i), 'valid password');
    await user.click(screen.getByRole('button', { name: /sign in/i }));

    expect(await screen.findByRole('heading', { name: /change password/i })).toBeVisible();
  });

  it('shows a safe inline error when sign-in is rejected', async () => {
    vi.stubGlobal('fetch', async () => new Response(JSON.stringify({
      title: 'Authentication failed',
      detail: 'The username or password is incorrect, or the account is unavailable.',
    }), {
      status: 401,
      headers: { 'content-type': 'application/problem+json' },
    }));

    const user = userEvent.setup();
    renderApp('/login');

    await user.type(screen.getByLabelText(/username/i), 'admin');
    await user.type(screen.getByLabelText(/^password/i), 'wrong password');
    await user.click(screen.getByRole('button', { name: /sign in/i }));

    expect(await screen.findByRole('alert')).toHaveTextContent('The username or password is incorrect, or the account is unavailable.');
  });
});
