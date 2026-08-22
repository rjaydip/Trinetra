using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using Trinetra.Federation.Core.Model;

namespace Trinetra.Federation.Adapters.Http;

/// <summary>
/// Supplies <see cref="HttpClient"/> instances to adapters, grouped by TLS policy.
/// </summary>
/// <remarks>
/// <para>
/// <b>A small fixed set of handlers, never one per target.</b> Connection pooling in
/// <see cref="SocketsHttpHandler"/> is keyed by <c>(scheme, host, port)</c> and the
/// target-to-host mapping is 1:1, so targets sharing a handler never share a connection pool
/// entry. Sharing costs nothing and avoids 200 sets of idle timers per worker process.
/// </para>
/// <para>
/// The reason policy <i>classes</i> exist rather than per-target callbacks:
/// <see cref="SslClientAuthenticationOptions.RemoteCertificateValidationCallback"/> receives the
/// <see cref="SslStream"/> as its sender, <b>not</b> the request — so there is no way to ask
/// "which target is this?" from inside the callback. Grouping by policy sidesteps that entirely
/// for the boolean <see cref="ConnectorTarget.VerifyTls"/> case.
/// </para>
/// <para>
/// <c>IHttpClientFactory</c> is deliberately not used. Its headline feature is handler
/// rotation to defeat DNS staleness, which <see cref="SocketsHttpHandler.PooledConnectionLifetime"/>
/// supersedes — the standard guidance when combining them is to disable handler rotation anyway.
/// Two long-lived handlers owned by this singleton is less machinery for the same result.
/// </para>
/// </remarks>
public sealed partial class TargetHttpClientProvider : IDisposable
{
    private readonly HttpClient _strict;
    private readonly HttpClient _lax;
    private readonly ILogger<TargetHttpClientProvider> _logger;
    private bool _disposed;

    public TargetHttpClientProvider(
        DigestAuthHandler digestForStrict,
        DigestAuthHandler digestForLax,
        ILogger<TargetHttpClientProvider> logger)
    {
        _logger = logger;
        _strict = BuildClient(CreateHandler(verifyTls: true, logger), digestForStrict);
        _lax = BuildClient(CreateHandler(verifyTls: false, logger), digestForLax);
    }

    /// <summary>
    /// The client for this target's TLS policy.
    /// </summary>
    /// <remarks>
    /// Requests must carry absolute URIs, since one client serves many targets. Credentials
    /// travel per-request via <see cref="DigestAuthHandler.CredentialOption"/>, not on the
    /// handler — <c>HttpClientHandler.Credentials</c> is handler-scoped and would force the
    /// per-target handler topology this class exists to avoid.
    /// </remarks>
    public HttpClient For(ConnectorTarget target) => target.VerifyTls ? _strict : _lax;

    private static SocketsHttpHandler CreateHandler(bool verifyTls, ILogger logger)
    {
        var handler = new SocketsHttpHandler
        {
            // Bounds DNS and TLS-session staleness for pooled connections.
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),

            // The single most important value for cheap NVRs. They drop idle keep-alive
            // connections aggressively (often 15-60s). If our idle timeout exceeds theirs we
            // routinely write onto a socket the device already closed, producing spurious
            // TransientVmsExceptions that pollute the circuit breaker with phantom failures.
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(20),

            // Per (host, port), so each target gets its own budget: MaxConcurrentRequests plus
            // headroom for a held event stream and an in-flight probe.
            MaxConnectionsPerServer = 6,

            ConnectTimeout = TimeSpan.FromSeconds(5),

            // Cheap devices advertise gzip and then send malformed bodies. Opt in per vendor
            // only once proven on real firmware.
            AutomaticDecompression = DecompressionMethods.None,

            // Digest is handled by DigestAuthHandler, which needs to see the 401 itself.
            AllowAutoRedirect = false,
        };

        if (!verifyTls)
        {
            // CA5359 flags accept-any-certificate, and is right to. Justification for the
            // suppression: this handler is reached only by targets whose operator explicitly
            // set VerifyTls=false, a per-target recorded decision made because departmental
            // NVRs routinely ship self-signed certificates and cannot be re-issued. Targets
            // that did not opt out use the strict handler, which validates normally. The
            // callback records subject and thumbprint on every deviation so the trust decision
            // is auditable rather than invisible. Certificate pinning (SslOptions per host via
            // ConnectCallback) is the planned upgrade path for targets that need it.
#pragma warning disable CA5359 // Do Not Disable Certificate Validation
            handler.SslOptions.RemoteCertificateValidationCallback =
                (_, certificate, _, errors) =>
                {
                    // An audited opt-out: the operator set VerifyTls=false for this target, but
                    // the thumbprint is still recorded so the trust decision leaves a trail.
                    if (errors != SslPolicyErrors.None && certificate is X509Certificate2 cert)
                    {
                        LogAcceptedUntrustedCertificate(logger, cert.Subject, cert.Thumbprint, errors);
                    }

                    return true;
                };
#pragma warning restore CA5359
        }

        return handler;
    }

    private static HttpClient BuildClient(SocketsHttpHandler transport, DigestAuthHandler digest)
    {
        digest.InnerHandler = transport;

        return new HttpClient(digest, disposeHandler: true)
        {
            // Deliberate. HttpClient.Timeout does not apply to reads on an unbuffered response
            // stream, so it is only ever half a mechanism — and long-lived event streams need
            // no total deadline at all. Making the runtime's CancellationToken the sole
            // deadline authority keeps cancellation policy uniform and observable in one place,
            // and makes every cancellation an unambiguous OperationCanceledException.
            Timeout = Timeout.InfiniteTimeSpan,

            // Some NVRs choke on ALPN/upgrade probing. Adapters for well-behaved enterprise VMS
            // may opt into negotiation per request.
            DefaultRequestVersion = HttpVersion.Version11,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _strict.Dispose();
        _lax.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Accepted untrusted TLS certificate (target has VerifyTls=false). "
                + "Subject={Subject} Thumbprint={Thumbprint} Errors={Errors}")]
    private static partial void LogAcceptedUntrustedCertificate(
        ILogger logger, string subject, string thumbprint, SslPolicyErrors errors);
}
