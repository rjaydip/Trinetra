using Trinetra.Federation.Core.Abstractions;

namespace Trinetra.Federation.Runtime;

/// <summary>
/// <see cref="ICameraHealthProbe"/> over <see cref="CameraCredentialProbe"/> — the automated
/// health-check runner (<c>Federation.Worker</c>'s <c>CameraHealthCheckRunner</c>) reuses the
/// exact same protocol-aware, already-tested handshake as the operator-triggered "test
/// credential" action, rather than a second, independent RTSP/HTTP client. The two callers differ
/// only in cadence and what they do with the result — one runs on a schedule and writes
/// <c>camera_health_history</c>, the other runs once on demand and returns straight to the caller.
/// </summary>
public sealed class CameraHealthProbe : ICameraHealthProbe
{
    public async Task<CameraProbeResult> ProbeAsync(CameraProbeTarget target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);

        var credential = new Credential(target.Username, target.Password);
        var report = await CameraCredentialProbe
            .RunAsync(target.Protocol, target.IpAddress, target.Port, credential, cancellationToken)
            .ConfigureAwait(false);

        // AuthOutcome is a finer signal than plain reachability ("credential_rejected" is
        // reachable but wrong secret, "not_verifiable" is reachable but nothing was actually
        // checked) — camera_health_history's connectivity axis only has room for
        // reachable/unreachable, so that finer detail is preserved in errorCode/failureReason
        // instead of being lost, the same way a 401-after-auth is distinguished from a bare
        // connect failure.
        var errorCode = report.AuthOutcome switch
        {
            "authenticated" or "not_verifiable" => null,
            "credential_rejected" => "AUTH_FAILED",
            "unreachable" => "UNREACHABLE",
            _ => "PROBE_ERROR",
        };

        return new CameraProbeResult(report.Reachable, report.ConnectMs, errorCode, report.Failure ?? report.Detail);
    }
}
