import { afterEach, describe, expect, it, vi } from 'vitest';

import { api } from './endpoints';

function respondWithJson(body: unknown): void {
  vi.stubGlobal('fetch', vi.fn(async () => Response.json(body)));
}

afterEach(() => {
  vi.unstubAllGlobals();
  sessionStorage.clear();
});

describe('detections API client', () => {
  it('searches detections with the supplied filters as a query string', async () => {
    respondWithJson([]);

    await api.detections.search({ plateNumber: 'MH12AB1234', limit: 50 });

    expect(fetch).toHaveBeenCalledWith(
      expect.stringMatching(/\/api\/v1\/detections\?.*plateNumber=MH12AB1234/),
      expect.anything(),
    );
  });

  it('omits filters that were not supplied', async () => {
    respondWithJson([]);

    await api.detections.search();

    expect(fetch).toHaveBeenCalledWith(expect.stringContaining('/api/v1/detections'), expect.anything());
    expect(String(vi.mocked(fetch).mock.calls[0][0])).not.toContain('?');
  });
});

describe('events API client', () => {
  it('queries events with the required time range', async () => {
    respondWithJson({ events: [], nextCursor: null });

    await api.events.query({ from: '2026-09-12T00:00:00.000Z', to: '2026-09-12T01:00:00.000Z' });

    expect(fetch).toHaveBeenCalledWith(
      expect.stringMatching(/\/api\/v1\/events\?.*from=2026-09-12T00%3A00%3A00\.000Z.*to=2026-09-12T01%3A00%3A00\.000Z/),
      expect.anything(),
    );
  });

  it('carries the cursor forward for the next page', async () => {
    respondWithJson({ events: [], nextCursor: null });

    await api.events.query({ from: '2026-09-12T00:00:00.000Z', to: '2026-09-12T01:00:00.000Z', cursor: 'opaque-cursor' });

    expect(fetch).toHaveBeenCalledWith(expect.stringContaining('cursor=opaque-cursor'), expect.anything());
  });
});
