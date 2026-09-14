using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http.HttpResults;
using Trinetra.Federation.Api.Auth;
using Trinetra.Federation.Api.Contracts;
using Trinetra.Federation.Api.OpenApi;
using Trinetra.Federation.Core.Abstractions;
using Trinetra.Federation.Core.Model;
using Trinetra.Federation.Storage.Repositories;

namespace Trinetra.Federation.Api.Endpoints;

/// <summary>
/// Live viewing session tokens, and the proxies that serve the video itself — G2 and G4 in
/// <c>docs/STREAMING-GATEWAY-PLAN.md</c>, extended by v1.25 to also cover a camera's own native
/// HLS/WHEP URL rather than always going through MediaMTX.
/// </summary>
/// <remarks>
/// <para>
/// <c>GET /{cameraId}/session</c> runs the same <c>camera.read</c> + org/geo scope check as any
/// other camera-scoped single-row read (<c>CameraRepository.GetAsync</c>, out-of-scope reading as
/// 404 exactly like <see cref="CameraCredentialEndpoints"/>), then mints a short-lived token
/// scoped to that one camera through <see cref="JwtTokenService"/>'s existing signing-key ring —
/// no second HMAC scheme. The token carries a distinct audience
/// (<see cref="JwtTokenService.StreamSessionAudience"/>), purpose claim, and (v1.25) the camera's
/// own <c>streamPreference</c> as a <see cref="TrinetraClaims.StreamMode"/> claim, so it can never
/// be replayed as a bearer token against the rest of this API, and nothing about the caller (no
/// user id, permissions or role) — only the camera id, its stream mode, and an expiry a few
/// minutes out.
/// </para>
/// <para>
/// <c>GET /{cameraId}/{*hlsPath}</c> is HLS playback: it validates that same token, confirms the
/// token's camera id matches the one in the URL (a valid token for camera A must not be
/// replayable against camera B by editing the path), then reverse-proxies to either MediaMTX
/// (<c>RTSP</c>/<c>HLS</c>... no — RTSP mode only) or the camera's own <c>nativeHlsUrl</c>
/// (<c>HLS</c> mode), per the token's embedded stream mode — see
/// <see cref="JwtTokenService.IssueStreamSession"/>'s remarks on why that is embedded rather than
/// looked up per request. <c>POST /{cameraId}/whep</c> and
/// <c>DELETE /{cameraId}/whep/{*sessionPath}</c> instead relay only the WHEP *signaling* exchange
/// (an SDP offer/answer, and session teardown) to the camera's own <c>nativeWebrtcUrl</c>
/// (<c>WEBRTC</c> mode) — the actual audio/video then flows directly between the viewer's browser
/// and the device over WebRTC, never through this API. None of these four routes touch the
/// database for the RTSP (MediaMTX) case, and none repeat the camera scope check: that check
/// already ran once, at mint time above, and the token's short lifetime is what stands in for
/// re-checking it on every request. The <c>HLS</c>/<c>WEBRTC</c> cases do one unscoped routing
/// lookup per request (<see cref="CameraRepository.GetStreamRoutingAsync"/>) to learn the native
/// URL and resolve its credential — deliberately not embedded in the token, since a token this
/// route hands to the browser must never carry a device's plaintext secret.
/// </para>
/// <para>
/// <c>GET /validate</c> is kept as a standalone check (the same validation the proxy route above
/// runs internally) for a deployment that prefers an external reverse proxy in front of MediaMTX
/// instead of this API's own proxy route — not required for the default, single-process path.
/// </para>
/// </remarks>
public static class StreamSessionEndpoints
{
    private static readonly Regex SameSiteNonePattern =
        new("SameSite=None", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static void MapStreamSessionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/streams")
                       .WithTags(ApiTags.Streaming);

        group.MapGet("/{cameraId:guid}/session", SessionAsync)
          .RequireAuthorization()
          .RequirePermission("camera.read")
          .WithSummary("Mint a short-lived, single-camera token for live viewing")
          .WithDescription(
              "Scoped and audited exactly like any other single-camera read — out of scope reads "
              + "as 404. On success, mints a token good for "
              + $"{JwtTokenService.StreamSessionLifetime.TotalMinutes:0} minutes, valid for "
              + "exactly this camera and nothing else on this API. The response's `mode` "
              + "(the camera's own `streamPreference`) says which route to actually use: "
              + "`RTSP`/`HLS` both mean play `GET /{cameraId}/{*hlsPath}` as HLS; `WEBRTC` means "
              + "use `POST /{cameraId}/whep` instead. Pass the token back as "
              + "`Authorization: Bearer {token}` either way.");

        // AllowAnonymous deliberately: the stream-session token carries a distinct audience, so
        // running it through the ordinary JwtBearer scheme would reject it outright before this
        // handler ever saw it. Validation happens inside the handler instead. Same for the WHEP
        // routes below.
        group.MapGet("/{cameraId:guid}/{*hlsPath}", ProxyAsync)
          .AllowAnonymous()
          .WithSummary("Proxy a live HLS playlist or segment for one camera")
          .WithDescription(
              "Validates the `Authorization: Bearer` stream-session token from `GET "
              + "/{cameraId}/session`, confirms it authorizes exactly this camera id (not a "
              + "different one substituted in the URL), then reverse-proxies the request to "
              + "MediaMTX or the camera's own native HLS URL, per the token's embedded mode. 403 "
              + "if the token is missing, invalid, expired, names a different camera, or names a "
              + "WEBRTC-mode camera (use `POST /{cameraId}/whep` instead); 502 if the upstream "
              + "cannot be reached.");

        group.MapPost("/{cameraId:guid}/whep", WhepOfferAsync)
          .AllowAnonymous()
          .WithSummary("Relay a WHEP SDP offer to a camera's native WebRTC endpoint")
          .WithDescription(
              "Same token validation as the HLS proxy above, but for a WEBRTC-mode camera: the "
              + "request body (an SDP offer, `Content-Type: application/sdp`) is POSTed straight "
              + "through to the camera's own `nativeWebrtcUrl`, with Basic auth added if the "
              + "camera has a resolvable credential. The response is the device's SDP answer, "
              + "verbatim; its `Location` header (the WHEP session resource, for teardown) is "
              + "rewritten to point at `DELETE /{cameraId}/whep/{*sessionPath}` on this API "
              + "instead of the device directly. The actual media then negotiates and flows "
              + "directly between the browser and the device over WebRTC — this route only ever "
              + "relays the signaling exchange. 403 for the same reasons as the HLS proxy, plus "
              + "a camera not in WEBRTC mode; 502 if the device cannot be reached.");

        group.MapDelete("/{cameraId:guid}/whep/{*sessionPath}", WhepTeardownAsync)
          .AllowAnonymous()
          .WithSummary("Tear down a relayed WHEP session")
          .WithDescription(
              "Relays a WHEP session's teardown DELETE to the camera's own native endpoint — the "
              + "`sessionPath` a prior `POST /{cameraId}/whep`'s rewritten `Location` named. "
              + "Best-effort: the device closing the underlying peer connection on its own idle "
              + "timeout if this never arrives is expected, not a failure this route needs to "
              + "guard against.");

        // AllowAnonymous deliberately: the caller here is either an external reverse proxy's
        // auth_request subrequest, or nothing at all in the default single-process deployment
        // (see class remarks) — not a logged-in platform user or API key.
        group.MapGet("/validate", ValidateAsync)
          .AllowAnonymous()
          .WithSummary("Validate a stream-session token, for an external reverse proxy")
          .WithDescription(
              "200 (allow) if the `Authorization: Bearer` token is a currently-valid "
              + "stream-session token minted by `GET /{cameraId}/session`; 403 (deny) otherwise. "
              + "Not needed when serving HLS through this API's own `GET /{cameraId}/{*hlsPath}` "
              + "— only relevant to a deployment fronting MediaMTX with its own reverse proxy "
              + "instead. On 200, echoes the camera id in an `X-Camera-Id` response header so "
              + "such a proxy can route the request without decoding the token itself.");
    }

    private static async Task<Results<Ok<StreamSessionResponse>, NotFound>> SessionAsync(
        Guid cameraId,
        CameraRepository cameras,
        JwtTokenService tokens,
        HttpContext http,
        CancellationToken ct)
    {
        var caller = CallerContextFactory.From(http);

        // Belt-and-suspenders behind the .RequirePermission filter.
        caller.Require("camera.read");

        // Scope is enforced by reaching the camera first. Out of scope reads as 404, so an
        // unauthorised caller cannot confirm which camera ids exist.
        var camera = await cameras.GetAsync(cameraId, caller, ct);
        if (camera is null)
        {
            return TypedResults.NotFound();
        }

        var (token, expiresAt) = tokens.IssueStreamSession(cameraId, camera.StreamPreference);
        return TypedResults.Ok(new StreamSessionResponse(cameraId, token, expiresAt, camera.StreamPreference));
    }

    private static async Task<IResult> ValidateAsync(
        JwtTokenService tokens, HttpContext http, CancellationToken ct)
    {
        var validated = await ValidateBearerAsync(tokens, http, ct);
        if (validated is null)
        {
            return TypedResults.StatusCode(StatusCodes.Status403Forbidden);
        }

        http.Response.Headers["X-Camera-Id"] = validated.CameraId.ToString();
        return TypedResults.Ok();
    }

    private static async Task<IResult> ProxyAsync(
        Guid cameraId, string? hlsPath,
        JwtTokenService tokens, IHttpClientFactory httpClientFactory,
        CameraRepository cameras, ICredentialResolver credentials,
        HttpContext http, CancellationToken ct)
    {
        var validated = await ValidateBearerAsync(tokens, http, ct);
        if (validated is null || validated.CameraId != cameraId || validated.Mode == CameraVocab.StreamPreferenceWebrtc)
        {
            // Same response for "no/invalid token", "token names a different camera", and "this
            // camera is WEBRTC-mode, use /whep instead" — a caller probing any of these learns
            // nothing from the difference.
            return TypedResults.StatusCode(StatusCodes.Status403Forbidden);
        }

        if (string.IsNullOrEmpty(hlsPath))
        {
            return TypedResults.NotFound();
        }

        if (validated.Mode == CameraVocab.StreamPreferenceHls)
        {
            return await ProxyNativeHlsAsync(cameraId, hlsPath, httpClientFactory, cameras, credentials, http, ct);
        }

        return await ProxyMediaMtxAsync(cameraId, hlsPath, httpClientFactory, http, ct);
    }

    private static async Task<IResult> ProxyMediaMtxAsync(
        Guid cameraId, string hlsPath, IHttpClientFactory httpClientFactory, HttpContext http, CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient(StreamingOptions.MediaMtxHttpClientName);

        // MediaMTX's HLS server issues a session-continuity cookie on the first request (a
        // redirect to the same URL plus a Set-Cookie), and expects it back on every request
        // after that for the same viewing session. The client above has UseCookies disabled
        // (ApiOptionsExtensions — that cookie is marked Secure, and .NET's built-in cookie jar
        // correctly refuses to resend a Secure cookie over this plain-HTTP hop to MediaMTX,
        // which silently broke every request after the first). Forwarded manually instead: the
        // browser's own Cookie header for this route (if it already has one from an earlier
        // response — see below) goes upstream unchanged; whatever MediaMTX sets on the way back
        // gets relayed to the browser with Secure/SameSite=None stripped, since the browser<->API
        // hop is not guaranteed to be HTTPS in every deployment either, and SameSite=None without
        // Secure is rejected outright by modern browsers.
        // The query string must be forwarded too — MediaMTX's own cookie-continuity handshake
        // (see the AllowAutoRedirect comment in ApiOptionsExtensions) round-trips a `cookieCheck`
        // marker through it. That used to work by accident: with auto-redirect left on the
        // default HttpClient followed MediaMTX's 302 internally, which naturally carried the
        // query string along. Now that auto-redirect is off so the 302 can reach the browser
        // instead, this proxy has to forward the query string itself or the handshake loops.
        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"cam-{cameraId}/{hlsPath}{http.Request.QueryString}");
        if (http.Request.Headers.TryGetValue("Cookie", out var incomingCookie))
        {
            request.Headers.TryAddWithoutValidation("Cookie", (string?)incomingCookie);
        }

        HttpResponseMessage upstream;
        try
        {
            upstream = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (HttpRequestException)
        {
            return TypedResults.Problem(
                title: "Streaming gateway unavailable",
                detail: "MediaMTX could not be reached. It may not be running, or this camera's "
                      + "stream path is not yet provisioned.",
                statusCode: StatusCodes.Status502BadGateway);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            // The HttpClient's own Timeout elapsed (distinct from the caller/browser aborting the
            // request, which would have set `ct` itself) — MediaMTX is up but did not answer in
            // time, e.g. it is still deciding whether an on-demand source is reachable. Without
            // this catch, HttpClient's internal timeout cancellation propagates as an unhandled
            // TaskCanceledException and becomes a 500, not a clean, explainable error.
            return TypedResults.Problem(
                title: "Streaming gateway timed out",
                detail: "MediaMTX did not respond in time. The camera's source may be slow to "
                      + "connect or unreachable.",
                statusCode: StatusCodes.Status504GatewayTimeout);
        }

        using (upstream)
        {
            RelayCommonHeaders(http, upstream);

            // MediaMTX's own cookie-continuity check (see the AllowAutoRedirect comment on the
            // MediaMtx HttpClient in ApiOptionsExtensions) 302s the *first* request for a path
            // back to itself with `?cookieCheck=1`, so the browser can pick up the Set-Cookie
            // above before re-requesting. Relayed here, but rewritten: MediaMTX's Location uses
            // its own path scheme (`/cam-{cameraId}/{hlsPath}`), which doesn't exist as a route
            // on this API — the browser must be redirected back through this proxy's own
            // `/api/v1/streams/{cameraId}/{hlsPath}` scheme instead, or the redirect 404s.
            if (upstream.Headers.Location is { } location)
            {
                var rewritten = Regex.Replace(
                    location.ToString(), $"^/cam-{Regex.Escape(cameraId.ToString())}/",
                    $"/api/v1/streams/{cameraId}/");
                http.Response.Headers.Location = rewritten;
            }

            await upstream.Content.CopyToAsync(http.Response.Body, ct);
        }

        return TypedResults.Empty;
    }

    /// <summary>
    /// The HLS-mode counterpart of <see cref="ProxyMediaMtxAsync"/> — proxies a camera's own
    /// <c>nativeHlsUrl</c> directly instead of MediaMTX. Deliberately generic rather than assuming
    /// any particular device's URL/redirect/cookie conventions (unlike the MediaMTX path, which
    /// can hard-code MediaMTX's own <c>cam-{id}</c> scheme): the very first request
    /// (<c>hlsPath == "index.m3u8"</c>, matching what <c>LiveVideoTile</c> always requests first)
    /// goes to <c>nativeHlsUrl</c> itself, whatever it's actually named on the device; every
    /// later request (a variant playlist or segment the master playlist named) resolves
    /// <c>hlsPath</c> relative to <c>nativeHlsUrl</c>'s own directory, the same way a browser
    /// would resolve a relative URL found inside that playlist.
    /// </summary>
    private static async Task<IResult> ProxyNativeHlsAsync(
        Guid cameraId, string hlsPath, IHttpClientFactory httpClientFactory,
        CameraRepository cameras, ICredentialResolver credentials, HttpContext http, CancellationToken ct)
    {
        var routing = await cameras.GetStreamRoutingAsync(cameraId, ct);
        if (routing?.NativeHlsUrl is not { } nativeHlsUrl)
        {
            return TypedResults.Problem(
                title: "No native HLS URL configured",
                detail: "This camera is set to HLS stream mode but has no nativeHlsUrl on file.",
                statusCode: StatusCodes.Status502BadGateway);
        }

        var nativeBase = new Uri(nativeHlsUrl);
        var upstreamUri = hlsPath == "index.m3u8"
            ? nativeBase
            : new Uri(nativeBase, hlsPath + http.Request.QueryString);

        var client = httpClientFactory.CreateClient(StreamingOptions.NativeStreamHttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, upstreamUri);
        if (http.Request.Headers.TryGetValue("Cookie", out var incomingCookie))
        {
            request.Headers.TryAddWithoutValidation("Cookie", (string?)incomingCookie);
        }

        if (routing.CredentialReference is { } reference)
        {
            await AddBasicAuthAsync(request.Headers, reference, cameraId, credentials, ct);
        }

        HttpResponseMessage upstream;
        try
        {
            upstream = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (HttpRequestException)
        {
            return TypedResults.Problem(
                title: "Camera's native HLS endpoint unavailable",
                detail: "The camera's own HLS URL could not be reached.",
                statusCode: StatusCodes.Status502BadGateway);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return TypedResults.Problem(
                title: "Camera's native HLS endpoint timed out",
                detail: "The camera did not respond in time.",
                statusCode: StatusCodes.Status504GatewayTimeout);
        }

        using (upstream)
        {
            RelayCommonHeaders(http, upstream);

            if (upstream.Headers.Location is { } location)
            {
                // Resolve whatever the device sent (relative or absolute) against the request
                // that produced it, then re-express it relative to nativeHlsUrl's own directory
                // so it can be mapped onto this proxy's own path scheme — the same rewrite
                // `ProxyMediaMtxAsync` does for MediaMTX's own redirect, generalized to a device
                // whose path scheme is unknown ahead of time.
                var resolved = new Uri(upstreamUri, location);
                var relative = nativeBase.MakeRelativeUri(resolved);
                http.Response.Headers.Location = $"/api/v1/streams/{cameraId}/{relative}";
            }

            await upstream.Content.CopyToAsync(http.Response.Body, ct);
        }

        return TypedResults.Empty;
    }

    private static async Task<IResult> WhepOfferAsync(
        Guid cameraId, JwtTokenService tokens, IHttpClientFactory httpClientFactory,
        CameraRepository cameras, ICredentialResolver credentials, HttpContext http, CancellationToken ct)
    {
        var validated = await ValidateBearerAsync(tokens, http, ct);
        if (validated is null || validated.CameraId != cameraId || validated.Mode != CameraVocab.StreamPreferenceWebrtc)
        {
            return TypedResults.StatusCode(StatusCodes.Status403Forbidden);
        }

        var routing = await cameras.GetStreamRoutingAsync(cameraId, ct);
        if (routing?.NativeWebrtcUrl is not { } nativeWebrtcUrl)
        {
            return TypedResults.Problem(
                title: "No native WebRTC URL configured",
                detail: "This camera is set to WEBRTC stream mode but has no nativeWebrtcUrl on file.",
                statusCode: StatusCodes.Status502BadGateway);
        }

        var nativeBase = new Uri(nativeWebrtcUrl);
        var client = httpClientFactory.CreateClient(StreamingOptions.NativeStreamHttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Post, nativeBase)
        {
            Content = new StreamContent(http.Request.Body),
        };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/sdp");

        if (routing.CredentialReference is { } reference)
        {
            await AddBasicAuthAsync(request.Headers, reference, cameraId, credentials, ct);
        }

        HttpResponseMessage upstream;
        try
        {
            upstream = await client.SendAsync(request, ct);
        }
        catch (HttpRequestException)
        {
            return TypedResults.Problem(
                title: "Camera's native WebRTC endpoint unavailable",
                detail: "The camera's own WHEP URL could not be reached.",
                statusCode: StatusCodes.Status502BadGateway);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return TypedResults.Problem(
                title: "Camera's native WebRTC endpoint timed out",
                detail: "The camera did not respond in time.",
                statusCode: StatusCodes.Status504GatewayTimeout);
        }

        using (upstream)
        {
            http.Response.StatusCode = (int)upstream.StatusCode;
            if (upstream.Content.Headers.ContentType is { } contentType)
            {
                http.Response.ContentType = contentType.ToString();
            }

            http.Response.Headers.CacheControl = "no-store";

            // The WHEP session resource the device wants a later DELETE sent to — rewritten to
            // route back through this API's own teardown route instead of the device directly,
            // exactly as the HLS proxy rewrites MediaMTX's/a native device's Location.
            if (upstream.Headers.Location is { } location)
            {
                var resolved = new Uri(nativeBase, location);
                var relative = nativeBase.MakeRelativeUri(resolved);
                http.Response.Headers.Location = $"/api/v1/streams/{cameraId}/whep/{relative}";
            }

            await upstream.Content.CopyToAsync(http.Response.Body, ct);
        }

        return TypedResults.Empty;
    }

    private static async Task<IResult> WhepTeardownAsync(
        Guid cameraId, string? sessionPath, JwtTokenService tokens, IHttpClientFactory httpClientFactory,
        CameraRepository cameras, ICredentialResolver credentials, HttpContext http, CancellationToken ct)
    {
        var validated = await ValidateBearerAsync(tokens, http, ct);
        if (validated is null || validated.CameraId != cameraId || validated.Mode != CameraVocab.StreamPreferenceWebrtc)
        {
            return TypedResults.StatusCode(StatusCodes.Status403Forbidden);
        }

        if (string.IsNullOrEmpty(sessionPath))
        {
            return TypedResults.NotFound();
        }

        var routing = await cameras.GetStreamRoutingAsync(cameraId, ct);
        if (routing?.NativeWebrtcUrl is not { } nativeWebrtcUrl)
        {
            return TypedResults.NotFound();
        }

        var upstreamUri = new Uri(new Uri(nativeWebrtcUrl), sessionPath);
        var client = httpClientFactory.CreateClient(StreamingOptions.NativeStreamHttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Delete, upstreamUri);
        if (routing.CredentialReference is { } reference)
        {
            await AddBasicAuthAsync(request.Headers, reference, cameraId, credentials, ct);
        }

        // Best-effort, per the route's own remarks — the device tearing itself down on idle
        // timeout if this never arrives is an accepted outcome, not something to retry or error
        // loudly over.
        try
        {
            using var upstream = await client.SendAsync(request, ct);
        }
        catch (HttpRequestException)
        {
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
        }

        return TypedResults.NoContent();
    }

    /// <summary>Resolves <paramref name="reference"/> and adds it to <paramref name="headers"/>
    /// as HTTP Basic auth — the convention a camera/NVR's own native HLS/WHEP endpoint speaks,
    /// distinct from this platform's own bearer-token auth. Every resolution is audited the same
    /// way any other credential resolution on this API is.</summary>
    private static async Task AddBasicAuthAsync(
        System.Net.Http.Headers.HttpRequestHeaders headers, string reference, Guid cameraId,
        ICredentialResolver credentials, CancellationToken ct)
    {
        try
        {
            var credential = await credentials.ResolveAsync(
                reference, new CredentialAccessContext("streaming-gateway", cameraId, AuditFailureIsFatal: false), ct);
            if (credential.Username is null && credential.Password is null)
            {
                return;
            }

            var raw = $"{credential.Username}:{credential.Password}";
            headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(raw)));
        }
        catch (CredentialNotProvisionedException)
        {
            // No credential sealed under this reference yet — proceed unauthenticated rather
            // than fail the whole proxy request; the device itself will 401 if it does need one.
        }
    }

    /// <summary>Status, content type, relayed cookies, and cache header — shared by both HLS
    /// proxy paths. <c>Location</c> is deliberately not included: each caller rewrites it
    /// differently (MediaMTX's own path scheme vs. a native device's).</summary>
    private static void RelayCommonHeaders(HttpContext http, HttpResponseMessage upstream)
    {
        http.Response.StatusCode = (int)upstream.StatusCode;
        if (upstream.Content.Headers.ContentType is { } contentType)
        {
            http.Response.ContentType = contentType.ToString();
        }

        // Secure and SameSite=None are stripped: this route is not guaranteed to be served over
        // HTTPS in every deployment, and a modern browser refuses a SameSite=None cookie outright
        // unless it is also Secure — sending both unmodified would make the cookie silently
        // unusable exactly where it is needed.
        if (upstream.Headers.TryGetValues("Set-Cookie", out var setCookieValues))
        {
            foreach (var cookie in setCookieValues)
            {
                var sanitized = SameSiteNonePattern.Replace(cookie, "SameSite=Lax")
                    .Replace("; Secure", "", StringComparison.OrdinalIgnoreCase);
                http.Response.Headers.Append("Set-Cookie", sanitized);
            }
        }

        // A stream token is short-lived; nothing about the response it authorizes should outlive
        // it in any cache.
        http.Response.Headers.CacheControl = "no-store";
    }

    /// <summary>
    /// Shared by <see cref="ValidateAsync"/>, <see cref="ProxyAsync"/> and the WHEP routes: what
    /// the caller's <c>Authorization: Bearer</c> stream-session token authorizes, or
    /// <see langword="null"/> if the header is missing, malformed, or the token itself does not
    /// validate.
    /// </summary>
    private static async Task<StreamSessionClaims?> ValidateBearerAsync(
        JwtTokenService tokens, HttpContext http, CancellationToken ct)
    {
        var header = http.Request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var token = header[prefix.Length..].Trim();
        return await tokens.ValidateStreamSessionAsync(token, ct);
    }
}
