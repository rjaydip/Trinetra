import '@testing-library/jest-dom/vitest';
import { render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { afterEach, expect, it, vi } from 'vitest';
import { App } from '../App';
import { sessionFixture } from '../test/fixtures';
import { AuthProvider } from './AuthProvider';
import { saveSession } from './session';

afterEach(() => { vi.unstubAllGlobals(); sessionStorage.clear(); });

function renderRoute(route: string, permissions: string | string[] = []) {
  saveSession(sessionFixture('operator', permissions));
  vi.stubGlobal('fetch', async (input: RequestInfo | URL) => String(input).includes('/api/v1/cameras?') ? Response.json({ items: [], nextCursor: null }) : Response.json([]));
  return render(<MemoryRouter initialEntries={[route]}><AuthProvider><App /></AuthProvider></MemoryRouter>);
}

it.each([
  { claims: [], create: false, bulk: false },
  { claims: 'camera.create', create: true, bulk: false },
  { claims: ['camera.import'], create: false, bulk: true },
  { claims: ['camera.create', 'camera.import'], create: true, bulk: true },
])('presents only onboarding links permitted by the issued claim: $claims', async ({ claims, create, bulk }) => {
  renderRoute('/cameras', claims);
  await screen.findByLabelText(/search cameras/i);
  expect(Boolean(screen.queryByRole('link', { name: /^register camera/i }))).toBe(create);
  expect(Boolean(screen.queryByRole('link', { name: /^bulk import/i }))).toBe(bulk);
});

it.each(['/cameras/new', '/cameras/import'])('keeps a denied direct route unavailable: %s', async (route) => {
  renderRoute(route, ['camera.read']);
  expect(await screen.findByRole('heading', { name: /action unavailable/i })).toBeVisible();
  expect(screen.queryByLabelText(/camera code|import JSON file/i)).not.toBeInTheDocument();
});

it('does not infer permissions from a malformed token', async () => {
  const session = sessionFixture();
  session.token = 'malformed.camera.create.camera.import';
  saveSession(session);
  vi.stubGlobal('fetch', async () => Response.json({ items: [], nextCursor: null }));
  render(<MemoryRouter initialEntries={['/cameras']}><AuthProvider><App /></AuthProvider></MemoryRouter>);
  await screen.findByLabelText(/search cameras/i);
  expect(screen.queryAllByRole('link', { name: /^register camera|^bulk import/i })).toHaveLength(0);
});
