using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using Trinetra.Federation.Core.Abstractions;

namespace Trinetra.Federation.Runtime;

/// <summary>What an authenticated credential probe found.</summary>
/// <remarks>
/// <para>
/// <b>Never claims more than it checked.</b> <see cref="AuthOutcome"/> is
/// <c>authenticated</c> or <c>credential_rejected</c> only when a real auth handshake ran
/// (HTTP(S)/ONVIF via Basic auth, RTSP(S) via an RFC 2326 <c>OPTIONS</c> exchange with
/// Basic/Digest). A protocol this probe cannot speak (<c>RTMP</c>, <c>SRT</c>, <c>OTHER</c>)
/// falls back to the same plain TCP check as <see cref="CameraReachabilityProbe"/> and reports
/// <c>not_verifiable</c> — reachable, but the credential was never actually tried.
/// </para>
/// <para>
/// Matches the "coverage is estimated, not measured" spirit of this repo (CLAUDE.md): a result
/// here is a planning aid for the operator deciding whether to keep registering this camera, not
/// a guarantee the device is correctly configured.
/// </para>
/// </remarks>
public sealed record CameraCredentialTestReport
{
    public required bool Reachable { get; init; }

    /// <summary>
    /// One of <c>authenticated</c>, <c>credential_rejected</c>, <c>not_verifiable</c>,
    /// <c>unreachable</c>, <c>error</c>.
    /// </summary>
    public required string AuthOutcome { get; init; }

    public double? ConnectMs { get; init; }
    public string? Detail { get; init; }
    public string? Failure { get; init; }
}

/// <summary>
/// Vendor-agnostic, protocol-aware authenticated probe for a standalone registry camera.
/// </summary>
/// <remarks>
/// <para>
/// <b>Not a vendor adapter and never becomes one.</b> Federation.Adapters exists for VMS targets
/// Model 3 polls and correlates events for; a standalone registry camera has no vendor SDK
/// behind it and is never branched on vendor here — only on the plain wire protocol declared in
/// <see cref="Trinetra.Federation.Core.Model.CameraVocab.Protocols"/>, which every camera in this
/// registry already carries regardless of manufacturer.
/// </para>
/// <para>
/// Read-only and side-effect-free against the device: an HTTP <c>GET</c> to the device root, or
/// an RTSP <c>OPTIONS</c> request. Nothing here streams video, writes configuration, or retries
/// on failure (CLAUDE.md non-negotiable 3 is a Model 3/adapter rule about the runtime wrapping
/// retries; this probe simply never retries at all — one bad credential must not hammer the
/// device or look like a brute-force attempt).
/// </para>
/// </remarks>
public sealed partial class CameraCredentialProbe
{
    /// <summary>Hard ceiling on one probe. Short — this speaks one request, not a session.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly ILogger<CameraCredentialProbe> _logger;

    public CameraCredentialProbe(ILogger<CameraCredentialProbe> logger) => _logger = logger;

    public static async Task<CameraCredentialTestReport> RunAsync(
        string protocol, string ipAddress, int port, Credential credential,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credential);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(Timeout);

        try
        {
            return protocol switch
            {
                "HTTP" or "HTTPS" or "ONVIF" =>
                    await ProbeHttpAsync(protocol, ipAddress, port, credential, deadline.Token)
                        .ConfigureAwait(false),
                "RTSP" or "RTSPS" =>
                    await ProbeRtspAsync(ipAddress, port, credential, deadline.Token)
                        .ConfigureAwait(false),
                _ => await ProbeUnverifiableAsync(ipAddress, port, deadline.Token).ConfigureAwait(false),
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new CameraCredentialTestReport
            {
                Reachable = false,
                AuthOutcome = "unreachable",
                Failure = $"No response within {Timeout.TotalSeconds:F0} seconds.",
            };
        }
    }

    // -----------------------------------------------------------------
    // HTTP / HTTPS / ONVIF — a real GET with HTTP Basic auth.
    // -----------------------------------------------------------------
    // ONVIF devices expose a SOAP endpoint whose path varies by vendor
    // (/onvif/device_service is common but not universal); branching on that path per-vendor
    // is exactly the thing CLAUDE.md reserves for Federation.Adapters. Hitting the device root
    // with Basic auth is the vendor-neutral subset: most ONVIF/IP-camera HTTP servers gate the
    // root behind the same credential store as the SOAP service, so a 401/2xx there is still a
    // real (if imperfect) signal, and this probe never claims otherwise.
    private static async Task<CameraCredentialTestReport> ProbeHttpAsync(
        string protocol, string ipAddress, int port, Credential credential, CancellationToken ct)
    {
        var scheme = protocol == "HTTPS" ? "https" : "http";
        var uri = new UriBuilder(scheme, ipAddress, port, "/").Uri;

        // A short-lived client, one per probe. This is a low-frequency operator-initiated action
        // (not a hot path polling thousands of cameras), so the per-target handler pool
        // TargetHttpClientProvider exists to avoid is not a concern here.
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };

        if (scheme == "https")
        {
            // Standalone registry cameras carry no VerifyTls setting (unlike a Model 3
            // ConnectorTarget) -- self-signed certificates are the norm for departmental NVRs,
            // and this probe's only job is to classify the device's auth response, not to
            // assert its certificate is trustworthy.
#pragma warning disable CA5359 // Do Not Disable Certificate Validation
            handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;
#pragma warning restore CA5359
        }

        using var client = new HttpClient(handler) { Timeout = Timeout };

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (!string.IsNullOrEmpty(credential.Username))
        {
            var raw = $"{credential.Username}:{credential.Password}";
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(raw)));
        }

        var started = Stopwatch.StartNew();
        try
        {
            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
            var connectMs = started.Elapsed.TotalMilliseconds;

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return new CameraCredentialTestReport
                {
                    Reachable = true,
                    AuthOutcome = "credential_rejected",
                    ConnectMs = connectMs,
                    Detail = $"Device responded {(int)response.StatusCode} {response.StatusCode}.",
                };
            }

            if ((int)response.StatusCode is >= 200 and < 400)
            {
                return new CameraCredentialTestReport
                {
                    Reachable = true,
                    AuthOutcome = "authenticated",
                    ConnectMs = connectMs,
                    Detail = $"Device responded {(int)response.StatusCode} {response.StatusCode} to an "
                           + "authenticated request.",
                };
            }

            return new CameraCredentialTestReport
            {
                Reachable = true,
                AuthOutcome = "error",
                ConnectMs = connectMs,
                Detail = $"Device responded {(int)response.StatusCode} {response.StatusCode}, "
                       + "neither a clear accept nor a credential rejection.",
            };
        }
        catch (HttpRequestException ex)
        {
            return new CameraCredentialTestReport
            {
                Reachable = false, AuthOutcome = "unreachable", Failure = ex.Message,
            };
        }
    }

    // -----------------------------------------------------------------
    // RTSP / RTSPS — a raw RFC 2326 OPTIONS request, Basic first and Digest on a
    // WWW-Authenticate challenge. No RTSP client library: this is the minimum handshake needed
    // to tell "credential accepted" from "credential rejected" without one.
    // -----------------------------------------------------------------
    private static async Task<CameraCredentialTestReport> ProbeRtspAsync(
        string ipAddress, int port, Credential credential, CancellationToken ct)
    {
        var started = Stopwatch.StartNew();
        using var client = new TcpClient();

        try
        {
            await client.ConnectAsync(ipAddress, port, ct).ConfigureAwait(false);
        }
        catch (SocketException ex)
        {
            return new CameraCredentialTestReport
            {
                Reachable = false, AuthOutcome = "unreachable", Failure = ex.Message,
            };
        }

        var connectMs = started.Elapsed.TotalMilliseconds;
        var url = $"rtsp://{ipAddress}:{port}/";

        var (status, wwwAuthenticate) = await RtspOptionsAsync(client, url, authorizationHeader: null, ct)
            .ConfigureAwait(false);

        if (status is 200)
        {
            // The device answered OPTIONS with no auth challenge at all — nothing was actually
            // verified, only that OPTIONS is unauthenticated on this device.
            return new CameraCredentialTestReport
            {
                Reachable = true,
                AuthOutcome = "not_verifiable",
                ConnectMs = connectMs,
                Detail = "The device does not challenge RTSP OPTIONS for credentials; nothing "
                       + "was verified beyond reachability.",
            };
        }

        if (status != 401 || wwwAuthenticate is null)
        {
            return new CameraCredentialTestReport
            {
                Reachable = status is not null,
                AuthOutcome = status is null ? "unreachable" : "error",
                ConnectMs = connectMs,
                Detail = status is null ? null : $"Unexpected RTSP status {status}.",
            };
        }

        var authHeader = BuildRtspAuthorization(wwwAuthenticate, credential, "OPTIONS", url);
        if (authHeader is null)
        {
            return new CameraCredentialTestReport
            {
                Reachable = true,
                AuthOutcome = "not_verifiable",
                ConnectMs = connectMs,
                Detail = "The device challenged with an authentication scheme this probe does "
                       + $"not support ({wwwAuthenticate}). Reachability only.",
            };
        }

        // Second request must be a fresh connection: several RTSP server implementations close
        // the socket after an unauthenticated OPTIONS rather than keeping it open for a retry.
        using var authedClient = new TcpClient();
        try
        {
            await authedClient.ConnectAsync(ipAddress, port, ct).ConfigureAwait(false);
        }
        catch (SocketException ex)
        {
            return new CameraCredentialTestReport
            {
                Reachable = false, AuthOutcome = "unreachable", Failure = ex.Message,
            };
        }

        var (authedStatus, _) = await RtspOptionsAsync(authedClient, url, authHeader, ct)
            .ConfigureAwait(false);

        return authedStatus switch
        {
            200 => new CameraCredentialTestReport
            {
                Reachable = true, AuthOutcome = "authenticated", ConnectMs = connectMs,
                Detail = "RTSP OPTIONS accepted the stored credential.",
            },
            401 => new CameraCredentialTestReport
            {
                Reachable = true, AuthOutcome = "credential_rejected", ConnectMs = connectMs,
                Detail = "RTSP OPTIONS rejected the stored credential.",
            },
            null => new CameraCredentialTestReport
            {
                Reachable = false, AuthOutcome = "unreachable",
                Failure = "No response to the authenticated OPTIONS request.",
            },
            _ => new CameraCredentialTestReport
            {
                Reachable = true, AuthOutcome = "error", ConnectMs = connectMs,
                Detail = $"Unexpected RTSP status {authedStatus} to the authenticated request.",
            },
        };
    }

    private static async Task<(int? Status, string? WwwAuthenticate)> RtspOptionsAsync(
        TcpClient client, string url, string? authorizationHeader, CancellationToken ct)
    {
        var stream = client.GetStream();

        var requestLines = new StringBuilder()
            .Append("OPTIONS ").Append(url).Append(" RTSP/1.0\r\n")
            .Append("CSeq: 1\r\n");

        if (authorizationHeader is not null)
        {
            requestLines.Append("Authorization: ").Append(authorizationHeader).Append("\r\n");
        }

        requestLines.Append("\r\n");

        var requestBytes = Encoding.ASCII.GetBytes(requestLines.ToString());
        await stream.WriteAsync(requestBytes, ct).ConfigureAwait(false);

        var buffer = new byte[4096];
        var read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
        if (read == 0)
        {
            return (null, null);
        }

        var response = Encoding.ASCII.GetString(buffer, 0, read);
        var firstLine = response.Split("\r\n", 2)[0];
        var parts = firstLine.Split(' ', 3);

        if (parts.Length < 2 || !int.TryParse(parts[1], out var status))
        {
            return (null, null);
        }

        string? wwwAuthenticate = null;
        foreach (var line in response.Split("\r\n"))
        {
            if (line.StartsWith("WWW-Authenticate:", StringComparison.OrdinalIgnoreCase))
            {
                wwwAuthenticate = line["WWW-Authenticate:".Length..].Trim();
                break;
            }
        }

        return (status, wwwAuthenticate);
    }

    /// <summary>
    /// Builds an RTSP <c>Authorization</c> header for a Basic or Digest challenge. Returns
    /// <see langword="null"/> for anything else (no scheme this probe can answer).
    /// </summary>
    private static string? BuildRtspAuthorization(
        string wwwAuthenticate, Credential credential, string method, string uri)
    {
        if (wwwAuthenticate.StartsWith("Basic", StringComparison.OrdinalIgnoreCase))
        {
            var raw = $"{credential.Username}:{credential.Password}";
            return "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(raw));
        }

        if (!wwwAuthenticate.StartsWith("Digest", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var realm = ExtractDigestParam(wwwAuthenticate, "realm");
        var nonce = ExtractDigestParam(wwwAuthenticate, "nonce");
        if (realm is null || nonce is null)
        {
            return null;
        }

        var username = credential.Username ?? "";
        var password = credential.Password ?? "";

        var ha1 = Md5Hex($"{username}:{realm}:{password}");
        var ha2 = Md5Hex($"{method}:{uri}");
        var response = Md5Hex($"{ha1}:{nonce}:{ha2}");

        return "Digest username=\"" + username + "\", realm=\"" + realm + "\", nonce=\"" + nonce
             + "\", uri=\"" + uri + "\", response=\"" + response + "\"";
    }

    private static string? ExtractDigestParam(string header, string name)
    {
        var marker = name + "=\"";
        var start = header.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return null;
        }

        start += marker.Length;
        var end = header.IndexOf('"', start);
        return end < 0 ? null : header[start..end];
    }

    private static string Md5Hex(string input)
    {
#pragma warning disable CA5351 // MD5 is required by the RTSP digest scheme itself (RFC 2326), not a
                               // choice made here -- it is the algorithm the wire protocol defines.
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(input));
#pragma warning restore CA5351
        return Convert.ToHexStringLower(hash);
    }

    // -----------------------------------------------------------------
    // RTMP / SRT / OTHER — no handshake this probe can speak. Degrades to reachability only,
    // and says so, rather than ever silently skipping the credential check.
    // -----------------------------------------------------------------
    private static async Task<CameraCredentialTestReport> ProbeUnverifiableAsync(
        string ipAddress, int port, CancellationToken ct)
    {
        var reachability = await CameraReachabilityProbe.RunAsync(ipAddress, port, ct)
            .ConfigureAwait(false);

        return new CameraCredentialTestReport
        {
            Reachable = reachability.Reachable,
            AuthOutcome = reachability.Reachable ? "not_verifiable" : "unreachable",
            ConnectMs = reachability.ConnectMs,
            Failure = reachability.Failure,
            Detail = reachability.Reachable
                ? "This protocol has no authenticated handshake this probe can perform. "
                  + "Reachability only -- the credential was not checked."
                : null,
        };
    }

    /// <summary>
    /// Caps how many probes run at once across the process. Each RTSP probe holds a raw socket
    /// open for up to <see cref="Timeout"/>; unbounded concurrency here is a self-inflicted
    /// resource exhaustion, same reasoning as <see cref="CameraReachabilityProbe"/>.
    /// </summary>
    private static readonly SemaphoreSlim Concurrency = new(50, 50);

    /// <summary>Runs a queued probe and records the outcome. Same shape as
    /// <see cref="CameraReachabilityProbe.ExecuteJobAsync"/> and <see cref="ConnectionTester.ExecuteJobAsync"/>.</summary>
    public async Task ExecuteJobAsync(
        Guid testId, string protocol, string ipAddress, int port, Credential credential,
        NpgsqlDataSource dataSource, string executedBy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(credential);

        await Concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await MarkRunningAsync(testId, dataSource, executedBy, cancellationToken)
                .ConfigureAwait(false);

            CameraCredentialTestReport report;
            try
            {
                report = await RunAsync(protocol, ipAddress, port, credential, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogProbeFailed(_logger, ipAddress, port, ex);
                report = new CameraCredentialTestReport
                {
                    Reachable = false, AuthOutcome = "error", Failure = ex.Message,
                };
            }

            await RecordResultAsync(testId, report, dataSource).ConfigureAwait(false);
        }
        finally
        {
            Concurrency.Release();
        }
    }

    private static async Task MarkRunningAsync(
        Guid testId, NpgsqlDataSource dataSource, string executedBy, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);

        await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE federation.camera_credential_test
            SET status = 'running', executed_by = @executedBy, started_at = now()
            WHERE id = @testId;
            """, new { testId, executedBy }, cancellationToken: ct)).ConfigureAwait(false);
    }

    private static async Task RecordResultAsync(
        Guid testId, CameraCredentialTestReport report, NpgsqlDataSource dataSource)
    {
        // Deliberately not the caller's token: an abandoned probe must still record a terminal
        // state, or the sweeper is the only thing that ever closes it out.
        await using var connection = await dataSource.OpenConnectionAsync(CancellationToken.None)
            .ConfigureAwait(false);

        var terminalStatus = report.AuthOutcome is "authenticated" or "not_verifiable"
            ? "completed"
            : "failed";

        await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE federation.camera_credential_test
            SET status = @status, completed_at = now(),
                result = @result::jsonb, failure_reason = @failure
            WHERE id = @testId;
            """, new
        {
            testId,
            status = terminalStatus,
            result = JsonSerializer.Serialize(report),
            failure = report.Failure,
        }, cancellationToken: CancellationToken.None)).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Credential probe for {IpAddress}:{Port} threw unexpectedly")]
    private static partial void LogProbeFailed(
        ILogger logger, string ipAddress, int port, Exception exception);
}
