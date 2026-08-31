using Dapper;
using Npgsql;

namespace Trinetra.Federation.Storage.Repositories;

// Query results, shaped by what the read needs rather than by a table. They live here rather
// than in the API because they are what the SQL produces; the API maps them to its own response
// contracts, so a column rename cannot silently change a published payload.

/// <summary>One health observation for a connector target.</summary>
// Init properties, not positional records. Dapper binds a positional record by matching the
// reader's column TYPES to constructor parameters exactly, so a timestamptz (which Npgsql
// surfaces as DateTime) will not bind to a DateTimeOffset parameter and the query throws at
// runtime. Property binding converts, which is what lets these keep DateTimeOffset -- required
// everywhere by CLAUDE.md, because a naive timestamp silently corrupts time-window correlation.
public sealed record ConnectorHealthRow
{
    public DateTimeOffset CheckedAt { get; init; }
    public string Status { get; init; } = "";
    public double? LatencyMs { get; init; }
    public int? CameraCount { get; init; }
    public int ConsecutiveFailures { get; init; }
    public bool CircuitOpen { get; init; }
    public string? LastError { get; init; }
    public long EventsSinceCheck { get; init; }
    public double? CursorLagSeconds { get; init; }
}

/// <summary>The stored capability matrix for a target.</summary>
public sealed record ConnectorCapabilityRow
{
    public int Supported { get; init; }
    public string AdapterVersion { get; init; } = "";
    public DateTimeOffset ProbedAt { get; init; }
    public string? NotesJson { get; init; }
}

/// <summary>A camera as the vendor reports it.</summary>
public sealed record FederatedCameraRow
{
    public string NativeCameraId { get; init; } = "";
    public Guid? CameraId { get; init; }
    public string? Name { get; init; }
    public string? VendorModel { get; init; }
    public string? Firmware { get; init; }
    public bool IsEnabled { get; init; }
    public bool? IsRecording { get; init; }
    public string Health { get; init; } = "";
    public DateTimeOffset? LastSeen { get; init; }
    public string[]? StreamReferences { get; init; }

    /// <summary>When the current status began. Independent of history retention.</summary>
    public DateTimeOffset? StatusChangedAt { get; init; }
}

/// <summary>
/// One recorded change in a camera's status.
/// </summary>
/// <remarks>
/// Previous values are null on the camera's first observation — that is "no earlier status",
/// not "the earlier status is unknown". A row exists only where something actually changed, so
/// the status at any instant is the newest row at or before it.
/// </remarks>
public sealed record CameraStatusChangeRow
{
    public DateTimeOffset ChangedAt { get; init; }
    public string? PreviousHealth { get; init; }
    public string Health { get; init; } = "";
    public bool? PreviousEnabled { get; init; }
    public bool IsEnabled { get; init; }
    public bool? PreviousRecording { get; init; }
    public bool? IsRecording { get; init; }
}

/// <summary>Estate counts, already restricted to what the caller may see.</summary>
public sealed record EstateOverviewRow
{
    public long Targets { get; init; }
    public long ActiveTargets { get; init; }
    public long QuarantinedTargets { get; init; }
    public long Cameras { get; init; }
    public long UnreachableCameras { get; init; }
}

/// <summary>
/// Read-side queries over federation state.
/// </summary>
/// <remarks>
/// These were inline in the endpoint handlers. Moving them here is not tidying: it is what keeps
/// the scope predicate and the SQL it guards in one place. A query written next to its handler
/// is a query whose <c>WHERE</c> clause the next person edits without knowing it is the
/// authorization boundary.
/// </remarks>
public sealed class FederationQueryRepository
{
    private readonly NpgsqlDataSource _dataSource;

    public FederationQueryRepository(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    /// <summary>Recent health observations for one target.</summary>
    /// <remarks>
    /// The caller must already have established that the target is in scope — every route that
    /// reaches this first loads the target through <c>ConnectorTargetRepository.GetAsync</c>,
    /// which applies the scope predicate and returns null when it does not match.
    /// </remarks>
    public async Task<IReadOnlyList<ConnectorHealthRow>> HealthAsync(
        Guid targetId, int limit, int days, CancellationToken ct)
    {
        await using var c = await _dataSource.OpenConnectionAsync(ct);

        var rows = await c.QueryAsync<ConnectorHealthRow>(new CommandDefinition("""
            SELECT checked_at, status::text AS status, latency_ms, camera_count,
                   consecutive_failures, circuit_open, last_error,
                   events_since_check, cursor_lag_seconds
            FROM federation.connector_health
            WHERE target_id = @targetId AND checked_at >= @since
            ORDER BY checked_at DESC LIMIT @limit;
            """, new
        {
            targetId,
            limit = Math.Clamp(limit, 1, 500),

            // Bounded so the planner prunes partitions. connector_health is partitioned by day;
            // without a lower bound this scans every partition retention holds, which grows
            // silently as the retention window is widened.
            since = DateTimeOffset.UtcNow.AddDays(-Math.Clamp(days, 1, 90)),
        }, cancellationToken: ct));

        return [.. rows];
    }

    /// <summary>The stored capability matrix, never a live probe.</summary>
    /// <remarks>
    /// At 80,000 cameras, letting a page render trigger probes turns one dashboard load into
    /// thousands of vendor round trips. Capabilities are written by the worker when it connects.
    /// </remarks>
    public async Task<ConnectorCapabilityRow?> CapabilitiesAsync(Guid targetId, CancellationToken ct)
    {
        await using var c = await _dataSource.OpenConnectionAsync(ct);

        return await c.QuerySingleOrDefaultAsync<ConnectorCapabilityRow>(new CommandDefinition("""
            SELECT supported, adapter_version, probed_at, notes::text AS notes_json
            FROM federation.connector_capability WHERE target_id = @targetId;
            """, new { targetId }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<FederatedCameraRow>> CamerasAsync(
        Guid targetId, CancellationToken ct)
    {
        await using var c = await _dataSource.OpenConnectionAsync(ct);

        var rows = await c.QueryAsync<FederatedCameraRow>(new CommandDefinition("""
            SELECT native_camera_id, camera_id, name, vendor_model, firmware,
                   is_enabled, is_recording, health::text AS health, last_seen,
                   stream_references, status_changed_at
            FROM federation.federated_camera
            WHERE target_id = @targetId ORDER BY native_camera_id;
            """, new { targetId }, cancellationToken: ct));

        return [.. rows];
    }

    /// <summary>
    /// One camera's status transitions, newest first, within a window.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Bounded by <paramref name="from"/> so a query cannot walk the whole retained history of a
    /// flapping camera. The bound is on the partition key, so PostgreSQL prunes to the days
    /// actually asked for rather than scanning every partition.
    /// </para>
    /// <para>
    /// The first row returned is the last transition <b>at or before</b> the window opens, not
    /// the first one inside it. Without it a timeline starting mid-window has no idea what the
    /// camera's status was when the window began — the transition that established it may be
    /// weeks old, and a chart would have to guess.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<CameraStatusChangeRow>> CameraStatusHistoryAsync(
        Guid targetId, string nativeCameraId, DateTimeOffset from, DateTimeOffset to, int limit,
        CancellationToken ct)
    {
        await using var c = await _dataSource.OpenConnectionAsync(ct);

        var rows = await c.QueryAsync<CameraStatusChangeRow>(new CommandDefinition("""
            WITH windowed AS (
                SELECT changed_at,
                       previous_health::text    AS previous_health,
                       health::text             AS health,
                       previous_enabled, is_enabled,
                       previous_recording, is_recording
                FROM federation.camera_status_history
                WHERE target_id = @targetId
                  AND native_camera_id = @nativeCameraId
                  AND changed_at >= @from AND changed_at < @to
                ORDER BY changed_at DESC
                LIMIT @limit
            ),
            opening AS (
                SELECT changed_at,
                       previous_health::text    AS previous_health,
                       health::text             AS health,
                       previous_enabled, is_enabled,
                       previous_recording, is_recording
                FROM federation.camera_status_history
                WHERE target_id = @targetId
                  AND native_camera_id = @nativeCameraId
                  AND changed_at < @from
                ORDER BY changed_at DESC
                LIMIT 1
            )
            SELECT * FROM windowed
            UNION ALL
            SELECT * FROM opening
            ORDER BY changed_at DESC;
            """, new { targetId, nativeCameraId, from, to, limit }, cancellationToken: ct));

        return [.. rows];
    }

    /// <summary>Estate counts, scoped to the caller.</summary>
    public async Task<EstateOverviewRow> OverviewAsync(CallerContext caller, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require("vms.read");

        await using var c = await _dataSource.OpenConnectionAsync(ct);

        return await c.QuerySingleAsync<EstateOverviewRow>(new CommandDefinition("""
            WITH scoped AS (
                SELECT t.id, t.state
                FROM federation.connector_target t
                WHERE @Unscoped OR t.organization_unit_id IN (
                    SELECT organization_unit_id
                    FROM federation.authorized_org_units(
                             p_user_id => @UserId, p_api_key_id => @ApiKeyId,
                             p_permission => 'vms.read'))
            )
            SELECT
                (SELECT count(*) FROM scoped) AS targets,
                (SELECT count(*) FROM scoped WHERE state = 'Active') AS active_targets,
                (SELECT count(*) FROM scoped WHERE state = 'Quarantined') AS quarantined_targets,
                (SELECT count(*) FROM federation.federated_camera fc
                  WHERE fc.target_id IN (SELECT id FROM scoped)) AS cameras,
                (SELECT count(*) FROM federation.federated_camera fc
                  WHERE fc.target_id IN (SELECT id FROM scoped)
                    AND fc.health = 'Unreachable') AS unreachable_cameras;
            """, new
        {
            caller.UserId, caller.ApiKeyId, Unscoped = caller.IsUnscopedFor("vms.read"),
        }, cancellationToken: ct));
    }
}
