import '@testing-library/jest-dom/vitest';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { EventsPage } from './EventsPage';

const event = {
  eventId: 'evt-1', sourceVmsId: '11111111-1111-4111-8111-111111111111', cameraId: 'CAM-07',
  eventType: 'MOTION_DETECTED', vendorEventType: 'VMD', occurredAt: '2026-09-12T10:00:00Z',
  severity: 'High', objectReference: null, confidence: null,
};

function renderPage() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(<QueryClientProvider client={queryClient}><EventsPage /></QueryClientProvider>);
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('EventsPage', () => {
  it('queries with the default one-hour window and renders results', async () => {
    const requests: string[] = [];
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => {
      const url = new URL(String(input));
      requests.push(`${url.pathname}${url.search}`);
      return Response.json({ events: [event], nextCursor: null });
    }));
    renderPage();

    expect(await screen.findByText('MOTION_DETECTED')).toBeVisible();
    expect(requests[0]).toContain('/api/v1/events?');
    expect(requests[0]).toContain('from=');
    expect(requests[0]).toContain('to=');
  });

  it('resets the cursor when a filter changes', async () => {
    const requests: string[] = [];
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => {
      const url = new URL(String(input));
      requests.push(`${url.pathname}${url.search}`);
      return Response.json({ events: [], nextCursor: 'opaque-cursor-1' });
    }));
    const user = userEvent.setup();
    renderPage();

    await screen.findByText(/no events matched/i);
    await user.click(screen.getByRole('button', { name: /^next$/i }));
    await waitFor(() => expect(requests.some((path) => path.includes('cursor=opaque-cursor-1'))).toBe(true));

    await user.type(screen.getByLabelText(/camera id/i), 'CAM-07');
    await waitFor(() => expect(requests.at(-1)).not.toContain('cursor='));
  });

  it('refuses to query when the range is invalid', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => Response.json({ events: [], nextCursor: null })));
    const user = userEvent.setup();
    renderPage();

    const fromInput = screen.getByLabelText(/^from/i);
    await user.clear(fromInput);
    await user.type(fromInput, '2030-01-01T00:00');

    expect(await screen.findByText(/must be after/i)).toBeVisible();
  });
});
