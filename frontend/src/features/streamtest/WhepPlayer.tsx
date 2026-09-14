import { useEffect, useRef, useState } from 'react';

import { extractStreamCredentials } from './streamCredentials';

type Status = 'idle' | 'connecting' | 'connected' | 'error';

/** `fetch()` has no default timeout — a device that silently drops the request (a CORS
 * preflight it never answers, a network path that never completes) leaves the tile "Connecting…"
 * forever with no error at all. This bounds the wait so a hang becomes an actual diagnostic. */
const CONNECT_TIMEOUT_MS = 10_000;

/**
 * A minimal WHEP (WebRTC-HTTP Egress Protocol) player — the de facto standard a camera/NVR's own
 * "WebRTC URL" almost always speaks (it's also what MediaMTX's own WebRTC output uses). Not tied
 * to this platform's streaming gateway or auth at all: it POSTs an SDP offer straight to whatever
 * URL is pasted in and plays back whatever answers. Exists purely to answer "does this device's
 * WebRTC URL actually work from a browser" before any real integration is built around it — see
 * `StreamTestPage`.
 */
export function WhepPlayer({ url, username, password }: { url: string; username?: string; password?: string }) {
  const videoRef = useRef<HTMLVideoElement>(null);
  const [status, setStatus] = useState<Status>('idle');
  const [detail, setDetail] = useState('');

  useEffect(() => {
    if (!url) {
      setStatus('idle');
      return undefined;
    }

    let cancelled = false;
    let resourceUrl: string | null = null;
    setStatus('connecting');
    setDetail('');

    const pc = new RTCPeerConnection();
    pc.addTransceiver('video', { direction: 'recvonly' });
    pc.addTransceiver('audio', { direction: 'recvonly' });
    pc.ontrack = (event) => {
      if (videoRef.current && videoRef.current.srcObject !== event.streams[0]) {
        videoRef.current.srcObject = event.streams[0];
      }
    };
    pc.onconnectionstatechange = () => {
      if (cancelled) return;
      if (pc.connectionState === 'connected') setStatus('connected');
      if (pc.connectionState === 'failed' || pc.connectionState === 'closed') {
        setStatus('error');
        setDetail(`Peer connection ${pc.connectionState}.`);
      }
    };

    // fetch() hard-fails (a network error, not a 401) on any URL that carries embedded
    // credentials — a WHEP URL a device hands out as `https://user:pass@host/whep/...` never
    // even reaches the server otherwise. Strip that userinfo out and use it (or the explicit
    // username/password fields, which win if both are given) as a real Authorization header
    // instead — see `extractStreamCredentials` for why a hand-rolled string split isn't safe
    // once the username itself contains an `@` (an email address, the common case here).
    const resolved = extractStreamCredentials(url, username, password);
    const controller = new AbortController();
    const timeout = window.setTimeout(() => controller.abort(), CONNECT_TIMEOUT_MS);

    (async () => {
      try {
        const offer = await pc.createOffer();
        await pc.setLocalDescription(offer);

        const headers: Record<string, string> = { 'Content-Type': 'application/sdp' };
        // Basic auth, offered as a convenience for a camera/NVR whose WHEP endpoint sits behind
        // it — not this platform's own auth, since this page never goes through the streaming
        // gateway at all (see the class remarks).
        if (resolved.username) headers.Authorization = `Basic ${btoa(`${resolved.username}:${resolved.password ?? ''}`)}`;

        const response = await fetch(resolved.url, {
          method: 'POST',
          headers,
          body: offer.sdp,
          signal: controller.signal,
        });
        if (!response.ok) {
          throw new Error(`WHEP endpoint returned ${response.status} ${response.statusText}`);
        }
        const location = response.headers.get('Location');
        resourceUrl = location ? new URL(location, resolved.url).toString() : null;

        const answerSdp = await response.text();
        if (cancelled) return;
        await pc.setRemoteDescription({ type: 'answer', sdp: answerSdp });
      } catch (error) {
        if (cancelled) return;
        setStatus('error');
        if (error instanceof DOMException && error.name === 'AbortError') {
          setDetail(
            `No response after ${CONNECT_TIMEOUT_MS / 1000}s. This usually means the URL isn't reachable from this `
            + 'browser, or the device rejected/ignored a CORS preflight request — check that the device is on a '
            + 'network this browser can reach, and that it sends Access-Control-Allow-Origin (and responds to an '
            + 'OPTIONS request) for this page\'s origin.',
          );
        } else {
          setDetail(error instanceof Error ? error.message : 'Unable to establish the WebRTC connection.');
        }
      } finally {
        window.clearTimeout(timeout);
      }
    })();

    return () => {
      cancelled = true;
      window.clearTimeout(timeout);
      pc.close();
      // WHEP's teardown convention: DELETE the session resource the server handed back so it can
      // free the peer connection immediately instead of waiting out its own idle timeout.
      if (resourceUrl) void fetch(resourceUrl, { method: 'DELETE' }).catch(() => {});
    };
  }, [url, username, password]);

  return <div className="stream-test__player">
    <video ref={videoRef} autoPlay muted playsInline aria-label="WebRTC test preview" />
    <p role="status">{
      status === 'idle' ? 'Enter a WebRTC (WHEP) URL above and click Connect.'
        : status === 'connecting' ? 'Connecting…'
          : status === 'connected' ? 'Connected.'
            : `Connection failed. ${detail}`
    }</p>
  </div>;
}
