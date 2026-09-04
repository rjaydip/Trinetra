import '@testing-library/jest-dom/vitest';
import { render, screen } from '@testing-library/react';
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
