import '@testing-library/jest-dom/vitest';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { App } from '../../App';
import { AuthProvider } from '../../auth/AuthProvider';
import { saveSession } from '../../auth/session';
import { AppShell } from '../../components/AppShell';
import { sessionFixture } from '../../test/fixtures';

afterEach(() => {
  vi.unstubAllGlobals();
  sessionStorage.clear();
});

function renderShell(permissions: string[]) {
  saveSession(sessionFixture('admin-navigation', permissions));
  render(
    <MemoryRouter initialEntries={['/dashboard']}>
      <AuthProvider>
        <Routes>
          <Route element={<AppShell />}>
            <Route path="/dashboard" element={<p>Dashboard content</p>} />
          </Route>
        </Routes>
      </AuthProvider>
    </MemoryRouter>,
  );
}

function renderApp(path: string, permissions: string[]) {
  saveSession(sessionFixture('admin-route', permissions));
  vi.stubGlobal('fetch', vi.fn(async () => Response.json([])));
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <MemoryRouter initialEntries={[path]}>
      <AuthProvider>
        <QueryClientProvider client={queryClient}>
          <App />
        </QueryClientProvider>
      </AuthProvider>
    </MemoryRouter>,
  );
}

describe('admin navigation', () => {
  it('does not show Admin without a supported admin read permission', () => {
    renderShell(['camera.read']);

    expect(screen.queryByRole('link', { name: /^admin$/i })).not.toBeInTheDocument();
  });

  it.each(['organization.read', 'geography.read', 'group.read', 'user.read', 'apikey.read', 'worker.read', 'alert.read'])('shows Admin to a user with %s', (permission) => {
    renderShell([permission]);

    expect(screen.getByRole('link', { name: /^admin$/i })).toHaveAttribute('href', '/admin');
  });
});

describe('admin route guards', () => {
  it('guards hierarchy with its exact read permissions', async () => {
    renderApp('/admin/hierarchy', ['group.read']);

    expect(await screen.findByRole('heading', { name: /action unavailable/i })).toBeVisible();
    expect(screen.queryByRole('heading', { name: /organization & geography/i })).not.toBeInTheDocument();
    expect(fetch).not.toHaveBeenCalled();
  });

  it('guards roles with group.read', async () => {
    renderApp('/admin/roles', ['organization.read']);

    expect(await screen.findByRole('heading', { name: /action unavailable/i })).toBeVisible();
    expect(screen.queryByRole('heading', { name: /^roles & permissions$/i })).not.toBeInTheDocument();
    expect(fetch).not.toHaveBeenCalled();
  });

  it('guards access groups with group.read', async () => {
    renderApp('/admin/access-groups', ['geography.read']);

    expect(await screen.findByRole('heading', { name: /action unavailable/i })).toBeVisible();
    expect(screen.queryByRole('heading', { name: /^access groups$/i })).not.toBeInTheDocument();
    expect(fetch).not.toHaveBeenCalled();
  });

  it('guards users with user.read', async () => {
    renderApp('/admin/users', ['geography.read']);

    expect(await screen.findByRole('heading', { name: /action unavailable/i })).toBeVisible();
    expect(screen.queryByRole('heading', { name: /^users$/i })).not.toBeInTheDocument();
    expect(fetch).not.toHaveBeenCalled();
  });

  it('guards API keys with apikey.read', async () => {
    renderApp('/admin/api-keys', ['geography.read']);

    expect(await screen.findByRole('heading', { name: /action unavailable/i })).toBeVisible();
    expect(screen.queryByRole('heading', { name: /^api keys$/i })).not.toBeInTheDocument();
    expect(fetch).not.toHaveBeenCalled();
  });

  it('guards worker health with worker.read', async () => {
    renderApp('/admin/worker-health', ['geography.read']);

    expect(await screen.findByRole('heading', { name: /action unavailable/i })).toBeVisible();
    expect(screen.queryByRole('heading', { name: /ai worker health/i })).not.toBeInTheDocument();
    expect(fetch).not.toHaveBeenCalled();
  });

  it('guards watchlist with alert.read', async () => {
    renderApp('/admin/watchlist', ['geography.read']);

    expect(await screen.findByRole('heading', { name: /action unavailable/i })).toBeVisible();
    expect(screen.queryByRole('heading', { name: /^watchlist$/i })).not.toBeInTheDocument();
    expect(fetch).not.toHaveBeenCalled();
  });
});
