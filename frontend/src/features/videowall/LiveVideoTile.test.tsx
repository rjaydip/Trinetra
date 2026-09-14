import '@testing-library/jest-dom/vitest';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, render, screen, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { LiveVideoTile } from './LiveVideoTile';

// A minimal hls.js stand-in, in the same spirit as this codebase's `maplibre-gl` mock
// (`MapViewport.test.tsx`) — jsdom has no MediaSource Extensions, so the real library would
// always report itself unsupported here, which would only let us test the fallback path.
const hls = vi.hoisted(() => ({
  instances: [] as Array<{
    config: unknown; destroyed: boolean; loadedUrl: string | null;
    startLoadCalls: number; recoverMediaErrorCalls: number;
    emitError(data: { fatal: boolean; type?: string }): void;
  }>,
}));

vi.mock('hls.js', () => {
  class FakeHls {
    static isSupported() { return true; }
    static Events = { ERROR: 'hlsError', MEDIA_ATTACHED: 'hlsMediaAttached' };
    static ErrorTypes = { NETWORK_ERROR: 'networkError', MEDIA_ERROR: 'mediaError' };
    private listeners: Record<string, ((...args: unknown[]) => void)[]> = {};
    destroyed = false;
    loadedUrl: string | null = null;
    config: unknown;
    startLoadCalls = 0;
    recoverMediaErrorCalls = 0;
    constructor(config: unknown) {
      this.config = config;
      hls.instances.push(this);
    }
    on(event: string, callback: (...args: unknown[]) => void) {
      (this.listeners[event] ??= []).push(callback);
    }
    attachMedia() {
      this.listeners.hlsMediaAttached?.forEach((cb) => cb('hlsMediaAttached', {}));
    }
    loadSource(url: string) {
      this.loadedUrl = url;
    }
    startLoad() {
      this.startLoadCalls += 1;
    }
    recoverMediaError() {
      this.recoverMediaErrorCalls += 1;
    }
    destroy() {
      this.destroyed = true;
    }
    emitError(data: { fatal: boolean; type?: string }) {
      this.listeners.hlsError?.forEach((cb) => cb('hlsError', data));
    }
  }
  return { default: FakeHls };
});

const cameraId = '11111111-1111-4111-8111-111111111111';
const otherCameraId = '22222222-2222-4222-8222-222222222222';

function renderTile(id: string) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const result = render(<QueryClientProvider client={queryClient}><LiveVideoTile cameraId={id} /></QueryClientProvider>);
  return { queryClient, ...result };
}

afterEach(() => {
  vi.unstubAllGlobals();
  hls.instances.length = 0;
});

describe('LiveVideoTile', () => {
  it('fetches a session and initializes hls.js against the streaming gateway on success', async () => {
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => {
      const url = new URL(String(input));
      if (url.pathname === `/api/v1/streams/${cameraId}/session`) {
        return Response.json({ cameraId, token: 'session-token-1', expiresAt: new Date(Date.now() + 300_000).toISOString() });
      }
      return new Response(null, { status: 404 });
    }));

    renderTile(cameraId);

    await waitFor(() => expect(hls.instances).toHaveLength(1));
    expect(hls.instances[0].loadedUrl).toContain(`/streams/${cameraId}/index.m3u8`);
    expect(screen.queryByText(/live feed not available/i)).not.toBeInTheDocument();
  });

  it('falls back to the honest panel when no gateway session is available (404)', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => new Response(null, { status: 404 })));

    renderTile(cameraId);

    expect(await screen.findByText(/live feed not available/i)).toBeVisible();
    expect(hls.instances).toHaveLength(0);
  });

  it('recovers from a fatal network/media error via hls.js\'s own retry instead of falling back immediately', async () => {
    // Regression test: a fatal error used to go straight to the fallback panel on the very first
    // occurrence, even though hls.js's documented pattern is to attempt its own recovery
    // (startLoad / recoverMediaError) first — this is what made an ordinary, transient MediaMTX
    // session hiccup during otherwise-healthy playback look like a permanent failure.
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => {
      const url = new URL(String(input));
      if (url.pathname === `/api/v1/streams/${cameraId}/session`) {
        return Response.json({ cameraId, token: 'session-token-1', expiresAt: new Date(Date.now() + 300_000).toISOString() });
      }
      return new Response(null, { status: 404 });
    }));

    renderTile(cameraId);
    await waitFor(() => expect(hls.instances).toHaveLength(1));
    const instance = hls.instances[0];

    // A fatal network error is recovered from via startLoad(), not an immediate fallback.
    act(() => instance.emitError({ fatal: true, type: 'networkError' }));
    expect(instance.startLoadCalls).toBe(1);
    expect(screen.queryByText(/live feed not available/i)).not.toBeInTheDocument();

    // A second fatal network error (recovery budget already spent) gives up for real — as a
    // network issue specifically, not the generic "not available" panel.
    act(() => instance.emitError({ fatal: true, type: 'networkError' }));
    expect(instance.startLoadCalls).toBe(1); // no second attempt
    expect(await screen.findByText(/network issue/i)).toBeVisible();
  });

  it('keeps the same <video> element mounted across a failure, so a later recovery has something to actually attach to', async () => {
    // Regression test: showing the fallback panel used to replace the whole tile, unmounting the
    // <video> hls.js was attached to. If playback then recovered (a `playing`/`timeupdate` event
    // clearing the failure), React mounted a *new* <video> node that the existing hls.js instance
    // had never heard of — nothing re-attached it, so the tile went on showing an empty player
    // with no error message at all, forever. The fallback must render as an overlay instead, so
    // the same <video> node survives the whole failure -> recovery cycle.
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => {
      const url = new URL(String(input));
      if (url.pathname === `/api/v1/streams/${cameraId}/session`) {
        return Response.json({ cameraId, token: 'session-token-1', expiresAt: new Date(Date.now() + 300_000).toISOString() });
      }
      return new Response(null, { status: 404 });
    }));

    renderTile(cameraId);
    await waitFor(() => expect(hls.instances).toHaveLength(1));
    const instance = hls.instances[0];
    const videoBeforeFailure = document.querySelector('video');
    expect(videoBeforeFailure).not.toBeNull();

    // Two fatal network errors: the first recovers via startLoad(), the second gives up and
    // shows the "Network issue" fallback.
    act(() => instance.emitError({ fatal: true, type: 'networkError' }));
    act(() => instance.emitError({ fatal: true, type: 'networkError' }));
    expect(await screen.findByText(/network issue/i)).toBeVisible();
    expect(document.querySelector('video')).toBe(videoBeforeFailure);

    // Playback actually recovers — the fallback clears, and it must be the SAME <video> node
    // still underneath, not a freshly mounted, unattached one.
    act(() => { videoBeforeFailure!.dispatchEvent(new Event('playing')); });
    expect(screen.queryByText(/network issue/i)).not.toBeInTheDocument();
    expect(document.querySelector('video')).toBe(videoBeforeFailure);
    expect(document.querySelector('video')).not.toHaveAttribute('hidden');
  });

  it('shows a "Connecting…" state instead of a blank video while waiting for the first frame, and falls back after a stall', async () => {
    // Regression test: hls.js retries a failed load several times before it ever reports a
    // fatal error, and until this fix the tile rendered a bare, empty <video> for that whole
    // window — a silent blank box, not an error state. The stall timeout forces the same
    // honest fallback without waiting out hls.js's full retry budget.
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => {
      const url = new URL(String(input));
      if (url.pathname === `/api/v1/streams/${cameraId}/session`) {
        return Response.json({ cameraId, token: 'session-token-1', expiresAt: new Date(Date.now() + 300_000).toISOString() });
      }
      return new Response(null, { status: 404 });
    }));

    vi.useFakeTimers({ shouldAdvanceTime: true });
    renderTile(cameraId);

    await vi.waitFor(() => expect(hls.instances).toHaveLength(1));
    expect(screen.getByText(/connecting/i)).toBeVisible();
    expect(document.querySelector('video')).toHaveAttribute('hidden');
    expect(screen.queryByText(/live feed not available/i)).not.toBeInTheDocument();

    await act(async () => { vi.advanceTimersByTime(8000); });

    expect(screen.getByText(/network issue/i)).toBeVisible();
    vi.useRealTimers();
  });

  it('falls back to a "Feed stalled" message when a "playing" video stops actually advancing', async () => {
    // Regression test: a video that fires `playing` once but then freezes on a black or stuck
    // frame reads as healthy to every other check here (no hls.js error, `playing` already
    // true) — before this fix that state showed nothing at all, an indefinitely blank/black
    // tile with no message a viewer could act on.
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => {
      const url = new URL(String(input));
      if (url.pathname === `/api/v1/streams/${cameraId}/session`) {
        return Response.json({ cameraId, token: 'session-token-1', expiresAt: new Date(Date.now() + 300_000).toISOString() });
      }
      return new Response(null, { status: 404 });
    }));

    vi.useFakeTimers({ shouldAdvanceTime: true });
    renderTile(cameraId);
    await vi.waitFor(() => expect(hls.instances).toHaveLength(1));

    const video = document.querySelector('video')!;
    act(() => { video.dispatchEvent(new Event('playing')); });
    expect(screen.queryByText(/connecting/i)).not.toBeInTheDocument();
    expect(video).not.toHaveAttribute('hidden');

    await act(async () => { vi.advanceTimersByTime(25000); });

    expect(screen.getByText(/feed stalled/i)).toBeVisible();
    vi.useRealTimers();
  });

  it('does not report a stall while a "playing" video keeps advancing via timeupdate', async () => {
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => {
      const url = new URL(String(input));
      if (url.pathname === `/api/v1/streams/${cameraId}/session`) {
        return Response.json({ cameraId, token: 'session-token-1', expiresAt: new Date(Date.now() + 300_000).toISOString() });
      }
      return new Response(null, { status: 404 });
    }));

    vi.useFakeTimers({ shouldAdvanceTime: true });
    renderTile(cameraId);
    await vi.waitFor(() => expect(hls.instances).toHaveLength(1));

    const video = document.querySelector('video')!;
    act(() => { video.dispatchEvent(new Event('playing')); });

    // Two ticks, each inside the freeze window, each resetting it — still no stall reported.
    await act(async () => { vi.advanceTimersByTime(20000); });
    act(() => { video.dispatchEvent(new Event('timeupdate')); });
    await act(async () => { vi.advanceTimersByTime(20000); });
    act(() => { video.dispatchEvent(new Event('timeupdate')); });

    expect(screen.queryByText(/feed stalled/i)).not.toBeInTheDocument();
    vi.useRealTimers();
  });

  it('tears down the old hls.js instance when the assigned camera changes', async () => {
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => {
      const url = new URL(String(input));
      const matchesId = url.pathname === `/api/v1/streams/${cameraId}/session` || url.pathname === `/api/v1/streams/${otherCameraId}/session`;
      if (matchesId) {
        const id = url.pathname.includes(otherCameraId) ? otherCameraId : cameraId;
        return Response.json({ cameraId: id, token: `token-${id}`, expiresAt: new Date(Date.now() + 300_000).toISOString() });
      }
      return new Response(null, { status: 404 });
    }));

    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const { rerender } = render(<QueryClientProvider client={queryClient}><LiveVideoTile cameraId={cameraId} /></QueryClientProvider>);

    await waitFor(() => expect(hls.instances).toHaveLength(1));
    const first = hls.instances[0];
    expect(first.destroyed).toBe(false);

    rerender(<QueryClientProvider client={queryClient}><LiveVideoTile cameraId={otherCameraId} /></QueryClientProvider>);

    await waitFor(() => expect(hls.instances).toHaveLength(2));
    expect(first.destroyed).toBe(true);
  });
});
