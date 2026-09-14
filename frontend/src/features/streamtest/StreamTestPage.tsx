import { useState, type FormEvent } from 'react';

import { PasswordInput } from '../../components/ui';
import { useDocumentTitle } from '../../lib/useDocumentTitle';
import { HlsUrlPlayer } from './HlsUrlPlayer';
import './streamtest.css';
import { WhepPlayer } from './WhepPlayer';

interface Connection {
  url: string;
  username?: string;
  password?: string;
}

/**
 * A throwaway diagnostic page, not part of the camera registry's real flows: paste a camera or
 * NVR's own native WebRTC (WHEP) and/or HLS URL — with a username/password if the device's own
 * stream endpoint needs them — and see whether the browser can actually play it directly, with no
 * involvement from this platform's streaming gateway/MediaMTX pipeline at all.
 *
 * Exists to answer that question BEFORE any real "stream preference" feature is built around it —
 * per the three-protocol split an operator actually wants: WebRTC for low-latency browser
 * preview, RTSP for AI-inference consumers (OpenCV/GStreamer/FFmpeg/DeepStream) that never touch
 * a browser at all, HLS for dashboards/mobile/restricted networks. This page only ever exercises
 * the browser-facing two (WebRTC, HLS); RTSP has no browser player and isn't tested here.
 */
export function StreamTestPage() {
  useDocumentTitle('Stream test');
  const [whepInput, setWhepInput] = useState({ url: '', username: '', password: '' });
  const [whepConnection, setWhepConnection] = useState<Connection | null>(null);
  const [hlsInput, setHlsInput] = useState({ url: '', username: '', password: '' });
  const [hlsConnection, setHlsConnection] = useState<Connection | null>(null);

  function connectWhep(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setWhepConnection({ url: whepInput.url.trim(), username: whepInput.username.trim() || undefined, password: whepInput.password || undefined });
  }

  function connectHls(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setHlsConnection({ url: hlsInput.url.trim(), username: hlsInput.username.trim() || undefined, password: hlsInput.password || undefined });
  }

  return <section className="stream-test-page" aria-labelledby="stream-test-title">
    <header>
      <p className="eyebrow">Diagnostic — not a real camera flow</p>
      <h1 id="stream-test-title">Stream test</h1>
      <p>Paste a camera or NVR's own native stream URL — with a username/password if that device's stream endpoint requires them — and see whether this browser can play it directly. No streaming gateway, no MediaMTX, no platform auth token; the credentials below go straight to the device itself as HTTP Basic auth. Use this to check what actually works before any real integration is built.</p>
    </header>

    <section className="stream-test-panel" aria-labelledby="whep-title">
      <h2 id="whep-title">WebRTC (WHEP)</h2>
      <p>Low-latency browser preview. Most cameras/NVRs that expose "WebRTC" speak WHEP (the same protocol MediaMTX's own WebRTC output uses) — paste that URL here.</p>
      <form onSubmit={connectWhep}>
        <label htmlFor="whep-url">WHEP URL
          <input id="whep-url" placeholder="https://camera-or-nvr/whep/..." type="url" value={whepInput.url} onChange={(event) => setWhepInput((current) => ({ ...current, url: event.target.value }))} />
        </label>
        <label htmlFor="whep-username">Username (optional)
          <input autoComplete="username" id="whep-username" value={whepInput.username} onChange={(event) => setWhepInput((current) => ({ ...current, username: event.target.value }))} />
        </label>
        <label htmlFor="whep-password">Password (optional)
          <PasswordInput autoComplete="current-password" id="whep-password" value={whepInput.password} onChange={(event) => setWhepInput((current) => ({ ...current, password: event.target.value }))} />
        </label>
        <button className="button" type="submit" disabled={!whepInput.url.trim()}>Connect</button>
      </form>
      {whepConnection && <WhepPlayer url={whepConnection.url} username={whepConnection.username} password={whepConnection.password} />}
    </section>

    <section className="stream-test-panel" aria-labelledby="hls-title">
      <h2 id="hls-title">HLS</h2>
      <p>For dashboards, mobile, or restricted networks. Paste the device's own .m3u8 URL — this plays it directly, not through this platform's streaming gateway.</p>
      <form onSubmit={connectHls}>
        <label htmlFor="hls-url">HLS URL
          <input id="hls-url" placeholder="https://camera-or-nvr/stream/index.m3u8" type="url" value={hlsInput.url} onChange={(event) => setHlsInput((current) => ({ ...current, url: event.target.value }))} />
        </label>
        <label htmlFor="hls-username">Username (optional)
          <input autoComplete="username" id="hls-username" value={hlsInput.username} onChange={(event) => setHlsInput((current) => ({ ...current, username: event.target.value }))} />
        </label>
        <label htmlFor="hls-password">Password (optional)
          <PasswordInput autoComplete="current-password" id="hls-password" value={hlsInput.password} onChange={(event) => setHlsInput((current) => ({ ...current, password: event.target.value }))} />
        </label>
        <button className="button" type="submit" disabled={!hlsInput.url.trim()}>Connect</button>
      </form>
      {hlsConnection && <HlsUrlPlayer url={hlsConnection.url} username={hlsConnection.username} password={hlsConnection.password} />}
    </section>

    <section className="stream-test-panel" aria-labelledby="rtsp-title">
      <h2 id="rtsp-title">RTSP</h2>
      <p>For AI-inference consumers (OpenCV, GStreamer, FFmpeg, DeepStream) — these connect to the camera directly and never go through a browser, so there is nothing to test here. This section is informational only.</p>
    </section>
  </section>;
}
