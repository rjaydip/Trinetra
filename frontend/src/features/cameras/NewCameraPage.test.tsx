import '@testing-library/jest-dom/vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import { afterEach, expect, it, vi } from 'vitest';
import { App } from '../../App';
import { AuthProvider } from '../../auth/AuthProvider';
import { saveSession } from '../../auth/session';
import { sessionFixture } from '../../test/fixtures';

afterEach(() => { vi.unstubAllGlobals(); sessionStorage.clear(); });

it('shows a retryable organization failure before the disabled units query loading state', async () => {
  saveSession(sessionFixture('registrar', ['camera.create']));
  let organizationsUnavailable = true;
  vi.stubGlobal('fetch', async (input: RequestInfo | URL) => {
    if (String(input).endsWith('/organizations') && organizationsUnavailable) {
      return Response.json({ title: 'Unavailable', detail: 'Reference data is temporarily unavailable.' }, { status: 503 });
    }
    return Response.json([]);
  });
  render(<MemoryRouter initialEntries={['/cameras/new']}><AuthProvider><App /></AuthProvider></MemoryRouter>);
  expect(await screen.findByRole('heading', { name: /couldn't load onboarding options/i })).toBeVisible();
  expect(screen.getByText('Reference data is temporarily unavailable.')).toBeVisible();
  expect(screen.queryByRole('heading', { name: /loading onboarding options/i })).not.toBeInTheDocument();
  organizationsUnavailable = false;
  await userEvent.click(screen.getByRole('button', { name: /try again/i }));
  expect(await screen.findByLabelText(/camera code/i)).toBeVisible();
});

it('loads map context only with a bounded bbox after coordinates are valid', async () => {
  saveSession(sessionFixture('registrar', ['camera.create']));
  const fetchMock = vi.fn(async (input: RequestInfo | URL) => {
    if (String(input).includes('/gis/cameras')) return Response.json({ type: 'FeatureCollection', features: [] });
    return Response.json([]);
  });
  vi.stubGlobal('fetch', fetchMock);
  render(<MemoryRouter initialEntries={['/cameras/new']}><AuthProvider><App /></AuthProvider></MemoryRouter>);

  const latitude = await screen.findByLabelText(/^Latitude/i);
  expect(fetchMock.mock.calls.some(([input]) => String(input).includes('/gis/cameras'))).toBe(false);

  await userEvent.type(latitude, '19.076');
  expect(fetchMock.mock.calls.some(([input]) => String(input).includes('/gis/cameras'))).toBe(false);
  await userEvent.type(screen.getByLabelText(/^Longitude/i), '72.8777');

  await waitFor(() => expect(fetchMock.mock.calls.some(([input]) => String(input).includes('/gis/cameras'))).toBe(true));
  const gisUrls = fetchMock.mock.calls.map(([input]) => String(input)).filter((url) => url.includes('/gis/cameras'));
  for (const url of gisUrls) {
    const bbox = new URL(url, 'http://localhost').searchParams.get('bbox')?.split(',').map(Number);
    expect(bbox).toHaveLength(4);
    expect(bbox![2] - bbox![0]).toBeLessThanOrEqual(0.02);
    expect(bbox![3] - bbox![1]).toBeLessThanOrEqual(0.02);
  }
});
