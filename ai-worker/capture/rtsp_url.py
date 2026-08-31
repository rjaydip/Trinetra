"""Credential handling for RTSP URLs.

The Trinetra registry never hands out credential *values* (a Model 3 invariant), so a camera
discovered through `CAMERA_SOURCE=trinetra` arrives as a credential-free `rtsp://host/path`
URL. Cameras that require RTSP authentication then fail the initial `DESCRIBE` with 401.

`apply_credentials` folds an operator-supplied username/password into such a URL at connect
time; `redact` masks any userinfo so a URL can be logged without leaking the secret. Both are
no-ops for a URL that already carries its own credentials, and for non-RTSP schemes.
"""

from __future__ import annotations

import re
from urllib.parse import quote, urlsplit, urlunsplit

_RTSP_SCHEMES = ("rtsp", "rtsps")
_USERINFO_RE = re.compile(r"(?i)(rtsps?://)[^/@\s]*@")


def apply_credentials(url: str, username: str | None, password: str | None) -> str:
    """Return `url` with `username`/`password` injected as userinfo.

    Unchanged when the URL is not RTSP, already has userinfo, or no credentials were supplied.
    Both parts are percent-encoded, so a password containing `@`, `:` or `/` is safe to pass
    verbatim.
    """
    if not username or not password:
        return url

    parts = urlsplit(url)
    if parts.scheme.lower() not in _RTSP_SCHEMES or parts.username is not None:
        return url

    host = parts.hostname or ""
    if parts.port is not None:
        host = f"{host}:{parts.port}"
    netloc = f"{quote(username, safe='')}:{quote(password, safe='')}@{host}"
    return urlunsplit((parts.scheme, netloc, parts.path, parts.query, parts.fragment))


def redact(url: str) -> str:
    """Replace any `user:pass@` userinfo in an RTSP URL with `***@` for safe logging."""
    return _USERINFO_RE.sub(r"\1***@", url)
