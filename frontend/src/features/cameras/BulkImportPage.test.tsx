import '@testing-library/jest-dom/vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { App } from '../../App';
import { AuthProvider } from '../../auth/AuthProvider';

function renderApp(path: string) {
  return render(<MemoryRouter initialEntries={[path]}><AuthProvider><App /></AuthProvider></MemoryRouter>);
}

function signIn() {
  sessionStorage.setItem('trinetra.auth.session', JSON.stringify({
    token: 'import-token', expiresAt: new Date(Date.now() + 60_000).toISOString(), mustChangePassword: false,
  }));
}

async function uploadJson(name: string, items: unknown[]) {
  const user = userEvent.setup();
  const file = new File([JSON.stringify({ mode: 'insert', items })], name, { type: 'application/json' });
  await user.upload(screen.getByLabelText(/import JSON file/i), file);
  return user;
}

afterEach(() => {
  vi.unstubAllGlobals();
  sessionStorage.clear();
});

describe('BulkImportPage', () => {
  it('shows row errors supplied by the bulk import result', async () => {
    signIn();
    vi.stubGlobal('fetch', async (input: RequestInfo | URL) => {
      const url = new URL(String(input));
      if (url.pathname === '/api/v1/cameras/bulk-import') {
        return Response.json({
          created: 0, updated: 0, failed: 1,
          rows: [{ index: 0, cameraCode: 'BAD', status: 'error', cameraId: null, error: 'cameraType is required.' }],
        });
      }
      return new Response(null, { status: 404 });
    });

    renderApp('/cameras/import');
    const user = await uploadJson('cameras.json', [{ cameraCode: 'BAD' }]);
    await user.click(await screen.findByRole('button', { name: /import cameras/i }));

    expect(await screen.findByText(/cameraType is required/i)).toBeVisible();
  });

  it('rejects non-JSON uploads before submitting', async () => {
    signIn();
    const fetch = vi.fn();
    vi.stubGlobal('fetch', fetch);
    renderApp('/cameras/import');
    const user = userEvent.setup({ applyAccept: false });
    await user.upload(screen.getByLabelText(/import JSON file/i), new File(['cameraCode,name'], 'cameras.csv', { type: 'text/csv' }));

    expect(await screen.findByText(/select a \.json file/i)).toBeVisible();
    expect(fetch).not.toHaveBeenCalled();
  });

  it('previews only a locally valid 1–500 item import envelope', async () => {
    signIn();
    renderApp('/cameras/import');
    await uploadJson('cameras.json', []);

    expect(await screen.findByText(/between 1 and 500 items/i)).toBeVisible();
    expect(screen.queryByRole('button', { name: /import cameras/i })).not.toBeInTheDocument();
  });
});
