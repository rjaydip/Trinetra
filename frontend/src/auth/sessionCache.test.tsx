import '@testing-library/jest-dom/vitest';
import { act, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { StrictMode } from 'react';
import userEvent from '@testing-library/user-event';
import { afterEach, expect, it, vi } from 'vitest';
import { App } from '../App';
import { cameraFixture, sessionFixture } from '../test/fixtures';
import { AuthProvider } from './AuthProvider';
import { clearSession, saveSession } from './session';

afterEach(() => { vi.unstubAllGlobals(); sessionStorage.clear(); });

it.each(['/cameras', '/dashboard'])('never renders prior-user results after a session change on %s', async (route) => {
  const first = sessionFixture('first');
  const second = sessionFixture('second');
  saveSession(first);
  const secondRequests: Array<() => void> = [];
  vi.stubGlobal('fetch', (input: RequestInfo | URL, init?: RequestInit) => {
    const url = new URL(String(input));
    const isSecond = new Headers(init?.headers).get('authorization') === `Bearer ${second.token}`;
    const name = isSecond ? 'Second user camera' : 'Prior user camera';
    const body = url.pathname === '/api/v1/gis/cameras'
      ? { type: 'FeatureCollection', features: [{ type: 'Feature', geometry: { type: 'Point', coordinates: [77.5946, 12.9716] }, properties: { cameraId: cameraFixture().id, name } }] }
      : url.pathname === '/api/v1/overview' ? { targets: 0, activeTargets: 0, quarantinedTargets: 0, cameras: 1, unreachableCameras: 0 }
        : { items: [cameraFixture({ name })], nextCursor: null };
    if (isSecond) return new Promise<Response>((resolve) => secondRequests.push(() => resolve(Response.json(body))));
    return Promise.resolve(Response.json(body));
  });
  render(<StrictMode><MemoryRouter initialEntries={[route]}><AuthProvider><App /></AuthProvider></MemoryRouter></StrictMode>);
  expect((await screen.findAllByText(/Prior user camera/))[0]).toBeVisible();

  act(() => saveSession(second));
  expect(screen.queryAllByText(/Prior user camera/)).toHaveLength(0);
  await waitFor(() => expect(secondRequests.length).toBeGreaterThan(0));
  await act(async () => { secondRequests.splice(0).forEach((resolve) => resolve()); });
  if (route === '/dashboard') {
    await waitFor(() => expect(secondRequests.length).toBeGreaterThan(0));
    await act(async () => { secondRequests.splice(0).forEach((resolve) => resolve()); });
  }
  expect((await screen.findAllByText(/Second user camera/))[0]).toBeVisible();
  act(() => clearSession());
  expect(screen.getByRole('heading', { name: /sign in/i })).toBeVisible();
  expect(screen.queryAllByText(/Second user camera/)).toHaveLength(0);
});

it('keeps the signed-out user cache unavailable while the next real login loads its cameras', async () => {
  const first = sessionFixture('first-login');
  const second = sessionFixture('second-login');
  saveSession(first);
  let releaseSecond: (() => void) | undefined;
  vi.stubGlobal('fetch', async (input: RequestInfo | URL, init?: RequestInit) => {
    const path = new URL(String(input)).pathname;
    if (path === '/api/v1/auth/login') return Response.json(second);
    if (path === '/api/v1/overview') return Response.json({ targets: 0, activeTargets: 0, quarantinedTargets: 0, cameras: 1, unreachableCameras: 0 });
    if (path === '/api/v1/gis/cameras') return Response.json({ type: 'FeatureCollection', features: [] });
    const response = () => Response.json({ items: [cameraFixture({ name: 'Prior private camera' })], nextCursor: null });
    if (new Headers(init?.headers).get('authorization') === `Bearer ${second.token}`) {
      return new Promise<Response>((resolve) => { releaseSecond = () => resolve(Response.json({ items: [], nextCursor: null })); });
    }
    return response();
  });
  render(<MemoryRouter initialEntries={['/cameras']}><AuthProvider><App /></AuthProvider></MemoryRouter>);
  await screen.findAllByText('Prior private camera');
  act(() => clearSession());
  const user = userEvent.setup();
  await user.type(screen.getByLabelText(/username/i), 'second');
  await user.type(screen.getByLabelText(/^password/i), 'valid password');
  await user.click(screen.getByRole('button', { name: /sign in/i }));
  await user.click(screen.getByRole('link', { name: /camera registry/i }));
  expect(screen.queryAllByText('Prior private camera')).toHaveLength(0);
  await waitFor(() => expect(releaseSecond).toBeDefined());
  await act(async () => releaseSecond!());
  expect(await screen.findByRole('heading', { name: /no cameras found/i })).toBeVisible();
});
