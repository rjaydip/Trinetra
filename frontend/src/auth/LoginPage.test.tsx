import '@testing-library/jest-dom/vitest';
import { act, render, screen } from '@testing-library/react';
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

function futureExpiry(): string {
  return new Date(Date.now() + 60_000).toISOString();
}

afterEach(() => {
  vi.useRealTimers();
  vi.unstubAllGlobals();
  sessionStorage.clear();
});

describe('LoginPage', () => {
  it('stores the returned token after a successful login', async () => {
    vi.stubGlobal('fetch', async (input: RequestInfo | URL) => {
      const path = new URL(String(input)).pathname;
      if (path === '/api/v1/auth/login') return Response.json({ token: 'signed-in-token', expiresAt: futureExpiry(), mustChangePassword: false });
      if (path === '/api/v1/cameras') return Response.json({ items: [], nextCursor: null });
      if (path === '/api/v1/overview') return Response.json({ targets: 0, activeTargets: 0, quarantinedTargets: 0, cameras: 0, unreachableCameras: 0 });
      return new Response(null, { status: 404 });
    });

    const user = userEvent.setup();
    renderApp('/login');

    await user.type(screen.getByLabelText(/username/i), 'admin');
    await user.type(screen.getByLabelText(/^password/i), 'valid password');
    await user.click(screen.getByRole('button', { name: /sign in/i }));

    expect(await screen.findByRole('heading', { name: /^camera map$/i })).toBeVisible();
    expect(sessionStorage.getItem('trinetra.auth.session')).toContain('signed-in-token');
  });

  it('routes a password-rotation session to the password form', async () => {
    vi.stubGlobal('fetch', async () => new Response(JSON.stringify({
      token: 'rotation-token',
      expiresAt: futureExpiry(),
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
    expect(screen.getByLabelText(/username/i)).toHaveAttribute('aria-describedby', 'sign-in-error');
    expect(screen.getByLabelText(/username/i)).toHaveAttribute('aria-invalid', 'true');
    expect(screen.getByLabelText(/^password/i)).toHaveAttribute('aria-describedby', 'sign-in-error');
    expect(screen.getByLabelText(/^password/i)).toHaveAttribute('aria-invalid', 'true');
  });

  it('toggles password visibility', async () => {
    const user = userEvent.setup();
    renderApp('/login');

    const password = screen.getByLabelText(/^password/i);
    expect(password).toHaveAttribute('type', 'password');

    await user.click(screen.getByRole('button', { name: /show password/i }));

    expect(password).toHaveAttribute('type', 'text');
  });

  it('removes protected access when the active session expires', async () => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date('2026-08-31T00:00:00Z'));
    sessionStorage.setItem('trinetra.auth.session', JSON.stringify({
      token: 'soon-expired-token',
      expiresAt: new Date(Date.now() + 1_000).toISOString(),
      mustChangePassword: false,
    }));
    vi.stubGlobal('fetch', async () => new Response(null, { status: 503 }));

    renderApp('/dashboard');
    expect(screen.getByRole('heading', { name: /^camera map$/i })).toBeVisible();

    await act(async () => {
      await vi.advanceTimersByTimeAsync(1_000);
    });

    expect(screen.getByRole('heading', { name: /sign in/i })).toBeVisible();
    expect(sessionStorage.getItem('trinetra.auth.session')).toBeNull();
  });
});
