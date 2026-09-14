import Hls from 'hls.js';
import { useEffect, useRef, useState } from 'react';

import { extractStreamCredentials } from './streamCredentials';

type Status = 'idle' | 'connecting' | 'playing' | 'error';

/** Neither a bare `<video src>` load nor hls.js's own manifest-loading timeout is guaranteed to
 * surface an unreachable/CORS-blocked device as a visible error in every case — this bounds the
 * "Connecting…" state so a silent hang becomes an actual diagnostic instead of spinning forever. */
const CONNECT_TIMEOUT_MS = 10_000;

/**
 * Plays a raw HLS URL directly — no auth header, no streaming-gateway proxying, unlike
 * `LiveVideoTile`. Exists purely to answer "does this device's native HLS URL actually work from
 * a browser" before deciding whether it needs to go through this platform's proxy at all — see
 * `StreamTestPage`. Native `<video src>` is tried first (Safari can play HLS without hls.js);
 * hls.js is the fallback everywhere else, same as the real gateway player.
 */
export function HlsUrlPlayer({ url, username, password }: { url: string; username?: string; password?: string }) {
  const videoRef = useRef<HTMLVideoElement>(null);
  const [status, setStatus] = useState<Status>('idle');
  const [detail, setDetail] = useState('');

  useEffect(() => {
    const video = videoRef.current;
    if (!url || !video) {
      setStatus('idle');
      return undefined;
    }

    setStatus('connecting');
    setDetail('');

    const stallTimeout = window.setTimeout(() => {
      setStatus('error');
      setDetail(
        `No playable video after ${CONNECT_TIMEOUT_MS / 1000}s. This usually means the URL isn't reachable from `
        + 'this browser, or the device rejected/ignored a CORS preflight request — check that the device is on a '
        + 'network this browser can reach, and that it sends Access-Control-Allow-Origin for this page\'s origin.',
      );
    }, CONNECT_TIMEOUT_MS);

    function onPlaying() {
      window.clearTimeout(stallTimeout);
      setStatus('playing');
    }
    video.addEventListener('playing', onPlaying);

    // Separates any `user:pass@` already embedded in the pasted URL from the base URL itself —
    // both so a username containing a literal `@` (an email address, the common case) round-
    // trips correctly instead of being misread as ending the userinfo early, and so the explicit
    // username/password fields (if filled in) can cleanly win over whatever was in the URL.
    const resolved = extractStreamCredentials(url, username, password);

    if (video.canPlayType('application/vnd.apple.mpegurl')) {
      // Native <video src> can't set a custom Authorization header — basic-auth credentials
      // embedded in the URL itself is the only way to carry them here. Deprecated, but still
      // honored by Safari's own HLS player for exactly this case. Re-embedding through the URL
      // API's own username/password setters (rather than string concatenation) is what makes an
      // `@` or `:` inside either value round-trip correctly.
      const parsed = new URL(resolved.url);
      if (resolved.username) {
        parsed.username = resolved.username;
        parsed.password = resolved.password ?? '';
      }
      video.src = parsed.toString();
      return () => {
        window.clearTimeout(stallTimeout);
        video.removeEventListener('playing', onPlaying);
      };
    }

    if (!Hls.isSupported()) {
      window.clearTimeout(stallTimeout);
      setStatus('error');
      setDetail('This browser cannot play HLS.');
      return () => video.removeEventListener('playing', onPlaying);
    }

    const hls = new Hls({
      xhrSetup: (xhr) => {
        if (resolved.username) xhr.setRequestHeader('Authorization', `Basic ${btoa(`${resolved.username}:${resolved.password ?? ''}`)}`);
      },
    });
    hls.on(Hls.Events.ERROR, (_event, data) => {
      if (!data.fatal) return;
      window.clearTimeout(stallTimeout);
      setStatus('error');
      setDetail(data.type);
    });
    hls.loadSource(resolved.url);
    hls.attachMedia(video);

    return () => {
      window.clearTimeout(stallTimeout);
      video.removeEventListener('playing', onPlaying);
      hls.destroy();
    };
  }, [url, username, password]);

  return <div className="stream-test__player">
    <video ref={videoRef} autoPlay muted playsInline aria-label="HLS test preview" />
    <p role="status">{
      status === 'idle' ? 'Enter an HLS (.m3u8) URL above and click Connect.'
        : status === 'connecting' ? 'Connecting…'
          : status === 'playing' ? 'Playing.'
            : `Playback failed. ${detail}`
    }</p>
  </div>;
}
