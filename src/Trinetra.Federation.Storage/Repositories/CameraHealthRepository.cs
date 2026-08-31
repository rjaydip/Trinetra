using Dapper;
using Npgsql;

namespace Trinetra.Federation.Storage.Repositories;

/// <summary>The current health of a camera, as the snapshot endpoint returns it.</summary>
public sealed record CameraHealthRow
{
    public Guid CameraId { get; init; }
    public string OperationalStatus { get; init; } = "";
    public string ConnectivityStatus { get; init; } = "";
    public string MaintenanceStatus { get; init; } = "";
    public DateTimeOffset? LastSeenAt { get; init; }
    public DateTimeOffset? LastHealthCheckAt { get; init; }
    public string? FailureReason { get; init; }
}

/// <summary>One recorded health check.</summary>
public sealed record CameraHealthCheckRow
{
    public string OperationalStatus { get; init; } = "";
    public string ConnectivityStatus { get; init; } = "";
    public DateTimeOffset CheckedAt { get; init; }
    public int? LatencyMs { get; init; }
    public string? ErrorCode { get; init; }
    public string? FailureReason { get; init; }
    public string Source { get; init; } = "";
}

/// <summary>
/// Camera health: the current snapshot, the transition history, and manual operator overrides.
/// </summary>
/// <remarks>
/// The registry does not poll. Health is projected here from an operator override now, and from
/// a bus consumer later. Every read and write is camera-scoped on both dimensions.
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Performance", "CA1822:Mark members as static",
    Justification = "Write methods take their connection from the UnitOfWork by design.",
    Scope = "type")]
public sealed class CameraHealthRepository
{
    private readonly NpgsqlDataSource _dataSource;

    public CameraHealthRepository(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<CameraHealthRow?> SnapshotAsync(Guid id, CallerContext caller, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require("camera.health.read");

        await using var c = await _dataSource.OpenConnectionAsync(ct);

        return await c.QuerySingleOrDefaultAsync<CameraHealthRow>(new CommandDefinition($"""
            SELECT c.id AS camera_id, c.operational_status, c.connectivity_status,
                   c.maintenance_status, c.last_seen_at, c.last_health_check_at,
                   (SELECT h.failure_reason FROM federation.camera_health_history h
                    WHERE h.camera_id = c.id AND h.failure_reason IS NOT NULL
                    ORDER BY h.checked_at DESC LIMIT 1) AS failure_reason
            FROM federation.cameras c
            WHERE c.id = @id AND ({CameraScope.PredicateForC});
            """, CameraScope.Args(id, caller, "camera.health.read"), cancellationToken: ct));
    }

    public async Task<IReadOnlyList<CameraHealthCheckRow>?> HistoryAsync(
        Guid id, DateTimeOffset from, DateTimeOffset to, int limit,
        CallerContext caller, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require("camera.health.read");

        await using var c = await _dataSource.OpenConnectionAsync(ct);

        var reachable = await c.ExecuteScalarAsync<bool>(new CommandDefinition($"""
            SELECT EXISTS (SELECT 1 FROM federation.cameras c
                           WHERE c.id = @id AND ({CameraScope.PredicateForC}));
            """, CameraScope.Args(id, caller, "camera.health.read"), cancellationToken: ct));

        if (!reachable)
        {
            return null;
        }

        var args = CameraScope.Args(id, caller, "camera.health.read");
        args.Add("From", from);
        args.Add("To", to);
        args.Add("Limit", limit);

        var rows = await c.QueryAsync<CameraHealthCheckRow>(new CommandDefinition("""
            SELECT operational_status, connectivity_status, checked_at, latency_ms,
                   error_code, failure_reason, source
            FROM federation.camera_health_history
            WHERE camera_id = @id AND checked_at >= @From AND checked_at < @To
            ORDER BY checked_at DESC
            LIMIT @Limit;
            """, args, cancellationToken: ct));

        return [.. rows];
    }

    /// <summary>
    /// Applies a manual health override: updates the camera's current status and records a
    /// <c>MANUAL</c> history row, both in the given transaction. Returns false if unreachable.
    /// </summary>
    public async Task<bool> OverrideAsync(
        Guid id, string? operationalStatus, string? connectivityStatus, string reason,
        CallerContext caller, UnitOfWork work, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(work);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        caller.Require("camera.update");

        var args = CameraScope.Args(id, caller, "camera.update");
        args.Add("Op", operationalStatus);
        args.Add("Conn", connectivityStatus);
        args.Add("Reason", reason);
        args.Add("ActorId", caller.UserId);

        var row = await work.Connection.QuerySingleOrDefaultAsync<StatusPair>(
            new CommandDefinition($"""
            UPDATE federation.cameras c
            SET operational_status = COALESCE(@Op, c.operational_status),
                connectivity_status = COALESCE(@Conn, c.connectivity_status),
                last_health_check_at = now(),
                updated_by = @ActorId, updated_at = now()
            WHERE c.id = @id AND c.deleted_at IS NULL AND ({CameraScope.PredicateForC})
            RETURNING c.operational_status AS op, c.connectivity_status AS conn;
            """, args, work.Transaction, cancellationToken: ct));

        if (row is null)
        {
            return false;
        }

        await work.Connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO federation.camera_health_history
                (camera_id, operational_status, connectivity_status, failure_reason,
                 source, recorded_by)
            VALUES (@id, @Op, @Conn, @Reason, 'MANUAL', @ActorId);
            """, new
        {
            id, Op = row.Op, Conn = row.Conn, Reason = reason, ActorId = caller.UserId,
        }, work.Transaction, cancellationToken: ct));

        return true;
    }

    private sealed record StatusPair
    {
        public string Op { get; init; } = "";
        public string Conn { get; init; } = "";
    }
}
