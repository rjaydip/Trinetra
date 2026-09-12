import '@testing-library/jest-dom/vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { AuthProvider } from '../auth/AuthProvider';
import { readSession, saveSession } from '../auth/session';
import { sessionFixture } from '../test/fixtures';
import { AppShell } from './AppShell';

afterEach(() => {
  vi.unstubAllGlobals();
  sessionStorage.clear();
});

function renderShell(permissions: string[] = []) {
  saveSession(sessionFixture('shell-user', permissions));
  return render(
    <MemoryRouter initialEntries={['/dashboard']}>
      <AuthProvider>
        <Routes>
          <Route element={<AppShell />}>
            <Route path="/dashboard" element={<p>Dashboard content</p>} />
          </Route>
          <Route path="/login" element={<p>Login page</p>} />
        </Routes>
      </AuthProvider>
    </MemoryRouter>,
  );
}

describe('AppShell navigation', () => {
  it('shows Detections and Events only to callers with the matching read permission', () => {
    renderShell(['observation.read']);

    expect(screen.getByRole('link', { name: 'Detections' })).toBeVisible();
    expect(screen.queryByRole('link', { name: 'Events' })).not.toBeInTheDocument();
  });
});

describe('AppShell logout', () => {
  it('ends the server-side session and clears the local one', async () => {
    const requests: Array<{ path: string; method?: string }> = [];
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      requests.push({ path: new URL(String(input)).pathname, method: init?.method });
      return new Response(null, { status: 204 });
    }));
    const user = userEvent.setup();
    renderShell([]);

    await user.click(screen.getByRole('button', { name: /log out/i }));

    await waitFor(() => expect(requests.some(({ path, method }) => path === '/api/v1/auth/logout' && method === 'POST')).toBe(true));
    await waitFor(() => expect(readSession()).toBeNull());
  });

  it('still clears the local session when the server call fails', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => new Response(null, { status: 500 })));
    const user = userEvent.setup();
    renderShell([]);

    await user.click(screen.getByRole('button', { name: /log out/i }));

    await waitFor(() => expect(readSession()).toBeNull());
  });
});
