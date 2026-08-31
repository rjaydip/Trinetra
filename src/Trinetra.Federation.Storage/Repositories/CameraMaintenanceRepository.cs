using Dapper;
using Npgsql;

namespace Trinetra.Federation.Storage.Repositories;

/// <summary>One maintenance record for a camera.</summary>
public sealed record MaintenanceRecordRow
{
    public Guid Id { get; init; }
    public Guid CameraId { get; init; }
    public string MaintenanceType { get; init; } = "";
    public string Status { get; init; } = "";
    public string Description { get; init; } = "";
    public string? FailureReason { get; init; }
    public DateTimeOffset ReportedAt { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public DateTimeOffset? NextDueAt { get; init; }
    public string? PerformedBy { get; init; }
}

/// <summary>The fields a new maintenance record carries.</summary>
public readonly record struct MaintenanceCreate(
    string MaintenanceType,
    string Status,
    string Description,
    string? FailureReason,
    DateTimeOffset? ReportedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? NextDueAt,
    string? PerformedBy);

/// <summary>A resolved partial update to a maintenance record.</summary>
public sealed class MaintenanceUpdate
{
    public string? Status { get; set; }
    public string? Description { get; set; }
    public string? FailureReason { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset? NextDueAt { get; set; }
    public string? PerformedBy { get; set; }
}

/// <summary>
/// The maintenance lifecycle for cameras. Opening or completing a record drives the camera's
/// <c>maintenance_status</c>, in the same transaction.
/// </summary>
/// <remarks>
/// Named to disambiguate from the scheduler's <c>MaintenanceRepository</c>. All access is
/// camera-scoped on both dimensions with the maintenance permissions.
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Performance", "CA1822:Mark members as static",
    Justification = "Write methods take their connection from the UnitOfWork by design.",
    Scope = "type")]
public sealed class CameraMaintenanceRepository
{
    private static readonly HashSet<string> OpenStatuses =
        new(StringComparer.Ordinal) { "OPEN", "IN_PROGRESS" };

    private readonly NpgsqlDataSource _dataSource;

    public CameraMaintenanceRepository(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    /// <summary>Records for a camera, newest first. Null when the camera is unreachable.</summary>
    public async Task<IReadOnlyList<MaintenanceRecordRow>?> ListAsync(
        Guid cameraId, string? statusFilter, int limit, CallerContext caller, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require("camera.maintenance.read");

        await using var c = await _dataSource.OpenConnectionAsync(ct);

        if (!await ReachableAsync(c, null, cameraId, caller, "camera.maintenance.read", ct))
        {
            return null;
        }

        var args = CameraScope.Args(cameraId, caller, "camera.maintenance.read");
        args.Add("StatusFilter", statusFilter);
        args.Add("Limit", limit);

        var rows = await c.QueryAsync<MaintenanceRecordRow>(new CommandDefinition("""
            SELECT id, camera_id, maintenance_type, status, description, failure_reason,
                   reported_at, started_at, completed_at, next_due_at, performed_by
            FROM federation.maintenance_records
            WHERE camera_id = @id
              AND (@StatusFilter::text IS NULL OR status = @StatusFilter)
            ORDER BY reported_at DESC
            LIMIT @Limit;
            """, args, cancellationToken: ct));

        return [.. rows];
    }

    public async Task<MaintenanceRecordRow?> GetAsync(
        Guid cameraId, Guid recordId, CallerContext caller, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require("camera.maintenance.read");

        await using var c = await _dataSource.OpenConnectionAsync(ct);

        if (!await ReachableAsync(c, null, cameraId, caller, "camera.maintenance.read", ct))
        {
            return null;
        }

        return await c.QuerySingleOrDefaultAsync<MaintenanceRecordRow>(new CommandDefinition("""
            SELECT id, camera_id, maintenance_type, status, description, failure_reason,
                   reported_at, started_at, completed_at, next_due_at, performed_by
            FROM federation.maintenance_records
            WHERE id = @recordId AND camera_id = @cameraId;
            """, new { recordId, cameraId }, cancellationToken: ct));
    }

    /// <summary>Opens a maintenance record. Null when the camera is unreachable.</summary>
    public async Task<Guid?> CreateAsync(
        Guid cameraId, MaintenanceCreate record, CallerContext caller, UnitOfWork work, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(work);
        caller.Require("camera.maintenance.update");

        if (!await ReachableAsync(work.Connection, work.Transaction, cameraId, caller,
                "camera.maintenance.update", ct))
        {
            return null;
        }

        var id = await work.Connection.ExecuteScalarAsync<Guid>(new CommandDefinition("""
            INSERT INTO federation.maintenance_records
                (camera_id, maintenance_type, status, description, failure_reason,
                 reported_at, started_at, next_due_at, performed_by, created_by, updated_by)
            VALUES (@cameraId, @MaintenanceType, @Status, @Description, @FailureReason,
                    COALESCE(@ReportedAt, now()), @StartedAt, @NextDueAt, @PerformedBy,
                    @ActorId, @ActorId)
            RETURNING id;
            """, new
        {
            cameraId, record.MaintenanceType, record.Status, record.Description,
            record.FailureReason, record.ReportedAt, record.StartedAt, record.NextDueAt,
            record.PerformedBy, ActorId = caller.UserId,
        }, work.Transaction, cancellationToken: ct));

        if (OpenStatuses.Contains(record.Status))
        {
            await SetCameraMaintenanceAsync(work, cameraId, "UNDER_MAINTENANCE", caller, ct);
        }

        return id;
    }

    /// <summary>
    /// Progresses, completes or cancels a record. Completing or cancelling the last open record
    /// returns the camera to <c>NORMAL</c>. Returns the updated row, or null if unreachable.
    /// </summary>
    public async Task<MaintenanceRecordRow?> UpdateAsync(
        Guid cameraId, Guid recordId, MaintenanceUpdate update,
        CallerContext caller, UnitOfWork work, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(update);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(work);
        caller.Require("camera.maintenance.update");

        if (!await ReachableAsync(work.Connection, work.Transaction, cameraId, caller,
                "camera.maintenance.update", ct))
        {
            return null;
        }

        var completing = string.Equals(update.Status, "COMPLETED", StringComparison.Ordinal);

        var updated = await work.Connection.QuerySingleOrDefaultAsync<MaintenanceRecordRow>(
            new CommandDefinition("""
            UPDATE federation.maintenance_records
            SET status        = COALESCE(@Status, status),
                description   = COALESCE(@Description, description),
                failure_reason = COALESCE(@FailureReason, failure_reason),
                started_at    = COALESCE(@StartedAt, started_at),
                completed_at  = CASE WHEN @Completing THEN COALESCE(@CompletedAt, now())
                                     ELSE completed_at END,
                next_due_at   = COALESCE(@NextDueAt, next_due_at),
                performed_by  = COALESCE(@PerformedBy, performed_by),
                updated_by = @ActorId, updated_at = now()
            WHERE id = @recordId AND camera_id = @cameraId
              AND status NOT IN ('COMPLETED', 'CANCELLED')
            RETURNING id, camera_id, maintenance_type, status, description, failure_reason,
                      reported_at, started_at, completed_at, next_due_at, performed_by;
            """, new
        {
            recordId, cameraId, update.Status, update.Description, update.FailureReason,
            update.StartedAt, update.CompletedAt, update.NextDueAt, update.PerformedBy,
            Completing = completing, ActorId = caller.UserId,
        }, work.Transaction, cancellationToken: ct));

        if (updated is null)
        {
            return null;
        }

        // If no record is still open for this camera, it is back to normal.
        var stillOpen = await work.Connection.ExecuteScalarAsync<bool>(new CommandDefinition("""
            SELECT EXISTS (SELECT 1 FROM federation.maintenance_records
                           WHERE camera_id = @cameraId AND status IN ('OPEN', 'IN_PROGRESS'));
            """, new { cameraId }, work.Transaction, cancellationToken: ct));

        if (OpenStatuses.Contains(updated.Status))
        {
            await SetCameraMaintenanceAsync(work, cameraId, "UNDER_MAINTENANCE", caller, ct);
        }
        else if (!stillOpen)
        {
            await SetCameraMaintenanceAsync(work, cameraId, "NORMAL", caller, ct);
        }

        return updated;
    }

    private static async Task SetCameraMaintenanceAsync(
        UnitOfWork work, Guid cameraId, string status, CallerContext caller, CancellationToken ct)
    {
        // Never touches a retired camera.
        await work.Connection.ExecuteAsync(new CommandDefinition("""
            UPDATE federation.cameras
            SET maintenance_status = @status, updated_by = @ActorId, updated_at = now()
            WHERE id = @cameraId AND deleted_at IS NULL AND maintenance_status <> @status;
            """, new { cameraId, status, ActorId = caller.UserId },
            work.Transaction, cancellationToken: ct));
    }

    private static async Task<bool> ReachableAsync(
        NpgsqlConnection c, NpgsqlTransaction? tx, Guid cameraId,
        CallerContext caller, string permission, CancellationToken ct) =>
        await c.ExecuteScalarAsync<bool>(new CommandDefinition($"""
            SELECT EXISTS (SELECT 1 FROM federation.cameras c
                           WHERE c.id = @id AND ({CameraScope.PredicateForC}));
            """, CameraScope.Args(cameraId, caller, permission), tx, cancellationToken: ct));
}
