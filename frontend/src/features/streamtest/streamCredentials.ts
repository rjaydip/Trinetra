/**
 * Pulls a `user:pass@` embedded in a stream URL (the form many camera/NVR-issued WHEP and HLS
 * URLs come in, the same way RTSP URLs do) out into separate, already-decoded strings, and
 * returns the URL with that userinfo stripped.
 *
 * Two things go wrong without this:
 *  - `fetch()` (the WHEP SDP POST) throws outright on any URL carrying embedded credentials —
 *    per the Fetch spec a request whose URL "includes credentials" is a hard network error, not
 *    a 401 — so a WHEP URL pasted with the login baked in never even reaches the server.
 *  - A literal, un-encoded `@` inside a username (an email address is the common case — see
 *    `provision_paths.py`'s own `rtsp_url.py` comment on exactly this) makes the *browser's own*
 *    URL parser misread where the userinfo actually ends: `https://a@b.com:pw@host/path` is
 *    ambiguous between "user `a@b.com`, password `pw`" and "user `a`, password `b.com:pw`" unless
 *    the `@` inside the username was percent-encoded (`%40`) when the URL was built. `URL`'s own
 *    `.username`/`.password` accessors decode/encode this correctly; splitting the string by hand
 *    would not.
 *
 * Explicit `username`/`password` fields (if the operator also filled those in) win over whatever
 * was embedded in the URL, on the assumption that a separately-typed credential is the more
 * deliberate one.
 */
export function extractStreamCredentials(rawUrl: string, username?: string, password?: string): {
  url: string;
  username?: string;
  password?: string;
} {
  let parsed: URL;
  try {
    parsed = new URL(rawUrl);
  } catch {
    // Not a well-formed absolute URL — hand it back unchanged and let the caller's own request
    // fail with whatever error is actually informative for that case.
    return { url: rawUrl, username, password };
  }

  const embeddedUsername = parsed.username ? decodeURIComponent(parsed.username) : undefined;
  const embeddedPassword = parsed.password ? decodeURIComponent(parsed.password) : undefined;
  parsed.username = '';
  parsed.password = '';

  return {
    url: parsed.toString(),
    username: username || embeddedUsername,
    password: password || embeddedPassword,
  };
}
