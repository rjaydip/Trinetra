using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using Trinetra.Federation.Api.Auth;
using Trinetra.Federation.Api.Contracts;
using Trinetra.Federation.Api.OpenApi;
using Trinetra.Federation.Core.Model;
using Trinetra.Federation.Storage;
using Trinetra.Federation.Storage.Repositories;

namespace Trinetra.Federation.Api.Endpoints;

/// <summary>Camera health and maintenance: current status, history, overrides and work records.</summary>
public static class CameraHealthEndpoints
{
    private const int DefaultHistoryDays = 30;
    private const int MaxHistoryDays = 90;
    private const int DefaultHistoryLimit = 200;
    private const int MaxHistoryLimit = 1000;
    private const int DefaultMaintenanceLimit = 100;
    private const int MaxMaintenanceLimit = 500;

    private static readonly HashSet<string> MaintenanceTypes = new(StringComparer.Ordinal)
    {
        "PREVENTIVE", "CORRECTIVE", "INSPECTION", "INSTALLATION",
        "RELOCATION", "DECOMMISSION", "OTHER",
    };

    private static readonly HashSet<string> MaintenanceStatuses = new(StringComparer.Ordinal)
    {
        "OPEN", "IN_PROGRESS", "COMPLETED", "CANCELLED",
    };

    public static void MapCameraHealthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/cameras/{id:guid}")
            .WithTags(ApiTags.Cameras).RequireAuthorization();

        group.MapGet("/health", SnapshotAsync)
          .RequirePermission("camera.health.read")
          .WithSummary("A camera's current health")
          .WithDescription(
              "The three status axes, the last-seen and last-check times, and the most recent "
              + "recorded failure reason. Out of scope or absent → 404.");

        group.MapGet("/health/history", HistoryAsync)
          .RequirePermission("camera.health.read")
          .WithSummary("A camera's health transitions")
          .WithDescription(
              "Recorded health checks, newest first. `from`/`to` default to the last 30 days and "
              + $"may span at most {MaxHistoryDays}; `limit` defaults to {DefaultHistoryLimit}, "
              + $"clamped to {MaxHistoryLimit}.");

        group.MapPatch("/health", OverrideAsync)
          .RequirePermission("camera.update")
          .WithSummary("Manually override a camera's health")
          .WithDescription(
              "Sets `operationalStatus` and/or `connectivityStatus` and records a `MANUAL` "
              + "history row. `reason` is required. Use this to flag a camera as failed or clear "
              + "it; automated health projection is separate.");

        group.MapGet("/maintenance", ListMaintenanceAsync)
          .RequirePermission("camera.maintenance.read")
          .WithSummary("Maintenance records for a camera")
          .WithDescription(
              "Newest first. Filter with `status` (OPEN / IN_PROGRESS / COMPLETED / CANCELLED); "
              + $"`limit` defaults to {DefaultMaintenanceLimit}, clamped to {MaxMaintenanceLimit}.");

        group.MapPost("/maintenance", CreateMaintenanceAsync)
          .RequirePermission("camera.maintenance.update")
          .WithSummary("Open a maintenance record")
          .WithDescription(
              "Creates a record. If its status is OPEN or IN_PROGRESS the camera's "
              + "`maintenanceStatus` moves to `UNDER_MAINTENANCE` in the same transaction.");

        group.MapPatch("/maintenance/{recordId:guid}", UpdateMaintenanceAsync)
          .RequirePermission("camera.maintenance.update")
          .WithSummary("Progress, complete or cancel a maintenance record")
          .WithDescription(
              "Only supplied fields change. Completing or cancelling the last open record for a "
              + "camera returns it to `NORMAL`. `COMPLETED` and `CANCELLED` are terminal — a "
              + "further change is 409.");
    }

    // -------------------------------------------------------------------

    private static async Task<Results<Ok<CameraHealthResponse>, NotFound>> SnapshotAsync(
        Guid id, CameraHealthRepository repo, HttpContext http, CancellationToken ct)
    {
        var row = await repo.SnapshotAsync(id, CallerContextFactory.From(http), ct);
        return row is null
            ? TypedResults.NotFound()
            : TypedResults.Ok(new CameraHealthResponse(
                row.CameraId, row.OperationalStatus, row.ConnectivityStatus, row.MaintenanceStatus,
                row.LastSeenAt, row.LastHealthCheckAt, row.FailureReason));
    }

    private static async Task<Results<Ok<CameraHealthHistoryResponse>, NotFound, ProblemHttpResult>> HistoryAsync(
        Guid id, DateTimeOffset? from, DateTimeOffset? to, int? limit,
        CameraHealthRepository repo, HttpContext http, CancellationToken ct)
    {
        var end = to ?? DateTimeOffset.UtcNow;
        var start = from ?? end.AddDays(-DefaultHistoryDays);

        if (end <= start)
        {
            return TypedResults.Problem(
                title: "Invalid time range", detail: "'to' must be after 'from'.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (end - start > TimeSpan.FromDays(MaxHistoryDays))
        {
            return TypedResults.Problem(
                title: "Time range too wide",
                detail: $"The window may span at most {MaxHistoryDays} days.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var rows = await repo.HistoryAsync(
            id, start, end, Math.Clamp(limit ?? DefaultHistoryLimit, 1, MaxHistoryLimit),
            CallerContextFactory.From(http), ct);

        if (rows is null)
        {
            return TypedResults.NotFound();
        }

        return TypedResults.Ok(new CameraHealthHistoryResponse(
            id, start, end,
            [.. rows.Select(r => new CameraHealthCheckResponse(
                r.OperationalStatus, r.ConnectivityStatus, r.CheckedAt, r.LatencyMs,
                r.ErrorCode, r.FailureReason, r.Source))]));
    }

    private static async Task<Results<Ok<CameraHealthResponse>, NotFound, ProblemHttpResult>> OverrideAsync(
        Guid id, [FromBody] HealthOverrideRequest request, CameraHealthRepository repo,
        NpgsqlDataSource db, HttpContext http, CancellationToken ct)
    {
        var caller = CallerContextFactory.From(http);

        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            return TypedResults.Problem(
                title: "Reason required", detail: "A manual override must state a reason.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var op = Normalise(request.OperationalStatus);
        var conn = Normalise(request.ConnectivityStatus);

        if (op is null && conn is null)
        {
            return TypedResults.Problem(
                title: "Nothing to change",
                detail: "Supply operationalStatus and/or connectivityStatus.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (op is not null && !CameraStatus.Operational.Contains(op))
        {
            return Bad($"operationalStatus must be one of: {string.Join(", ", CameraStatus.Operational)}.");
        }

        if (conn is not null && !CameraStatus.Connectivity.Contains(conn))
        {
            return Bad($"connectivityStatus must be one of: {string.Join(", ", CameraStatus.Connectivity)}.");
        }

        var before = await repo.SnapshotAsync(id, caller, ct);
        if (before is null)
        {
            return TypedResults.NotFound();
        }

        await using var work = await UnitOfWork.BeginAsync(db, ct);

        if (!await repo.OverrideAsync(id, op, conn, request.Reason.Trim(), caller, work, ct))
        {
            return TypedResults.NotFound();
        }

        await work.AuditAsync(caller, "update", "camera_health", id.ToString(),
            new { before.OperationalStatus, before.ConnectivityStatus },
            new { operationalStatus = op ?? before.OperationalStatus, connectivityStatus = conn ?? before.ConnectivityStatus, reason = request.Reason.Trim() },
            organizationUnitId: null, ct);
        await work.CommitAsync(ct);

        var after = await repo.SnapshotAsync(id, caller, ct);
        return after is null
            ? TypedResults.NotFound()
            : TypedResults.Ok(new CameraHealthResponse(
                after.CameraId, after.OperationalStatus, after.ConnectivityStatus,
                after.MaintenanceStatus, after.LastSeenAt, after.LastHealthCheckAt, after.FailureReason));
    }

    // ---- Maintenance --------------------------------------------------

    private static async Task<Results<Ok<IReadOnlyList<MaintenanceRecordResponse>>, NotFound, ProblemHttpResult>> ListMaintenanceAsync(
        Guid id, string? status, int? limit,
        CameraMaintenanceRepository repo, HttpContext http, CancellationToken ct)
    {
        var filter = Normalise(status);
        if (filter is not null && !MaintenanceStatuses.Contains(filter))
        {
            return Bad($"status must be one of: {string.Join(", ", MaintenanceStatuses)}.");
        }

        var rows = await repo.ListAsync(
            id, filter, Math.Clamp(limit ?? DefaultMaintenanceLimit, 1, MaxMaintenanceLimit),
            CallerContextFactory.From(http), ct);

        return rows is null
            ? TypedResults.NotFound()
            : TypedResults.Ok<IReadOnlyList<MaintenanceRecordResponse>>([.. rows.Select(ToResponse)]);
    }

    private static async Task<Results<Created<CreatedResponse>, NotFound, ProblemHttpResult>> CreateMaintenanceAsync(
        Guid id, [FromBody] MaintenanceCreateRequest request, CameraMaintenanceRepository repo,
        NpgsqlDataSource db, HttpContext http, CancellationToken ct)
    {
        var caller = CallerContextFactory.From(http);

        var type = Normalise(request.MaintenanceType);
        var recordStatus = Normalise(request.Status) ?? "OPEN";

        if (type is null || !MaintenanceTypes.Contains(type))
        {
            return Bad($"maintenanceType must be one of: {string.Join(", ", MaintenanceTypes)}.");
        }

        if (!MaintenanceStatuses.Contains(recordStatus) || recordStatus == "COMPLETED")
        {
            return Bad("status on a new record must be OPEN, IN_PROGRESS or CANCELLED.");
        }

        if (string.IsNullOrWhiteSpace(request.Description))
        {
            return Bad("description is required.");
        }

        await using var work = await UnitOfWork.BeginAsync(db, ct);

        var recordId = await repo.CreateAsync(
            id,
            new MaintenanceCreate(
                type, recordStatus, request.Description.Trim(), request.FailureReason?.Trim(),
                request.ReportedAt, request.StartedAt, request.NextDueAt, request.PerformedBy?.Trim()),
            caller, work, ct);

        if (recordId is null)
        {
            return TypedResults.NotFound();
        }

        await work.AuditAsync(caller, "create", "maintenance_record", recordId.Value.ToString(),
            before: null, after: new { cameraId = id, type, status = recordStatus },
            organizationUnitId: null, ct);
        await work.CommitAsync(ct);

        return TypedResults.Created(
            $"/api/v1/cameras/{id}/maintenance/{recordId}", new CreatedResponse(recordId.Value));
    }

    private static async Task<Results<Ok<MaintenanceRecordResponse>, NotFound, ProblemHttpResult>> UpdateMaintenanceAsync(
        Guid id, Guid recordId, [FromBody] MaintenanceUpdateRequest request,
        CameraMaintenanceRepository repo, NpgsqlDataSource db, HttpContext http, CancellationToken ct)
    {
        var caller = CallerContextFactory.From(http);

        var newStatus = Normalise(request.Status);
        if (newStatus is not null && !MaintenanceStatuses.Contains(newStatus))
        {
            return Bad($"status must be one of: {string.Join(", ", MaintenanceStatuses)}.");
        }

        var existing = await repo.GetAsync(id, recordId, caller, ct);
        if (existing is null)
        {
            return TypedResults.NotFound();
        }

        if (existing.Status is "COMPLETED" or "CANCELLED")
        {
            return TypedResults.Problem(
                title: "Record is closed",
                detail: $"This record is {existing.Status} and cannot be changed.",
                statusCode: StatusCodes.Status409Conflict);
        }

        await using var work = await UnitOfWork.BeginAsync(db, ct);

        var updated = await repo.UpdateAsync(
            id, recordId,
            new MaintenanceUpdate
            {
                Status = newStatus,
                Description = request.Description?.Trim(),
                FailureReason = request.FailureReason?.Trim(),
                StartedAt = request.StartedAt,
                CompletedAt = request.CompletedAt,
                NextDueAt = request.NextDueAt,
                PerformedBy = request.PerformedBy?.Trim(),
            },
            caller, work, ct);

        if (updated is null)
        {
            return TypedResults.NotFound();
        }

        await work.AuditAsync(caller, "update", "maintenance_record", recordId.ToString(),
            new { existing.Status }, new { updated.Status }, organizationUnitId: null, ct);
        await work.CommitAsync(ct);

        return TypedResults.Ok(ToResponse(updated));
    }

    // -------------------------------------------------------------------

    private static MaintenanceRecordResponse ToResponse(MaintenanceRecordRow r) => new(
        r.Id, r.CameraId, r.MaintenanceType, r.Status, r.Description, r.FailureReason,
        r.ReportedAt, r.StartedAt, r.CompletedAt, r.NextDueAt, r.PerformedBy);

    private static string? Normalise(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim().ToUpperInvariant();

    private static ProblemHttpResult Bad(string detail) => TypedResults.Problem(
        title: "Invalid request", detail: detail, statusCode: StatusCodes.Status400BadRequest);
}
