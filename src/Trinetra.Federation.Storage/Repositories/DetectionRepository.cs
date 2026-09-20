using Dapper;
using Npgsql;
using Trinetra.Federation.Core.Model;

namespace Trinetra.Federation.Storage.Repositories;

/// <summary>One detection row, as read back.</summary>
public sealed record DetectionEventRow
{
    public string EventId { get; init; } = "";
    public DateTimeOffset OccurredAt { get; init; }
    public Guid? TargetId { get; init; }
    public string? NativeCameraId { get; init; }
    public Guid? CameraId { get; init; }
    public string EventType { get; init; } = "";
    public double? Confidence { get; init; }
    public string? VehicleType { get; init; }
    public string? PlateNumberRaw { get; init; }
    public string? PlateNumberNormalized { get; init; }
    public string? SnapshotReference { get; init; }

    /// <summary>The camera's own display name — resolved by <see cref="DetectionRepository.SearchAsync"/>
    /// from <c>cameras</c> (a standalone registry camera, keyed on <c>camera_id</c>) or
    /// <c>federated_camera</c> (a VMS-discovered one, keyed on <c>(target_id, native_camera_id)</c>),
    /// whichever this row actually has. Null only when neither source has a name on file.</summary>
    public string? CameraName { get; init; }
}

/// <summary>Outcome of adding an operator tag to a detection (v1.26).</summary>
public enum DetectionTagOutcome
{
    /// <summary>The tag was attached.</summary>
    Added,

    /// <summary>This detection already carries this tag (case-insensitive) — a no-op, not an
    /// error; a repeated "tag as reviewed" click should not surface a conflict to the operator.</summary>
    AlreadyTagged,

    /// <summary>No such detection, or it exists but is outside the caller's org+geo scope — the
    /// two are indistinguishable on purpose (CLAUDE.md: an out-of-scope record is 404, not 403).</summary>
    DetectionNotFound,
}

/// <summary>Outcome of a detection ingest attempt (finding 15-L1).</summary>
public enum DetectionIngestOutcome
{
    /// <summary>A new row was inserted.</summary>
    Inserted,

    /// <summary>
    /// This <c>id</c> was already stored with the same content — an at-least-once retry,
    /// accepted again with no state change and no second watchlist alert.
    /// </summary>
    DuplicateIdentical,

    /// <summary>
    /// This <c>id</c> was already stored with <b>different</b> content — a distinct detection
    /// reusing an id, not a retry. Silently accepting it as "already handled" would discard the
    /// new payload, so this is surfaced to the caller instead of swallowed.
    /// </summary>
    DuplicateConflict,
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
    /// <summary>The <c>snapshot_reference</c> value stored on a detection whose evidence lives in
    /// <c>detection_snapshot</c> (v1.30) rather than at an opaque filesystem path — a fixed,
    /// content-free marker rather than the actual bytes' location, since the location for a
    /// database-stored image is just "this database." The API's evidence route checks for this
    /// exact value to decide which of the two storage paths to read from.
    /// </summary>
    public const string DatabaseSnapshotMarker = "db";

    private readonly NpgsqlDataSource _dataSource;

    public DetectionRepository(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    /// <summary>
    /// Resolves a worker's camera id — either <c>"{targetId}:{nativeCameraId}"</c> against the
    /// VMS-federated inventory a worker discovered, or a bare registry camera UUID (v1.28) against
    /// <c>federation.cameras</c> directly, as the camera-lease claim endpoint (v1.27) now hands
    /// out both kinds — for the organization/geography scope to denormalize onto the detection row.
    /// </summary>
    /// <remarks>
    /// Finding 15-M3: an out-of-scope camera id used to resolve successfully here and only fail
    /// later, in <see cref="IngestAsync"/>'s own scope check — a distinguishable <c>403</c>
    /// (camera exists, wrong scope) versus this method's <c>null</c> (no such camera anywhere),
    /// turned into a <c>400</c> by the caller. That let any <c>observation.write</c> holder probe
    /// arbitrary <c>targetId:nativeId</c> pairs and learn whether a camera exists anywhere in the
    /// estate, regardless of their own scope — an existence oracle (CLAUDE.md invariant 11: a
    /// scoped query that omits its <see cref="CallerContext"/> should not compile, let alone leak
    /// this). The scope check now runs inline: an out-of-scope camera is indistinguishable from
    /// an unknown one — both return <see langword="null"/>. <see cref="IngestAsync"/> keeps its
    /// own check as a defense-in-depth backstop for any other caller of this repository.
    /// </remarks>
    public async Task<(Guid? TargetId, string? NativeCameraId, Guid? CameraId,
        Guid OrganizationUnitId, Guid? GeographicAreaId)?> ResolveCameraAsync(
        string workerCameraId, CallerContext caller, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);

        // The camera-lease claim endpoint (v1.27) hands a VMS-discovered camera back as
        // "vms:{targetId}:{nativeCameraId}" — the "vms:" tags the source for the claim table's
        // own bookkeeping, but a worker submits detections with the same id verbatim, so this
        // strips it back to the plain "{targetId}:{nativeCameraId}" shape below expects.
        if (workerCameraId.StartsWith("vms:", StringComparison.Ordinal))
        {
            workerCameraId = workerCameraId["vms:".Length..];
        }

        var separator = workerCameraId.IndexOf(':', StringComparison.Ordinal);
        if (separator <= 0 || !Guid.TryParse(workerCameraId[..separator], out var targetId))
        {
            // Not the VMS "{targetId}:{nativeCameraId}" shape — try it as a bare registry
            // camera id instead of failing outright.
            return Guid.TryParse(workerCameraId, out var cameraId)
                ? await ResolveRegistryCameraAsync(cameraId, caller, ct)
                : null;
        }

        var nativeCameraId = workerCameraId[(separator + 1)..];

        var orgOk = caller.IsUnscopedFor("observation.write");
        var geoOk = caller.IsUnscopedForGeography("observation.write");

        await using var c = await _dataSource.OpenConnectionAsync(ct);
        var row = await c.QuerySingleOrDefaultAsync<CameraLookupRow>(new CommandDefinition("""
            SELECT camera_id, organization_unit_id, geographic_area_id
            FROM federation.federated_camera
            WHERE target_id = @targetId AND native_camera_id = @nativeCameraId
              AND (@OrgOk OR organization_unit_id IN (
                  SELECT organization_unit_id FROM federation.authorized_org_units(
                      p_user_id => @UserId, p_api_key_id => @ApiKeyId,
                      p_permission => 'observation.write')))
              AND (@GeoOk OR geographic_area_id IS NULL OR geographic_area_id IN (
                  SELECT geographic_area_id FROM federation.authorized_geographic_areas(
                      p_user_id => @UserId, p_api_key_id => @ApiKeyId,
                      p_permission => 'observation.write')));
            """, new
            {
                targetId, nativeCameraId,
                caller.UserId, caller.ApiKeyId,
                OrgOk = orgOk, GeoOk = geoOk,
            }, cancellationToken: ct));

        return row is null
            ? null
            : (targetId, nativeCameraId, row.CameraId, row.OrganizationUnitId, row.GeographicAreaId);
    }

    /// <summary>The bare-UUID half of <see cref="ResolveCameraAsync"/> — a standalone registry
    /// camera has no <c>connector_target</c>, so it is resolved against <c>federation.cameras</c>
    /// directly rather than <c>federated_camera</c>.</summary>
    private async Task<(Guid? TargetId, string? NativeCameraId, Guid? CameraId,
        Guid OrganizationUnitId, Guid? GeographicAreaId)?> ResolveRegistryCameraAsync(
        Guid cameraId, CallerContext caller, CancellationToken ct)
    {
        var orgOk = caller.IsUnscopedFor("observation.write");
        var geoOk = caller.IsUnscopedForGeography("observation.write");

        await using var c = await _dataSource.OpenConnectionAsync(ct);
        var row = await c.QuerySingleOrDefaultAsync<CameraLookupRow>(new CommandDefinition("""
            SELECT id AS camera_id, organization_unit_id, geographic_area_id
            FROM federation.cameras
            WHERE id = @cameraId AND deleted_at IS NULL
              AND (@OrgOk OR organization_unit_id IN (
                  SELECT organization_unit_id FROM federation.authorized_org_units(
                      p_user_id => @UserId, p_api_key_id => @ApiKeyId,
                      p_permission => 'observation.write')))
              AND (@GeoOk OR geographic_area_id IN (
                  SELECT geographic_area_id FROM federation.authorized_geographic_areas(
                      p_user_id => @UserId, p_api_key_id => @ApiKeyId,
                      p_permission => 'observation.write')));
            """, new
            {
                cameraId,
                caller.UserId, caller.ApiKeyId,
                OrgOk = orgOk, GeoOk = geoOk,
            }, cancellationToken: ct));

        return row is null
            ? null
            : ((Guid?)null, (string?)null, row.CameraId, row.OrganizationUnitId, row.GeographicAreaId);
    }

    /// <summary>
    /// Inserts a detection, idempotent on <see cref="DetectionEvent.Id"/>. A same-id retry with
    /// identical content is accepted again with no new row and no second watchlist match; a
    /// same-id submission with <b>different</b> content is reported as
    /// <see cref="DetectionIngestOutcome.DuplicateConflict"/> rather than silently dropped
    /// (finding 15-L1) — the id is caller-supplied, so a collision from a buggy worker retry
    /// must not be mistaken for the original detection having been stored.
    /// </summary>
    public async Task<DetectionIngestOutcome> IngestAsync(
        DetectionEvent evt, Guid? targetId, string? nativeCameraId, Guid? cameraId,
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

        var insertedId = await work.Connection.ExecuteScalarAsync<string?>(new CommandDefinition("""
            INSERT INTO federation.detection_event
                (event_id, occurred_at, target_id, native_camera_id, camera_id,
                 organization_unit_id, geographic_area_id, event_type, confidence,
                 vehicle_type, plate_number_raw, plate_number_normalized, snapshot_reference)
            VALUES (@EventId, @OccurredAt, @TargetId, @NativeCameraId, @CameraId,
                    @OrganizationUnitId, @GeographicAreaId, @EventType, @Confidence,
                    @VehicleType, @PlateNumberRaw, @PlateNumberNormalized, @SnapshotReference)
            ON CONFLICT (event_id, occurred_at) DO NOTHING
            RETURNING event_id;
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

        if (insertedId is not null)
        {
            return DetectionIngestOutcome.Inserted;
        }

        // The conflict target is (event_id, occurred_at), so this is the same detection id
        // reported at the same timestamp — compare the rest of the content to tell an
        // at-least-once retry from a genuine id collision.
        var existing = await work.Connection.QuerySingleAsync<DetectionEventRow>(
            new CommandDefinition("""
                SELECT event_id AS EventId, occurred_at AS OccurredAt, target_id AS TargetId,
                       native_camera_id AS NativeCameraId, camera_id AS CameraId,
                       event_type AS EventType, confidence AS Confidence,
                       vehicle_type AS VehicleType, plate_number_raw AS PlateNumberRaw,
                       plate_number_normalized AS PlateNumberNormalized,
                       snapshot_reference AS SnapshotReference
                FROM federation.detection_event
                WHERE event_id = @EventId AND occurred_at = @OccurredAt;
                """, new { EventId = evt.Id, OccurredAt = evt.Timestamp },
                work.Transaction, cancellationToken: ct));

        var identical =
            existing.TargetId == targetId
            && existing.NativeCameraId == nativeCameraId
            && existing.CameraId == cameraId
            && existing.EventType == evt.EventType
            && existing.Confidence == evt.Confidence
            && existing.VehicleType == evt.VehicleType
            && existing.PlateNumberRaw == evt.PlateNumberRaw
            && existing.PlateNumberNormalized == plateNumberNormalized
            && existing.SnapshotReference == evt.SnapshotReference;

        return identical
            ? DetectionIngestOutcome.DuplicateIdentical
            : DetectionIngestOutcome.DuplicateConflict;
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
                   d.plate_number_normalized, d.snapshot_reference,
                   COALESCE(cam.name, fc.name) AS camera_name
            FROM federation.detection_event d
            LEFT JOIN federation.cameras cam ON cam.id = d.camera_id
            LEFT JOIN federation.federated_camera fc
                ON fc.target_id = d.target_id AND fc.native_camera_id = d.native_camera_id
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

    /// <summary>
    /// The stored <c>snapshot_reference</c> for one detection, scoped to the caller's org+geo
    /// reach exactly like <see cref="SearchAsync"/> — used by the evidence-image route, which
    /// must never serve a snapshot the caller could not already see via search. Null when the
    /// detection has no snapshot on file; the whole result is null when the id/timestamp pair is
    /// unknown or out of the caller's scope (the two are indistinguishable on purpose, matching
    /// <see cref="AddTagAsync"/>'s posture).
    /// </summary>
    public async Task<string?> GetSnapshotReferenceAsync(
        string eventId, DateTimeOffset occurredAt, CallerContext caller, CancellationToken ct)
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
            return null;
        }

        var sql = $"""
            SELECT snapshot_reference
            FROM federation.detection_event d
            WHERE d.event_id = @eventId AND d.occurred_at = @occurredAt
              {(unscopedOrg ? "" : "AND d.organization_unit_id = ANY (@units)")}
              {(unscopedGeo ? "" : """
              AND (d.geographic_area_id IS NULL OR d.geographic_area_id = ANY (@areas))
              """)};
            """;

        // QuerySingleOrDefaultAsync<string?> can't distinguish "no matching row" from "row found
        // with a null column" by itself, but both are treated identically by the caller (404
        // either way), so a plain query is enough here.
        var found = await c.QueryAsync<string?>(new CommandDefinition(sql, new
        {
            eventId, occurredAt, units, areas,
        }, cancellationToken: ct));

        return found.FirstOrDefault();
    }

    /// <summary>
    /// Stores a detection's evidence image bytes in <c>detection_snapshot</c> (v1.30), in the
    /// same transaction as the detection insert itself — a snapshot and the detection it's
    /// evidence for should never be able to diverge (one committed, the other not). Idempotent on
    /// <c>(detection_event_id, detection_occurred_at)</c>, matching the detection row's own
    /// idempotent-retry posture: a same-id ingest retry overwrites with identical bytes, a no-op
    /// in effect.
    /// </summary>
    public async Task SaveSnapshotAsync(
        string eventId, DateTimeOffset occurredAt, byte[] imageData, string contentType,
        string? contentEncoding, UnitOfWork work, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(work);

        await work.Connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO federation.detection_snapshot
                (detection_event_id, detection_occurred_at, image_data, content_type, content_encoding)
            VALUES (@eventId, @occurredAt, @imageData, @contentType, @contentEncoding)
            ON CONFLICT (detection_event_id, detection_occurred_at)
            DO UPDATE SET image_data = EXCLUDED.image_data, content_type = EXCLUDED.content_type,
                          content_encoding = EXCLUDED.content_encoding;
            """, new { eventId, occurredAt, imageData, contentType, contentEncoding },
            work.Transaction, cancellationToken: ct));
    }

    /// <summary>
    /// The stored evidence image for one detection, scoped to the caller's org+geo reach exactly
    /// like <see cref="GetSnapshotReferenceAsync"/> — the two together are what the evidence
    /// route needs: confirm scope, then fetch bytes only for a detection whose
    /// <c>snapshot_reference</c> is <see cref="DatabaseSnapshotMarker"/>.
    /// </summary>
    public async Task<(byte[] ImageData, string ContentType, string? ContentEncoding)?> GetSnapshotAsync(
        string eventId, DateTimeOffset occurredAt, CallerContext caller, CancellationToken ct)
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
            return null;
        }

        var sql = $"""
            SELECT s.image_data AS ImageData, s.content_type AS ContentType,
                   s.content_encoding AS ContentEncoding
            FROM federation.detection_snapshot s
            JOIN federation.detection_event d
                ON d.event_id = s.detection_event_id AND d.occurred_at = s.detection_occurred_at
            WHERE s.detection_event_id = @eventId AND s.detection_occurred_at = @occurredAt
              {(unscopedOrg ? "" : "AND d.organization_unit_id = ANY (@units)")}
              {(unscopedGeo ? "" : """
              AND (d.geographic_area_id IS NULL OR d.geographic_area_id = ANY (@areas))
              """)};
            """;

        var row = await c.QuerySingleOrDefaultAsync<SnapshotRow>(new CommandDefinition(sql, new
        {
            eventId, occurredAt, units, areas,
        }, cancellationToken: ct));

        return row is null ? null : (row.ImageData, row.ContentType, row.ContentEncoding);
    }

    private sealed record SnapshotRow
    {
        public byte[] ImageData { get; init; } = [];
        public string ContentType { get; init; } = "";
        public string? ContentEncoding { get; init; }
    }

    /// <summary>
    /// Attaches a free-form operator tag to a detection — RFP "event tagging," distinct from and
    /// additive to <c>event_type</c>'s closed machine classification (see v1.26's own doc
    /// comment). Confirms the detection exists and is in the caller's org+geo scope by reading
    /// <c>detection_event</c> directly (no FK is possible across the partition boundary — same
    /// migration's note), then denormalises that scope onto the new tag row so a later list/
    /// delete never needs to re-join detection_event.
    /// </summary>
    public async Task<DetectionTagOutcome> AddTagAsync(
        string eventId, DateTimeOffset occurredAt, string tag,
        CallerContext caller, UnitOfWork work, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(work);
        caller.Require("observation.write");

        var unscopedOrg = caller.IsUnscopedFor("observation.write");
        var unscopedGeo = caller.IsUnscopedForGeography("observation.write");

        Guid[] units = unscopedOrg ? [] : await AuthorizedOrgUnitsAsync(work.Connection, caller, ct, "observation.write");
        Guid[] areas = unscopedGeo ? [] : await AuthorizedAreasAsync(work.Connection, caller, ct, "observation.write");

        var detection = await work.Connection.QuerySingleOrDefaultAsync<DetectionScopeRow>(
            new CommandDefinition("""
                SELECT organization_unit_id AS OrganizationUnitId, geographic_area_id AS GeographicAreaId
                FROM federation.detection_event
                WHERE event_id = @EventId AND occurred_at = @OccurredAt
                  AND (@UnscopedOrg OR organization_unit_id = ANY (@Units))
                  AND (@UnscopedGeo OR geographic_area_id IS NULL OR geographic_area_id = ANY (@Areas));
                """, new { EventId = eventId, OccurredAt = occurredAt, UnscopedOrg = unscopedOrg, UnscopedGeo = unscopedGeo, Units = units, Areas = areas },
                work.Transaction, cancellationToken: ct));

        if (detection is null)
        {
            return DetectionTagOutcome.DetectionNotFound;
        }

        var insertedId = await work.Connection.ExecuteScalarAsync<Guid?>(new CommandDefinition("""
            INSERT INTO federation.detection_tag
                (detection_event_id, detection_occurred_at, tag, organization_unit_id,
                 geographic_area_id, created_by)
            VALUES (@EventId, @OccurredAt, @Tag, @OrganizationUnitId, @GeographicAreaId, @CreatedBy)
            ON CONFLICT (detection_event_id, lower(tag)) DO NOTHING
            RETURNING id;
            """, new
        {
            EventId = eventId, OccurredAt = occurredAt, Tag = tag,
            detection.OrganizationUnitId, detection.GeographicAreaId,
            CreatedBy = caller.UserId,
        }, work.Transaction, cancellationToken: ct));

        return insertedId is null ? DetectionTagOutcome.AlreadyTagged : DetectionTagOutcome.Added;
    }

    /// <summary>Removes one tag from a detection. A caller only sees/affects tags on detections
    /// within their own org+geo scope — enforced directly against the tag row's own denormalised
    /// scope columns, not by re-reading detection_event.</summary>
    public async Task<bool> RemoveTagAsync(
        string eventId, string tag, CallerContext caller, UnitOfWork work, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(work);
        caller.Require("observation.write");

        var unscopedOrg = caller.IsUnscopedFor("observation.write");
        var unscopedGeo = caller.IsUnscopedForGeography("observation.write");
        Guid[] units = unscopedOrg ? [] : await AuthorizedOrgUnitsAsync(work.Connection, caller, ct, "observation.write");
        Guid[] areas = unscopedGeo ? [] : await AuthorizedAreasAsync(work.Connection, caller, ct, "observation.write");

        var deletedId = await work.Connection.ExecuteScalarAsync<Guid?>(new CommandDefinition("""
            DELETE FROM federation.detection_tag
            WHERE detection_event_id = @EventId AND lower(tag) = lower(@Tag)
              AND (@UnscopedOrg OR organization_unit_id = ANY (@Units))
              AND (@UnscopedGeo OR geographic_area_id IS NULL OR geographic_area_id = ANY (@Areas))
            RETURNING id;
            """, new { EventId = eventId, Tag = tag, UnscopedOrg = unscopedOrg, UnscopedGeo = unscopedGeo, Units = units, Areas = areas },
            work.Transaction, cancellationToken: ct));

        return deletedId is not null;
    }

    /// <summary>Tags for a set of detections the caller has already legitimately fetched (e.g.
    /// via <see cref="SearchAsync"/>, which already applied the org+geo scope check) — grouped by
    /// <c>event_id</c> for the caller to fold into each detection's own response. Not itself
    /// scope-checked: the event ids passed in are assumed already scoped by the caller of this
    /// method, the same "don't re-check what the caller already checked" reasoning
    /// <c>StreamSessionEndpoints</c> documents for its own unscoped lookup.</summary>
    public async Task<ILookup<string, string>> GetTagsForEventsAsync(
        IReadOnlyCollection<string> eventIds, CancellationToken ct)
    {
        if (eventIds.Count == 0) return Array.Empty<(string, string)>().ToLookup(x => x.Item1, x => x.Item2);

        await using var c = await _dataSource.OpenConnectionAsync(ct);
        var rows = await c.QueryAsync<(string EventId, string Tag)>(new CommandDefinition("""
            SELECT detection_event_id AS EventId, tag AS Tag
            FROM federation.detection_tag
            WHERE detection_event_id = ANY (@EventIds)
            ORDER BY tag;
            """, new { EventIds = eventIds.ToArray() }, cancellationToken: ct));

        return rows.ToLookup(r => r.EventId, r => r.Tag);
    }

    /// <summary>The organization units this caller may reach for <paramref name="permission"/>
    /// (<c>observation.read</c> for search, <c>observation.write</c> for tagging).</summary>
    private static async Task<Guid[]> AuthorizedOrgUnitsAsync(
        NpgsqlConnection c, CallerContext caller, CancellationToken ct, string permission = "observation.read")
    {
        var rows = await c.QueryAsync<Guid>(new CommandDefinition("""
            SELECT organization_unit_id FROM federation.authorized_org_units(
                p_user_id => @UserId, p_api_key_id => @ApiKeyId, p_permission => @Permission);
            """, new { caller.UserId, caller.ApiKeyId, Permission = permission }, cancellationToken: ct));

        return [.. rows];
    }

    /// <summary>The geographic areas this caller may reach for <paramref name="permission"/>
    /// (<c>observation.read</c> for search, <c>observation.write</c> for tagging).</summary>
    private static async Task<Guid[]> AuthorizedAreasAsync(
        NpgsqlConnection c, CallerContext caller, CancellationToken ct, string permission = "observation.read")
    {
        var rows = await c.QueryAsync<Guid>(new CommandDefinition("""
            SELECT geographic_area_id FROM federation.authorized_geographic_areas(
                p_user_id => @UserId, p_api_key_id => @ApiKeyId, p_permission => @Permission);
            """, new { caller.UserId, caller.ApiKeyId, Permission = permission }, cancellationToken: ct));

        return [.. rows];
    }

    private sealed record CameraLookupRow
    {
        public Guid? CameraId { get; init; }
        public Guid OrganizationUnitId { get; init; }
        public Guid? GeographicAreaId { get; init; }
    }

    private sealed record DetectionScopeRow
    {
        public Guid OrganizationUnitId { get; init; }
        public Guid? GeographicAreaId { get; init; }
    }
}
