import '@testing-library/jest-dom/vitest';
import { act, render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { afterEach, expect, it, vi } from 'vitest';
import { App } from '../../App';
import { AuthProvider } from '../../auth/AuthProvider';
import { saveSession } from '../../auth/session';
import { cameraFixture, sessionFixture } from '../../test/fixtures';

// WebGL is unavailable in jsdom. Only the external renderer is replaced; the
// production CameraMap, move debounce, MapPage, API and query cache all run.
const renderer = vi.hoisted(() => ({ move: () => {}, bounds: [-120, 35, -119.99, 35.01] }));
vi.mock('maplibre-gl', () => ({ default: {
  NavigationControl: class {},
  Map: class {
    addControl() {}
    on(event: string, callback: () => void) { if (event === 'moveend') renderer.move = callback; }
    getBounds() { return { getWest: () => renderer.bounds[0], getSouth: () => renderer.bounds[1], getEast: () => renderer.bounds[2], getNorth: () => renderer.bounds[3] }; }
    getSource() { return undefined; }
    remove() {}
  },
} }));

afterEach(() => { vi.restoreAllMocks(); vi.unstubAllGlobals(); sessionStorage.clear(); });

it('explains suppressed wide bounds instead of reporting an empty loaded camera set', async () => {
  saveSession(sessionFixture());
  vi.spyOn(navigator, 'userAgent', 'get').mockReturnValue('map-renderer-test');
  vi.stubGlobal('fetch', async (input: RequestInfo | URL) => {
    const url = new URL(String(input));
    if (url.pathname === '/api/v1/cameras') return Response.json({ items: [cameraFixture()], nextCursor: null });
    if (url.pathname === '/api/v1/overview') return Response.json({ targets: 0, activeTargets: 0, quarantinedTargets: 0, cameras: 1, unreachableCameras: 0 });
    return Response.json({ type: 'FeatureCollection', features: [] });
  });
  render(<MemoryRouter initialEntries={['/dashboard']}><AuthProvider><App /></AuthProvider></MemoryRouter>);
  await screen.findByText(/no individual cameras/i);
  act(() => { renderer.bounds = [-125, 30, -115, 40]; renderer.move(); });
  expect(await screen.findByText(/zoom in.*2°/i)).toBeVisible();
  expect(screen.queryByText(/no individual cameras/i)).not.toBeInTheDocument();
  expect(screen.queryByText(/0 visible cameras/i)).not.toBeInTheDocument();
});
