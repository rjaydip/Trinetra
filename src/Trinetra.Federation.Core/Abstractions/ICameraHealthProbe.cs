namespace Trinetra.Federation.Core.Abstractions;

/// <summary>What a probe needs to reach one camera's own device directly — never a VMS API, so
/// this works identically for a standalone registry camera and one discovered through a VMS
/// target (both carry the same <c>protocol</c>/<c>ipAddress</c>/<c>port</c> fields on their
/// <c>cameras</c> row regardless of provenance).</summary>
public sealed record CameraProbeTarget(
    Guid CameraId, string Protocol, string IpAddress, int Port, string? Username, string? Password);

/// <summary>The outcome of one probe attempt, shaped to feed straight into
/// <c>camera_health_history</c> (<c>latency_ms</c>/<c>error_code</c>/<c>failure_reason</c>).</summary>
public sealed record CameraProbeResult(
    bool Reachable, double? LatencyMs, string? ErrorCode, string? FailureReason);

/// <summary>
/// Confirms a camera's own device actually answers — not just that its VMS (if any) is reachable.
/// <c>CameraHealthProbe</c> (<c>Federation.Runtime</c>) is the real implementation, built on the
/// same protocol-aware, already-tested handshake logic as the manual "test credential" action
/// (<c>CameraCredentialProbe</c>) rather than a second, independent one; this interface exists so
/// the per-camera health-check runner (<c>Federation.Worker</c>) never depends on that directly.
/// </summary>
public interface ICameraHealthProbe
{
    Task<CameraProbeResult> ProbeAsync(CameraProbeTarget target, CancellationToken cancellationToken);
}
