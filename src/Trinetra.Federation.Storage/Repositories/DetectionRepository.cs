using Dapper;
using Npgsql;
using Trinetra.Federation.Core.Model;

namespace Trinetra.Federation.Storage.Repositories;

/// <summary>One detection row, as read back.</summary>
public sealed record DetectionEventRow
{
    public string EventId { get; init; } = "";
    public DateTimeOffset OccurredAt { get; init; }
    public Guid TargetId { get; init; }
    public string NativeCameraId { get; init; } = "";
    public Guid? CameraId { get; init; }
    public string EventType { get; init; } = "";
    public double? Confidence { get; init; }
    public string? VehicleType { get; init; }
    public string? PlateNumberRaw { get; init; }
    public string? PlateNumberNormalized { get; init; }
    public string? SnapshotReference { get; init; }
}

/// <summary>
/// Stores detections submitted by Model 2's AI worker, and resolves them against
/// <c>federated_camera</c> for organization/site scope.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Performance", "CA1822:Mark members as static",
    Justification = "Write methods take their connection from the UnitOfWork by design.",
    Scope = "type")]
public sealed class DetectionRepository
{
    private readonly NpgsqlDataSource _dataSource;

    public DetectionRepository(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    /// <summary>
    /// Resolves a worker's <c>"{targetId}:{nativeCameraId}"</c> camera id against the camera
    /// inventory a worker discovered, for the organization/site scope to denormalize onto the
    /// detection row.
    /// </summary>
    public async Task<(Guid TargetId, string NativeCameraId, Guid? CameraId,
        Guid OrganizationUnitId, Guid? SiteId)?> ResolveCameraAsync(
        string workerCameraId, CancellationToken ct)
    {
        var separator = workerCameraId.IndexOf(':', StringComparison.Ordinal);
        if (separator <= 0 || !Guid.TryParse(workerCameraId[..separator], out var targetId))
        {
            return null;
        }

        var nativeCameraId = workerCameraId[(separator + 1)..];

        await using var c = await _dataSource.OpenConnectionAsync(ct);
        var row = await c.QuerySingleOrDefaultAsync<CameraLookupRow>(new CommandDefinition("""
            SELECT camera_id, organization_unit_id, site_id
            FROM federation.federated_camera
            WHERE target_id = @targetId AND native_camera_id = @nativeCameraId;
            """, new { targetId, nativeCameraId }, cancellationToken: ct));

        return row is null
            ? null
            : (targetId, nativeCameraId, row.CameraId, row.OrganizationUnitId, row.SiteId);
    }

    /// <summary>
    /// Inserts a detection, idempotent on <see cref="DetectionEvent.Id"/>. Returns
    /// <see langword="true"/> only when this call actually inserted a new row — a retried,
    /// already-seen POST must not trigger a second watchlist match.
    /// </summary>
    public async Task<bool> IngestAsync(
        DetectionEvent evt, Guid targetId, string nativeCameraId, Guid? cameraId,
        Guid organizationUnitId, Guid? siteId, string? plateNumberNormalized,
        CallerContext caller, UnitOfWork work, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(evt);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(work);
        caller.Require("observation.write");

        // A caller must be able to reach the camera's own organization, or a key scoped to one
        // department could ingest detections into another's — CLAUDE.md #12: organization and
        // geography scope are independent dimensions, never assumed from the permission alone.
        if (!caller.IsUnscopedFor("observation.write"))
        {
            var permitted = await work.Connection.ExecuteScalarAsync<bool>(new CommandDefinition(
                """
                SELECT federation.has_permission(
                    p_user_id => @UserId, p_api_key_id => @ApiKeyId,
                    p_permission => 'observation.write', p_organization_unit_id => @OrgUnit);
                """,
                new { caller.UserId, caller.ApiKeyId, OrgUnit = organizationUnitId },
                work.Transaction, cancellationToken: ct));

            if (!permitted)
            {
                throw new ForbiddenException("observation.write");
            }
        }

        var affected = await work.Connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO federation.detection_event
                (event_id, occurred_at, target_id, native_camera_id, camera_id,
                 organization_unit_id, site_id, event_type, confidence,
                 vehicle_type, plate_number_raw, plate_number_normalized, snapshot_reference)
            VALUES (@EventId, @OccurredAt, @TargetId, @NativeCameraId, @CameraId,
                    @OrganizationUnitId, @SiteId, @EventType, @Confidence,
                    @VehicleType, @PlateNumberRaw, @PlateNumberNormalized, @SnapshotReference)
            ON CONFLICT (event_id, occurred_at) DO NOTHING;
            """, new
        {
            EventId = evt.Id,
            OccurredAt = evt.Timestamp,
            TargetId = targetId,
            NativeCameraId = nativeCameraId,
            CameraId = cameraId,
            OrganizationUnitId = organizationUnitId,
            SiteId = siteId,
            EventType = evt.EventType,
            evt.Confidence,
            evt.VehicleType,
            PlateNumberRaw = evt.PlateNumberRaw,
            PlateNumberNormalized = plateNumberNormalized,
            SnapshotReference = evt.SnapshotReference,
        }, work.Transaction, cancellationToken: ct));

        return affected > 0;
    }

    /// <summary>Search over recent detections, scoped to the caller's organization reach.</summary>
    public async Task<IReadOnlyList<DetectionEventRow>> SearchAsync(
        string? plateNumberNormalized, Guid? targetId, DateTimeOffset from, DateTimeOffset to,
        int limit, CallerContext caller, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require("observation.read");

        var sql = $"""
            SELECT event_id, occurred_at, target_id, native_camera_id, camera_id,
                   event_type, confidence, vehicle_type, plate_number_raw,
                   plate_number_normalized, snapshot_reference
            FROM federation.detection_event
            WHERE occurred_at >= @from AND occurred_at < @to
              AND (@PlateNumberNormalized::text IS NULL
                   OR plate_number_normalized = @PlateNumberNormalized)
              AND (@TargetId::uuid IS NULL OR target_id = @TargetId)
              AND (@Unscoped OR organization_unit_id IN (
                       SELECT organization_unit_id
                       FROM federation.authorized_org_units(
                                p_user_id => @UserId, p_api_key_id => @ApiKeyId,
                                p_permission => 'observation.read')))
            ORDER BY occurred_at DESC
            LIMIT @limit;
            """;

        await using var c = await _dataSource.OpenConnectionAsync(ct);
        var rows = await c.QueryAsync<DetectionEventRow>(new CommandDefinition(sql, new
        {
            from,
            to,
            PlateNumberNormalized = plateNumberNormalized,
            TargetId = targetId,
            limit = Math.Clamp(limit, 1, 500),
            caller.UserId,
            caller.ApiKeyId,
            Unscoped = caller.IsUnscopedFor("observation.read"),
        }, cancellationToken: ct));

        return [.. rows];
    }

    private sealed record CameraLookupRow
    {
        public Guid? CameraId { get; init; }
        public Guid OrganizationUnitId { get; init; }
        public Guid? SiteId { get; init; }
    }
}
