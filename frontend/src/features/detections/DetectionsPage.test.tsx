import '@testing-library/jest-dom/vitest';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { DetectionsPage } from './DetectionsPage';

const detection = {
  id: 'evt-1', cameraId: '11111111-1111-4111-8111-111111111111:CAM-07', registeredCameraId: null,
  eventType: 'ANPR_DETECTED', timestamp: '2026-09-12T10:00:00Z', confidence: 0.92,
  vehicleType: 'CAR', plateNumber: 'MH12AB1234', snapshotReference: null,
};

function renderPage() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(<QueryClientProvider client={queryClient}><DetectionsPage /></QueryClientProvider>);
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('DetectionsPage', () => {
  it('renders matching detections with a human-readable confidence', async () => {
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => {
      const url = new URL(String(input));
      if (url.pathname === '/api/v1/vms') return Response.json([]);
      if (url.pathname === '/api/v1/detections') return Response.json([detection]);
      return new Response(null, { status: 404 });
    }));
    renderPage();

    expect(await screen.findByText('MH12AB1234')).toBeVisible();
    expect(screen.getByText('92%')).toBeVisible();
  });

  it('re-queries with the plate number filter', async () => {
    const requests: string[] = [];
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => {
      const url = new URL(String(input));
      requests.push(`${url.pathname}${url.search}`);
      if (url.pathname === '/api/v1/vms') return Response.json([]);
      return Response.json([]);
    }));
    const user = userEvent.setup();
    renderPage();

    await screen.findByText(/no detections matched/i);
    await user.type(screen.getByLabelText(/plate number/i), 'MH12AB1234');

    await waitFor(() => expect(requests.some((path) => path.includes('plateNumber=MH12AB1234'))).toBe(true));
  });

  it('shows an empty state when nothing matches', async () => {
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => {
      const url = new URL(String(input));
      if (url.pathname === '/api/v1/vms') return Response.json([]);
      return Response.json([]);
    }));
    renderPage();

    expect(await screen.findByText(/no detections matched/i)).toBeVisible();
  });
});
