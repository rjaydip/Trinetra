import '@testing-library/jest-dom/vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { App } from '../../App';
import { AuthProvider } from '../../auth/AuthProvider';

function renderApp(path: string) {
  return render(
    <MemoryRouter initialEntries={[path]}>
      <AuthProvider><App /></AuthProvider>
    </MemoryRouter>,
  );
}

function signIn() {
  sessionStorage.setItem('trinetra.auth.session', JSON.stringify({
    token: 'reports-token',
    expiresAt: new Date(Date.now() + 60_000).toISOString(),
    mustChangePassword: false,
  }));
}

afterEach(() => {
  vi.unstubAllGlobals();
  sessionStorage.clear();
});

describe('ReportsPage', () => {
  it('does not present unavailable coverage gaps as zero', async () => {
    signIn();
    vi.stubGlobal('fetch', async () => Response.json({
      targets: 10, activeTargets: 8, quarantinedTargets: 2, cameras: 12, unreachableCameras: 3,
    }));

    renderApp('/reports');

    expect(await screen.findByText(/coverage-gap analysis is not available yet/i)).toBeVisible();
    expect(screen.queryByText(/^0 gaps$/i)).not.toBeInTheDocument();
    expect(screen.getByText(/ageing infrastructure reporting is not available yet/i)).toBeVisible();
  });

  it('renders backend fleet totals and selected coverage buckets without calling gaps', async () => {
    signIn();
    const fetch = vi.fn(async (input: RequestInfo | URL) => {
      const url = new URL(String(input));
      if (url.pathname === '/api/v1/overview') {
        return Response.json({ targets: 10, activeTargets: 8, quarantinedTargets: 2, cameras: 12, unreachableCameras: 3 });
      }
      if (url.pathname === '/api/v1/gis/coverage') {
        return Response.json({ buckets: { operationalStatus: { ACTIVE: 9, INACTIVE: 3 } } });
      }
      return new Response(null, { status: 404 });
    });
    vi.stubGlobal('fetch', fetch);
    const user = userEvent.setup();

    renderApp('/reports');

    expect(await screen.findByText('12')).toBeVisible();
    expect(screen.getByText(/3 unreachable/i)).toBeVisible();
    await user.type(screen.getByLabelText(/coverage bounding box/i), '77.5,12.9,77.6,13.0');
    await user.click(screen.getByRole('button', { name: /load coverage summary/i }));

    expect(await screen.findByText('ACTIVE')).toBeVisible();
    expect(screen.getByText('9')).toBeVisible();
    await waitFor(() => expect(fetch.mock.calls.map(([input]) => String(input))).not.toContainEqual(expect.stringContaining('/api/v1/gis/gaps')));
    expect(screen.getAllByText(/estimated planning aid/i)).not.toHaveLength(0);
  });
});
