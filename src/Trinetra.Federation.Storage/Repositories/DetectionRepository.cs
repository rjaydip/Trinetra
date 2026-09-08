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
/// <c>federated_camera</c> for organization/geography scope.
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
    /// inventory a worker discovered, for the organization/geography scope to denormalize onto the
    /// detection row.
    /// </summary>
    public async Task<(Guid TargetId, string NativeCameraId, Guid? CameraId,
        Guid OrganizationUnitId, Guid? GeographicAreaId)?> ResolveCameraAsync(
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
            SELECT camera_id, organization_unit_id, geographic_area_id
            FROM federation.federated_camera
            WHERE target_id = @targetId AND native_camera_id = @nativeCameraId;
            """, new { targetId, nativeCameraId }, cancellationToken: ct));

        return row is null
            ? null
            : (targetId, nativeCameraId, row.CameraId, row.OrganizationUnitId, row.GeographicAreaId);
    }

    /// <summary>
    /// Inserts a detection, idempotent on <see cref="DetectionEvent.Id"/>. Returns
    /// <see langword="true"/> only when this call actually inserted a new row — a retried,
    /// already-seen POST must not trigger a second watchlist match.
    /// </summary>
    public async Task<bool> IngestAsync(
        DetectionEvent evt, Guid targetId, string nativeCameraId, Guid? cameraId,
        Guid organizationUnitId, Guid? geographicAreaId, string? plateNumberNormalized,
        CallerContext caller, UnitOfWork work, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(evt);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(work);
        caller.Require("observation.write");

        // A caller must be able to reach the camera's own organization AND the geography of its
        // area — a key scoped to one department, or one district, must not be able to ingest
        // detections into another's. CLAUDE.md #12: the two dimensions are independent, ANDed,
        // and each is skipped only by its own unscoped flag. A camera with no area has no
        // geography to check.
        var orgOk = caller.IsUnscopedFor("observation.write");
        var geoOk = geographicAreaId is null || caller.IsUnscopedForGeography("observation.write");

        if (!orgOk || !geoOk)
        {
            // Each dimension checked against its OWN authorized_* set, never has_permission():
            // that function requires every dimension a group constrains to be supplied, so a
            // group scoped on BOTH organization and geography would fail an org-only call
            // simply because no geography was passed — a false denial, not a real one.
            var permitted = await work.Connection.ExecuteScalarAsync<bool>(new CommandDefinition(
                """
                SELECT
                    (@OrgOk OR EXISTS (
                        SELECT 1 FROM federation.authorized_org_units(
                            p_user_id => @UserId, p_api_key_id => @ApiKeyId,
                            p_permission => 'observation.write')
                        WHERE organization_unit_id = @OrgUnit))
                    AND
                    (@GeoOk OR @GeographicAreaId::uuid IS NULL OR EXISTS (
                        SELECT 1 FROM federation.authorized_geographic_areas(
                            p_user_id => @UserId, p_api_key_id => @ApiKeyId,
                            p_permission => 'observation.write')
                        WHERE geographic_area_id = @GeographicAreaId));
                """,
                new
                {
                    caller.UserId, caller.ApiKeyId,
                    OrgOk = orgOk, GeoOk = geoOk,
                    OrgUnit = organizationUnitId, GeographicAreaId = geographicAreaId,
                },
                work.Transaction, cancellationToken: ct));

            if (!permitted)
            {
                throw new ForbiddenException("observation.write");
            }
        }

        var affected = await work.Connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO federation.detection_event
                (event_id, occurred_at, target_id, native_camera_id, camera_id,
                 organization_unit_id, geographic_area_id, event_type, confidence,
                 vehicle_type, plate_number_raw, plate_number_normalized, snapshot_reference)
            VALUES (@EventId, @OccurredAt, @TargetId, @NativeCameraId, @CameraId,
                    @OrganizationUnitId, @GeographicAreaId, @EventType, @Confidence,
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
            GeographicAreaId = geographicAreaId,
            EventType = evt.EventType,
            evt.Confidence,
            evt.VehicleType,
            PlateNumberRaw = evt.PlateNumberRaw,
            PlateNumberNormalized = plateNumberNormalized,
            SnapshotReference = evt.SnapshotReference,
        }, work.Transaction, cancellationToken: ct));

        return affected > 0;
    }

    /// <summary>
    /// Search over recent detections, scoped to the caller's organization AND geography reach
    /// (invariant 12) — each dimension bypassed only by its own unscoped flag. A detection whose
    /// camera has no area is not geo-constrained.
    /// </summary>
    /// <remarks>
    /// The caller's reachable org units and geographic areas are resolved to arrays before the
    /// query, matching <see cref="EventQueryRepository"/>: the planner cannot see inside the
    /// <c>authorized_*</c> set-returning functions, so a join against them scans and discards
    /// out-of-scope rows, while an <c>= ANY(array)</c> lets it use the ordinary indexes.
    /// </remarks>
    public async Task<IReadOnlyList<DetectionEventRow>> SearchAsync(
        string? plateNumberNormalized, Guid? targetId, DateTimeOffset from, DateTimeOffset to,
        int limit, CallerContext caller, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require("observation.read");

        var unscopedOrg = caller.IsUnscopedFor("observation.read");
        var unscopedGeo = caller.IsUnscopedForGeography("observation.read");

        await using var c = await _dataSource.OpenConnectionAsync(ct);

        Guid[] units = unscopedOrg ? [] : await AuthorizedOrgUnitsAsync(c, caller, ct);
        Guid[] areas = unscopedGeo ? [] : await AuthorizedAreasAsync(c, caller, ct);

        if ((!unscopedOrg && units.Length == 0) || (!unscopedGeo && areas.Length == 0))
        {
            // Reaches nothing on one dimension. `= ANY(empty)` is valid SQL that matches nothing
            // but still costs a scan, so short-circuit.
            return [];
        }

        var sql = $"""
            SELECT d.event_id, d.occurred_at, d.target_id, d.native_camera_id, d.camera_id,
                   d.event_type, d.confidence, d.vehicle_type, d.plate_number_raw,
                   d.plate_number_normalized, d.snapshot_reference
            FROM federation.detection_event d
            WHERE d.occurred_at >= @from AND d.occurred_at < @to
              AND (@PlateNumberNormalized::text IS NULL
                   OR d.plate_number_normalized = @PlateNumberNormalized)
              AND (@TargetId::uuid IS NULL OR d.target_id = @TargetId)
              {(unscopedOrg ? "" : "AND d.organization_unit_id = ANY (@units)")}
              {(unscopedGeo ? "" : """
              AND (d.geographic_area_id IS NULL OR d.geographic_area_id = ANY (@areas))
              """)}
            ORDER BY d.occurred_at DESC
            LIMIT @limit;
            """;

        var rows = await c.QueryAsync<DetectionEventRow>(new CommandDefinition(sql, new
        {
            from,
            to,
            PlateNumberNormalized = plateNumberNormalized,
            TargetId = targetId,
            limit = Math.Clamp(limit, 1, 500),
            units,
            areas,
        }, cancellationToken: ct));

        return [.. rows];
    }

    /// <summary>The organization units this caller may read detections for.</summary>
    private static async Task<Guid[]> AuthorizedOrgUnitsAsync(
        NpgsqlConnection c, CallerContext caller, CancellationToken ct)
    {
        var rows = await c.QueryAsync<Guid>(new CommandDefinition("""
            SELECT organization_unit_id FROM federation.authorized_org_units(
                p_user_id => @UserId, p_api_key_id => @ApiKeyId, p_permission => 'observation.read');
            """, new { caller.UserId, caller.ApiKeyId }, cancellationToken: ct));

        return [.. rows];
    }

    /// <summary>The geographic areas this caller may read detections for.</summary>
    private static async Task<Guid[]> AuthorizedAreasAsync(
        NpgsqlConnection c, CallerContext caller, CancellationToken ct)
    {
        var rows = await c.QueryAsync<Guid>(new CommandDefinition("""
            SELECT geographic_area_id FROM federation.authorized_geographic_areas(
                p_user_id => @UserId, p_api_key_id => @ApiKeyId, p_permission => 'observation.read');
            """, new { caller.UserId, caller.ApiKeyId }, cancellationToken: ct));

        return [.. rows];
    }

    private sealed record CameraLookupRow
    {
        public Guid? CameraId { get; init; }
        public Guid OrganizationUnitId { get; init; }
        public Guid? GeographicAreaId { get; init; }
    }
}
