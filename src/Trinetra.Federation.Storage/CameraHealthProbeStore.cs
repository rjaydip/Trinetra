using Dapper;
using Npgsql;
using Trinetra.Federation.Core.Model;

namespace Trinetra.Federation.Storage;

/// <summary>One camera eligible for an automated health probe.</summary>
public sealed record CameraProbeCandidate(
    Guid CameraId, string Protocol, string IpAddress, int Port, string? CredentialReference);

/// <summary>
/// Backs the per-camera automated health check (<c>CameraHealthCheckRunner</c>,
/// <c>Federation.Worker</c>). Not a scoped repository — a system-internal reader/writer with no
/// caller, the same shape as <c>SqlCorrelationEngine</c> and <c>EventStore</c>: it runs on its own
/// schedule, in-process, never in response to an API request, so there is no
/// <see cref="CallerContext"/> to require. The caller-facing read side
/// (<c>CameraHealthRepository</c>) still takes one, as every scoped repository method must.
/// </summary>
/// <remarks>
/// Works identically for a standalone registry camera and one discovered through a VMS target —
/// both resolve to the same <c>stream_reference</c>/<c>credential_reference</c> shape on the
/// <c>cameras</c> row, so there is nothing here that branches on provenance.
/// </remarks>
public sealed class CameraHealthProbeStore
{
    private readonly NpgsqlDataSource _dataSource;

    public CameraHealthProbeStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    /// <summary>
    /// The next <paramref name="batchSize"/> cameras due a check, oldest-checked-first (never
    /// checked sorts first via <c>NULLS FIRST</c>). This ordering alone is what staggers checks
    /// across the whole fleet over time — a small batch every tick keeps rotating through
    /// whichever cameras have gone longest without a check, rather than the same subset winning
    /// every time — so no separate "due" cutoff is required beyond skipping a camera checked more
    /// recently than <paramref name="checkInterval"/> (otherwise a small fleet would get
    /// re-probed far faster than the configured interval, wasting the very concurrency budget
    /// meant to spread load across a large one).
    /// </summary>
    public async Task<IReadOnlyList<CameraProbeCandidate>> ListDueAsync(
        int batchSize, TimeSpan checkInterval, CancellationToken ct)
    {
        await using var c = await _dataSource.OpenConnectionAsync(ct);

        // protocol/ip_address/port only, the same fields the manual "test credential" action
        // requires (CameraCredentialTestEndpoints) — not stream_reference, which is often a bare
        // display string rather than something these probes can dial directly.
        var rows = await c.QueryAsync<CameraProbeCandidate>(new CommandDefinition("""
            SELECT id AS CameraId, protocol AS Protocol, host(ip_address) AS IpAddress, port AS Port,
                   credential_reference AS CredentialReference
            FROM federation.cameras
            WHERE deleted_at IS NULL
              AND protocol IS NOT NULL AND ip_address IS NOT NULL AND port IS NOT NULL
              AND (last_health_check_at IS NULL OR last_health_check_at < now() - @Interval)
            ORDER BY last_health_check_at ASC NULLS FIRST
            LIMIT @BatchSize;
            """, new { Interval = checkInterval, BatchSize = batchSize }, cancellationToken: ct));

        return [.. rows];
    }

    /// <summary>Projects a probe result onto the camera row and appends one
    /// <c>camera_health_history</c> row with <c>source = 'PROBE'</c>. Unconditional — a probe
    /// result is authoritative for connectivity regardless of whatever a MANUAL override last
    /// set, the same way the next automated check on a VMS target supersedes its own last
    /// reading.</summary>
    public async Task RecordAsync(
        Guid cameraId, bool reachable, double? latencyMs, string? errorCode, string? failureReason,
        CancellationToken ct)
    {
        var connectivity = reachable ? CameraStatus.ConnectivityConnected : CameraStatus.ConnectivityDisconnected;
        // OFFLINE only after a real failure to reach the stream; a successful probe always means
        // ONLINE. DEGRADED is left to whatever *else* sets it (e.g. the ai-worker's own detection
        // activity later) — a probe alone cannot distinguish "reachable but degraded" from
        // "reachable and fine".
        var operational = reachable ? CameraStatus.OperationalOnline : CameraStatus.OperationalOffline;

        await using var c = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);

        await c.ExecuteAsync(new CommandDefinition("""
            UPDATE federation.cameras
            SET operational_status = @Operational,
                connectivity_status = @Connectivity,
                last_health_check_at = now(),
                last_seen_at = CASE WHEN @Reachable THEN now() ELSE last_seen_at END
            WHERE id = @CameraId AND deleted_at IS NULL;
            """, new { CameraId = cameraId, Operational = operational, Connectivity = connectivity, Reachable = reachable },
            tx, cancellationToken: ct));

        await c.ExecuteAsync(new CommandDefinition("""
            INSERT INTO federation.camera_health_history
                (camera_id, operational_status, connectivity_status, latency_ms,
                 error_code, failure_reason, source)
            VALUES (@CameraId, @Operational, @Connectivity, @LatencyMs, @ErrorCode, @FailureReason, 'PROBE');
            """, new
        {
            CameraId = cameraId, Operational = operational, Connectivity = connectivity,
            LatencyMs = latencyMs.HasValue ? (int)Math.Round(latencyMs.Value) : (int?)null,
            ErrorCode = errorCode, FailureReason = failureReason,
        }, tx, cancellationToken: ct));

        await tx.CommitAsync(ct);
    }
}
