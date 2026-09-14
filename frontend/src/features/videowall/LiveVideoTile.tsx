import { useQuery } from '@tanstack/react-query';
import Hls from 'hls.js';
import { useEffect, useRef, useState } from 'react';

import { isApiProblem } from '../../api/client';
import { api } from '../../api/endpoints';
import { queryKeys } from '../../api/queryKeys';
import { PageState } from '../../components/ui';

// The session token is good for 5 minutes (`StreamSessionEndpoints`/`JwtTokenService`); refetch a
// good bit before it expires so hls.js is never left holding a token that's about to be rejected
// mid-segment. TanStack Query's own `refetchInterval` is the established pattern for anything
// time-sensitive in this codebase (see `CredentialPanel`'s connection-test polling) — no separate
// timer to invent.
const SESSION_REFETCH_INTERVAL_MS = 4 * 60_000;

/** How long a "playing" video is allowed to go without a `timeupdate` before it's treated as
 * frozen rather than actually live — see the `onTimeUpdate`/`armFreezeWatch` comment below.
 * Deliberately generous: real RTSP cameras rebuffer for several seconds under ordinary network
 * jitter (lost RTP packets, a slow upstream) without anything being actually wrong, and
 * `timeupdate` stops firing for the whole rebuffer, not just the bad instant. An earlier, much
 * shorter value here (6s) mistook that routine rebuffering for a frozen feed and broke live view
 * for cameras that were never actually stuck. */
const FREEZE_TIMEOUT_MS = 25_000;

interface Failure {
  title: string;
  body: string;
}

const FAILURES = {
  // The ordinary, expected state for a camera with no gateway path provisioned — not alarming,
  // so its wording stays neutral rather than "error"-toned.
  unavailable: {
    title: 'Live feed not available',
    body: 'No streaming gateway is configured for this camera, so this tile cannot decode video.',
  },
  auth: {
    title: 'Authentication issue',
    body: 'Your session may have expired, or you no longer have permission to view this camera. Try signing in again.',
  },
  network: {
    title: 'Network issue',
    body: 'The streaming service could not be reached. Check your network connection and try again.',
  },
  playback: {
    title: 'Playback issue',
    body: "This camera's feed could not be played back — it may be using a codec this browser cannot decode.",
  },
  unsupported: {
    title: 'Live video not supported',
    body: 'This browser cannot play live camera feeds. Try a recent version of Chrome, Edge, or Safari.',
  },
  stalled: {
    title: 'Feed stalled',
    body: "This camera's feed stopped sending new video. Reconnecting may resolve this — try refreshing.",
  },
} as const satisfies Record<string, Failure>;

/** Maps the stream-session fetch's own failure to a specific reason instead of the generic
 * "not available" — a 404 there is the ordinary "no gateway for this camera" case (see
 * `StreamSessionEndpoints`), but a 401/403 is this viewer's own session/permissions, and anything
 * else (including a plain network failure, which never reaches `isApiProblem`) is the streaming
 * service itself being unreachable. */
function sessionFailure(error: unknown): Failure {
  if (isApiProblem(error)) {
    if (error.status === 401 || error.status === 403) return FAILURES.auth;
    if (error.status === 404) return FAILURES.unavailable;
  }
  return FAILURES.network;
}

/**
 * Plays a camera's live HLS feed, served by `Federation.Api` itself
 * (`GET /api/v1/streams/{cameraId}/{*hlsPath}`, `docs/STREAMING-GATEWAY-PLAN.md` G4/G5). Must use
 * hls.js rather than a bare `<video src>` — a native `<video>` element cannot send a custom
 * `Authorization` header, and this route requires one (a short-lived, single-camera bearer token)
 * on every playlist/segment request. hls.js does its own XHR for those requests and exposes
 * `xhrSetup` to attach the header.
 *
 * The stream URL is deliberately a *relative* path (`/api/v1/streams/...`), not
 * `${apiBaseUrl()}/...` the way every other API call in this app is built — see `StreamSessionEndpoints`'s
 * remarks: MediaMTX's own session-continuity cookie (relayed through this route's `302`) only
 * ever reaches the browser correctly when hls.js's requests are same-origin. A `SameSite=Lax`
 * cookie (the strongest this route can set without requiring HTTPS — see the class remarks on
 * `ApiOptionsExtensions`) is never sent by the browser on a cross-origin XHR/fetch, only on
 * top-level navigation, so an absolute cross-origin stream URL silently drops that cookie after
 * the very first request and every request after it fails MediaMTX's continuity check. A relative
 * path keeps the request on the frontend's own origin — resolved by Vite's dev proxy in
 * development, and by whatever serves the frontend and this API from one origin in production
 * (the deployment this cookie mechanism assumes) — where the cookie behaves like an ordinary
 * same-site cookie.
 *
 * Falls back to an honest, *specific* status panel whenever playback genuinely isn't available —
 * never a silent blank or black tile: no session (401/403 read as an auth problem, 404 as no
 * gateway configured — an expected state, not alarmed — anything else as a network problem),
 * hls.js unsupported in this browser, a fatal hls.js playback error/stall, or a "playing" video
 * that stops actually advancing (a black, frozen frame reads as fine to the browser's own
 * `playing` event even though nothing further is visibly happening).
 */
export function LiveVideoTile({ cameraId }: { cameraId: string }) {
  const videoRef = useRef<HTMLVideoElement>(null);
  // Read by `xhrSetup` on every request hls.js makes — must always reflect the latest fetched
  // token, not the one captured when the hls.js instance was created, since the session refreshes
  // underneath a still-playing instance. A plain ref assigned during render (rather than a
  // `useEffect` that re-runs on every token refresh) is enough: it doesn't affect what this
  // component renders, only what a later callback reads.
  const tokenRef = useRef<string | null>(null);
  const [failure, setFailure] = useState<Failure | null>(null);
  // hls.js retries a failed manifest/segment load several times before ever firing a fatal
  // error — left unhandled, that retry window renders a bare, empty <video> element (a
  // silent blank box) for several seconds before falling back. Tracked separately from
  // `failure` so the tile can show an honest "connecting" state for that whole window
  // instead of nothing, and a hard timeout (below) forces the fallback if the gateway is
  // simply unreachable rather than waiting out hls.js's full retry budget.
  const [playing, setPlaying] = useState(false);

  const session = useQuery({
    queryKey: queryKeys.streamSession(cameraId),
    queryFn: ({ signal }) => api.streams.session(cameraId, signal),
    retry: false,
    refetchInterval: SESSION_REFETCH_INTERVAL_MS,
    refetchOnWindowFocus: false,
  });

  tokenRef.current = session.data?.token ?? null;

  const mode = session.data?.mode;

  // WebRTC (WHEP) mode: signaling only goes through this API — the actual audio/video negotiates
  // and flows directly between this browser and the device (see `StreamSessionEndpoints`'s
  // remarks). No hls.js involved at all, so this effect is entirely separate from the HLS one
  // below rather than a branch inside it.
  useEffect(() => {
    setFailure(null);
    setPlaying(false);
    const video = videoRef.current;
    if (!session.data || !video || mode !== 'WEBRTC') return undefined;

    let cancelled = false;
    let teardownPath: string | null = null;
    const pc = new RTCPeerConnection();
    pc.addTransceiver('video', { direction: 'recvonly' });
    pc.addTransceiver('audio', { direction: 'recvonly' });
    pc.ontrack = (event) => {
      video.srcObject = event.streams[0] ?? null;
    };

    const controller = new AbortController();
    const stallTimeout = window.setTimeout(() => {
      controller.abort();
      setFailure(FAILURES.network);
    }, 10_000);

    async function negotiate() {
      const offer = await pc.createOffer();
      await pc.setLocalDescription(offer);

      const { answerSdp, teardownPath: relayedTeardownPath } = await api.streams.whepOffer(
        cameraId, tokenRef.current ?? '', offer.sdp ?? '', controller.signal,
      );

      teardownPath = relayedTeardownPath;
      if (cancelled) return;
      await pc.setRemoteDescription({ type: 'answer', sdp: answerSdp });
    }

    negotiate()
      .then(() => {
        if (!cancelled) window.clearTimeout(stallTimeout);
      })
      .catch(() => {
        if (!cancelled) setFailure(FAILURES.network);
      });

    pc.oniceconnectionstatechange = () => {
      if (cancelled) return;
      if (pc.iceConnectionState === 'connected' || pc.iceConnectionState === 'completed') {
        window.clearTimeout(stallTimeout);
        setPlaying(true);
        setFailure(null);
      } else if (pc.iceConnectionState === 'failed' || pc.iceConnectionState === 'disconnected') {
        setFailure(FAILURES.stalled);
      }
    };

    return () => {
      cancelled = true;
      window.clearTimeout(stallTimeout);
      controller.abort();
      pc.close();
      if (teardownPath) {
        void api.streams.whepTeardown(teardownPath, tokenRef.current ?? '');
      }
    };
  }, [session.data, cameraId, mode]);

  useEffect(() => {
    setFailure(null);
    setPlaying(false);
    const video = videoRef.current;
    if (!session.data || !video || mode === 'WEBRTC') return undefined;

    if (!Hls.isSupported()) {
      setFailure(FAILURES.unsupported);
      return undefined;
    }

    const hls = new Hls({
      xhrSetup: (xhr) => {
        if (tokenRef.current) xhr.setRequestHeader('Authorization', `Bearer ${tokenRef.current}`);
      },
    });

    // hls.js's documented pattern for a fatal error is to attempt its own recovery first —
    // startLoad() re-fetches the manifest from scratch (a fresh MediaMTX HLS session/cookie,
    // sidestepping whatever made the previous one stop being accepted, whether that's an idled-out
    // session, a dropped upstream RTSP connection, or anything else transient) — before treating
    // it as unrecoverable. Skipping this and jumping straight to the fallback panel on the first
    // fatal event is why a normal, expected MediaMTX session hiccup during otherwise-healthy
    // playback was surfacing as a permanent "not available" state instead of a brief reconnect.
    // One recovery attempt per error type; a second fatal error of the same kind gives up for real.
    let recoveredNetworkError = false;
    let recoveredMediaError = false;

    // The 8s patience budget must not fire WHILE a recovery attempt (startLoad /
    // recoverMediaError) is genuinely in progress — that would cut off a fix that was about to
    // work. Calling this again, not just on the initial connect, means "an error just happened
    // and hls.js is retrying" resets the clock instead of racing it.
    function armStallFallback() {
      return window.setTimeout(() => setFailure(FAILURES.network), 8000);
    }

    let stallTimeout = armStallFallback();
    // Armed once real playback starts, and re-armed on every `timeupdate` — a video that keeps
    // firing `playing` semantics but never advances its own clock is frozen (a black or stuck
    // last frame), which the browser's `playing` event alone can't distinguish from healthy
    // playback. Without this, that state fell all the way through to a blank tile with no
    // message at all, since nothing else here ever treats an already-"playing" video as failed.
    let freezeTimeout: number | undefined;
    function armFreezeWatch() {
      window.clearTimeout(freezeTimeout);
      freezeTimeout = window.setTimeout(() => setFailure(FAILURES.stalled), FREEZE_TIMEOUT_MS);
    }

    function onError(_event: string, data: { fatal: boolean; type?: string }) {
      if (!data.fatal) return;

      if (data.type === Hls.ErrorTypes.NETWORK_ERROR && !recoveredNetworkError) {
        recoveredNetworkError = true;
        window.clearTimeout(stallTimeout);
        hls.startLoad();
        stallTimeout = armStallFallback();
        return;
      }

      if (data.type === Hls.ErrorTypes.MEDIA_ERROR && !recoveredMediaError) {
        recoveredMediaError = true;
        window.clearTimeout(stallTimeout);
        hls.recoverMediaError();
        stallTimeout = armStallFallback();
        return;
      }

      window.clearTimeout(stallTimeout);
      window.clearTimeout(freezeTimeout);
      setFailure(data.type === Hls.ErrorTypes.NETWORK_ERROR ? FAILURES.network : FAILURES.playback);
    }

    function onPlaying() {
      setPlaying(true);
      // A `playing` event proves the feed is actually up — clear any earlier fallback (the
      // initial connect stall timing out, a transient hls.js error) so a slow-but-eventually-
      // successful start doesn't leave the tile stuck showing a failure message forever. Without
      // this, `failure` was only ever set, never unset by recovery — the render below still
      // returns the fallback panel even after playback resumes because nothing here reset it.
      setFailure(null);
      window.clearTimeout(stallTimeout);
      // A resumed, healthy playback session earns a fresh recovery budget — otherwise a stream
      // that hiccups once early on and recovers fine would have its recovery budget already
      // spent for a later, unrelated hiccup much further into what is by then a long, normal
      // viewing session.
      recoveredNetworkError = false;
      recoveredMediaError = false;
      armFreezeWatch();
    }

    function onTimeUpdate() {
      // Belt-and-suspenders alongside onPlaying's own setFailure(null): `timeupdate` is the
      // actual "still advancing" signal, so any ongoing progress clears a stale fallback too,
      // not just the `playing` event that normally (but doesn't strictly have to) precede it.
      setFailure(null);
      armFreezeWatch();
    }

    hls.on(Hls.Events.ERROR, onError);
    hls.on(Hls.Events.MEDIA_ATTACHED, () => {
      hls.loadSource(`/api/v1/streams/${cameraId}/index.m3u8`);
    });
    hls.attachMedia(video);
    video.addEventListener('playing', onPlaying);
    video.addEventListener('timeupdate', onTimeUpdate);

    return () => {
      window.clearTimeout(stallTimeout);
      window.clearTimeout(freezeTimeout);
      video.removeEventListener('playing', onPlaying);
      video.removeEventListener('timeupdate', onTimeUpdate);
      hls.destroy();
    };
    // `session.data` (not just its token) is the dependency: a new session object means a fresh
    // hls.js instance is (re)built against the current video element; the token itself is read
    // live via `tokenRef` inside `xhrSetup`, not captured here.
  }, [session.data, cameraId, mode]);

  // Once actually playing, a later fatal event (e.g. the gateway drops mid-stream) still falls
  // back correctly — `failure` isn't gated on `playing`, this only controls what's shown
  // *before* the first frame arrives, so a stalled connect never lingers as a blank <video>.
  const connecting = Boolean(session.data) && !playing && !failure;

  if (session.isPending) {
    return <PageState title="Connecting…">Requesting a live viewing session for this camera.</PageState>;
  }

  if (session.isError) {
    const { title, body } = sessionFailure(session.error);
    return <PageState title={title}>{body}</PageState>;
  }

  // The <video> element stays mounted for `failure` too, not just `connecting` — the effect
  // above needs a *stable* ref to attach hls.js to for the entire lifetime of that hls.js
  // instance. `failure` used to return a fallback panel in place of this whole element, which
  // unmounted the <video> hls.js was attached to; if playback later recovered (onPlaying/
  // onTimeUpdate clearing `failure`), React mounted a brand-new <video> node that the existing
  // hls.js instance had never heard of — nothing re-attached it, so the tile silently showed an
  // empty player with no error at all. Rendering the fallback as an overlay instead means
  // recovery just hides the overlay; the same live video element underneath was never touched.
  return <div className="videowall-tile__video-wrap">
    {connecting && <PageState title="Connecting…">Waiting for the first frame from this camera.</PageState>}
    {failure && <PageState title={failure.title}>{failure.body}</PageState>}
    <video
      ref={videoRef} className="videowall-tile__video" hidden={connecting || Boolean(failure)}
      muted autoPlay playsInline aria-label="Live camera feed"
    />
  </div>;
}
