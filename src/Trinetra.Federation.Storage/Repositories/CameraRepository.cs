using Dapper;
using Npgsql;
using Trinetra.Federation.Core.Model;

namespace Trinetra.Federation.Storage.Repositories;

/// <summary>
/// The authoritative camera registry, scoped to what the caller may reach.
/// </summary>
/// <remarks>
/// <para>
/// Every read and write is constrained on <b>both</b> scope dimensions, ANDed and each with its
/// own unscoped flag (<c>CLAUDE.md</c> invariant 12): the organization dimension against
/// <c>cameras.organization_unit_id</c>, the geographic dimension against
/// <c>cameras.geographic_area_id</c>. Unlike <c>connector_target</c>, a camera's
/// <c>geographic_area_id</c> is <c>NOT NULL</c>, so the geographic check is never skipped.
/// </para>
/// <para>
/// A camera the caller cannot reach returns null / affects no rows — never a 403. Telling an
/// unauthorised caller that an id exists is itself a disclosure.
/// </para>
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Performance", "CA1822:Mark members as static",
    Justification = "Write methods take their connection from the UnitOfWork by design.",
    Scope = "type")]
public sealed class CameraRepository
{
    private const string Columns = """
        c.id, c.camera_code AS code, c.name,
        c.organization_unit_id, c.geographic_area_id, c.manufacturer, c.model, c.camera_type,
        c.serial_number, c.latitude, c.longitude, c.altitude, c.mounting_height,
        c.azimuth, c.tilt, c.horizontal_fov, c.vertical_fov, c.effective_range,
        host(c.ip_address) AS ip_address, c.port, c.protocol,
        c.vms_id, c.stream_reference, c.stream_preference, c.native_hls_url, c.native_webrtc_url,
        c.credential_reference, c.installation_date,
        c.record_events,
        c.operational_status, c.connectivity_status, c.maintenance_status,
        c.last_seen_at, c.last_health_check_at, c.deleted_at
        """;

    // The camera's geographic-scope key — a direct column since sites were removed.
    private const string SiteArea = "c.geographic_area_id";

    private readonly NpgsqlDataSource _dataSource;

    public CameraRepository(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    // -------------------------------------------------------------------
    // Reads
    // -------------------------------------------------------------------

    public async Task<Camera?> GetAsync(Guid id, CallerContext caller, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require("camera.read");

        await using var c = await _dataSource.OpenConnectionAsync(ct);

        return await GetAsync(id, caller, c, null, ct);
    }

    /// <summary>
    /// Reads within an in-flight <see cref="UnitOfWork"/>'s own connection and transaction —
    /// needed to read back a row this same transaction just wrote. A read on a fresh connection
    /// (the plain <see cref="GetAsync(Guid,CallerContext,CancellationToken)"/> overload) opens a
    /// separate Postgres session, so under read-committed isolation it cannot see this
    /// transaction's own uncommitted write: called before <c>CommitAsync</c>, it silently returns
    /// the row as it stood <i>before</i> this write, not after it.
    /// </summary>
    public Task<Camera?> GetAsync(Guid id, CallerContext caller, UnitOfWork work, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(work);
        return GetAsync(id, caller, work.Connection, work.Transaction, ct);
    }

    private async Task<Camera?> GetAsync(
        Guid id, CallerContext caller, NpgsqlConnection connection, NpgsqlTransaction? transaction,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require("camera.read");

        var row = await connection.QuerySingleOrDefaultAsync<CameraRow>(new CommandDefinition($"""
            SELECT {Columns}
            FROM federation.cameras c
            WHERE c.id = @id
              AND ({Scope("camera.read")})
            """, ScopeArgs(id, caller, "camera.read"), transaction, cancellationToken: ct));

        return row?.ToDomain();
    }

    /// <summary>One page of the registry, keyset-ordered by <c>camera_code</c>.</summary>
    public async Task<IReadOnlyList<Camera>> ListAsync(
        CameraQuery query, CallerContext caller, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require("camera.read");

        await using var c = await _dataSource.OpenConnectionAsync(ct);

        // Finding 10-M6: a retired camera's code is freed for reuse (v1.6.sql), so two live+
        // retired rows can share a camera_code once `includeRetired=true` widens the result set.
        // Keying the cursor on code alone made `> @Cursor` an unstable tiebreak between them —
        // whichever tied row landed last on a page could be skipped entirely on the next page
        // (both compare equal to the boundary, "greater than" excludes both). Ordering and
        // paging on (camera_code, id) together makes the sequence total and stable regardless of
        // how many rows share a code.
        var sql = $"""
            SELECT {Columns}
            FROM federation.cameras c
            WHERE ({Scope("camera.read")})
              AND (@IncludeRetired OR c.deleted_at IS NULL)
              AND (@OrganizationUnitId::uuid IS NULL
                   OR c.organization_unit_id IN (
                       SELECT id FROM federation.org_unit_descendants(@OrganizationUnitId)))
              AND (@GeographicAreaId::uuid IS NULL
                   OR c.geographic_area_id IN (
                       SELECT id FROM federation.geographic_area_descendants(@GeographicAreaId)))
              AND (@CameraType::text IS NULL OR c.camera_type = @CameraType)
              AND (@OperationalStatus::text IS NULL OR c.operational_status = @OperationalStatus)
              AND (@ConnectivityStatus::text IS NULL OR c.connectivity_status = @ConnectivityStatus)
              AND (@MaintenanceStatus::text IS NULL OR c.maintenance_status = @MaintenanceStatus)
              AND (@Query::text IS NULL
                   OR c.camera_code ILIKE @Query || '%' OR c.name ILIKE @Query || '%')
              AND (@MinLon::numeric IS NULL OR (
                       c.longitude BETWEEN @MinLon AND @MaxLon
                       AND c.latitude BETWEEN @MinLat AND @MaxLat))
              AND (@Cursor::text IS NULL OR (c.camera_code, c.id) > (@Cursor, @CursorId::uuid))
            ORDER BY c.camera_code, c.id
            LIMIT @Limit
            """;

        var args = new DynamicParameters(ScopeArgs(Guid.Empty, caller, "camera.read"));
        args.Add("CursorId", query.CursorId);
        args.Add("IncludeRetired", query.IncludeRetired);
        args.Add("OrganizationUnitId", query.OrganizationUnitId);
        args.Add("GeographicAreaId", query.GeographicAreaId);
        args.Add("CameraType", query.CameraType);
        args.Add("OperationalStatus", query.OperationalStatus);
        args.Add("ConnectivityStatus", query.ConnectivityStatus);
        args.Add("MaintenanceStatus", query.MaintenanceStatus);
        args.Add("Query", query.Query);
        args.Add("MinLon", query.Bbox?.MinLon);
        args.Add("MaxLon", query.Bbox?.MaxLon);
        args.Add("MinLat", query.Bbox?.MinLat);
        args.Add("MaxLat", query.Bbox?.MaxLat);
        args.Add("Cursor", query.Cursor);
        args.Add("Limit", query.Limit);

        var rows = await c.QueryAsync<CameraRow>(new CommandDefinition(sql, args, cancellationToken: ct));
        return [.. rows.Select(r => r.ToDomain())];
    }

    /// <summary>
    /// RFP Model 1 "ageing-infrastructure reporting": buckets the caller's live, in-scope
    /// cameras by age since <c>installation_date</c>, plus the oldest <paramref name="oldestLimit"/>
    /// with a known date — the two things a maintenance planner actually needs (how much
    /// equipment falls in each age band, and which specific cameras are the priority
    /// replacement candidates). A camera with no recorded <c>installation_date</c> is its own
    /// bucket rather than silently excluded — an unknown install date is itself something to go
    /// fix, not something to pretend doesn't exist.
    /// </summary>
    public async Task<AgeingInfrastructureRow> GetAgeingInfrastructureAsync(
        CallerContext caller, Guid? organizationUnitId, Guid? geographicAreaId,
        int oldestLimit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require("camera.read");

        await using var c = await _dataSource.OpenConnectionAsync(ct);

        var args = ScopeArgs(Guid.Empty, caller, "camera.read");
        args.Add("OrganizationUnitId", organizationUnitId);
        args.Add("GeographicAreaId", geographicAreaId);
        args.Add("OldestLimit", oldestLimit);

        // Bucket boundaries are a fixed policy choice, not configuration — CCTV hardware is
        // typically rated for a 5-7 year service life, so "5-10 years" and "10+ years" are the
        // two bands worth a planner's attention; "under 3" and "3-5" separate recently-installed
        // stock from stock approaching that window. Repeated in both statements below (a CTE
        // can't span two separate SQL statements) rather than factored out — see AGE_BUCKET_CASE.
        const string ageBucketCase = """
            CASE
                WHEN c.installation_date IS NULL THEN 'unknown'
                WHEN c.installation_date > (CURRENT_DATE - INTERVAL '3 years') THEN 'under_3'
                WHEN c.installation_date > (CURRENT_DATE - INTERVAL '5 years') THEN '3_to_5'
                WHEN c.installation_date > (CURRENT_DATE - INTERVAL '10 years') THEN '5_to_10'
                ELSE '10_plus'
            END
            """;

        var scopedWhere = $"""
            FROM federation.cameras c
            WHERE c.deleted_at IS NULL
              AND ({Scope("camera.read")})
              AND (@OrganizationUnitId::uuid IS NULL
                   OR c.organization_unit_id IN (
                       SELECT id FROM federation.org_unit_descendants(@OrganizationUnitId)))
              AND (@GeographicAreaId::uuid IS NULL
                   OR c.geographic_area_id IN (
                       SELECT id FROM federation.geographic_area_descendants(@GeographicAreaId)))
            """;

        using var multi = await c.QueryMultipleAsync(new CommandDefinition($"""
            SELECT {ageBucketCase} AS Bucket, count(*)::int AS Count
            {scopedWhere}
            GROUP BY Bucket;

            SELECT c.id AS Id, c.camera_code AS CameraCode, c.name AS Name,
                   c.installation_date AS InstallationDate,
                   (EXTRACT(YEAR FROM age(CURRENT_DATE, c.installation_date)))::int AS AgeYears,
                   c.maintenance_status AS MaintenanceStatus
            {scopedWhere}
              AND c.installation_date IS NOT NULL
            ORDER BY c.installation_date ASC, c.id
            LIMIT @OldestLimit;
            """, args, cancellationToken: ct));

        var buckets = (await multi.ReadAsync<AgeBucketRow>()).ToDictionary(r => r.Bucket, r => r.Count);
        var oldest = (await multi.ReadAsync<AgeingCameraRow>()).ToList();

        return new AgeingInfrastructureRow(
            UnderThreeYears: buckets.GetValueOrDefault("under_3"),
            ThreeToFiveYears: buckets.GetValueOrDefault("3_to_5"),
            FiveToTenYears: buckets.GetValueOrDefault("5_to_10"),
            TenPlusYears: buckets.GetValueOrDefault("10_plus"),
            UnknownInstallationDate: buckets.GetValueOrDefault("unknown"),
            OldestCameras: oldest);
    }

    /// <summary>The id of the live camera with this code the caller can reach, or null.</summary>
    public async Task<Guid?> FindLiveIdByCodeAsync(
        string code, CallerContext caller, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require("camera.read");

        await using var c = await _dataSource.OpenConnectionAsync(ct);

        return await FindLiveIdByCodeAsync(code, caller, c, null, ct);
    }

    /// <summary>
    /// The transaction-scoped form of <see cref="FindLiveIdByCodeAsync(string, CallerContext, CancellationToken)"/>.
    /// </summary>
    /// <remarks>
    /// Bulk import (finding 10-M4) shares one transaction across up to 500 rows now, instead of
    /// committing each row's write independently before the next row runs. The connection-per-call
    /// form above reads on its own connection at READ COMMITTED, so it cannot see an earlier
    /// row's still-uncommitted insert in the SAME batch — two rows in one `upsert` request
    /// sharing a `cameraCode` would both take the "no existing row" branch and the second would
    /// hit a live `UniqueViolation` instead of correctly replacing the first. Reading through the
    /// batch's own connection/transaction instead means row 2 sees row 1's uncommitted insert,
    /// exactly as it would have seen row 1's already-committed insert under the old per-row-
    /// transaction design — same observable behaviour, cheaper underlying mechanism.
    /// </remarks>
    public async Task<Guid?> FindLiveIdByCodeAsync(
        string code, CallerContext caller, UnitOfWork work, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(work);
        caller.Require("camera.read");

        return await FindLiveIdByCodeAsync(code, caller, work.Connection, work.Transaction, ct);
    }

    private async Task<Guid?> FindLiveIdByCodeAsync(
        string code, CallerContext caller, NpgsqlConnection connection, NpgsqlTransaction? transaction,
        CancellationToken ct)
    {
        var args = ScopeArgs(Guid.Empty, caller, "camera.read");
        args.Add("code", code);

        return await connection.ExecuteScalarAsync<Guid?>(new CommandDefinition($"""
            SELECT c.id FROM federation.cameras c
            WHERE c.camera_code = @code AND c.deleted_at IS NULL
              AND ({Scope("camera.read")});
            """, args, transaction, cancellationToken: ct));
    }

    /// <summary>
    /// Deliberately unscoped — <b>the one exception to invariant 11</b> alongside
    /// <see cref="GetAsync(Guid,CallerContext,CancellationToken)"/>'s own mint-time check. Called
    /// only from <c>StreamSessionEndpoints.ProxyAsync</c>/the WHEP relay routes on every
    /// playlist/segment/signaling request for a live viewing session — the scope check already
    /// ran once, at <c>GET /streams/{cameraId}/session</c>'s mint time
    /// (<c>CameraRepository.GetAsync</c> there), and the stream-session token's short lifetime
    /// stands in for re-checking it here, exactly as the class remarks on
    /// <c>StreamSessionEndpoints</c> already document for the RTSP/MediaMTX path. Re-scoping on
    /// every segment would also defeat the point of embedding <c>cameraId</c> straight in that
    /// token instead of a user identity. Returns null only if the camera does not exist or is
    /// retired — never for an out-of-scope caller, since there is no caller identity to scope
    /// against here.
    /// </summary>
    public async Task<CameraStreamRouting?> GetStreamRoutingAsync(Guid cameraId, CancellationToken ct)
    {
        await using var c = await _dataSource.OpenConnectionAsync(ct);
        return await c.QuerySingleOrDefaultAsync<CameraStreamRouting>(new CommandDefinition("""
            SELECT stream_preference AS StreamPreference, native_hls_url AS NativeHlsUrl,
                   native_webrtc_url AS NativeWebrtcUrl, credential_reference AS CredentialReference
            FROM federation.cameras
            WHERE id = @cameraId AND deleted_at IS NULL;
            """, new { cameraId }, cancellationToken: ct));
    }

    // -------------------------------------------------------------------
    // Writes
    // -------------------------------------------------------------------

    /// <summary>Inserts a new registry record. Returns its id.</summary>
    public async Task<Guid> CreateAsync(
        Camera camera, CallerContext caller, UnitOfWork work, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(camera);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(work);
        caller.Require("camera.create");

        await RequirePlacementAsync(
            work, caller, "camera.create", camera.OrganizationUnitId, camera.GeographicAreaId, ct);

        return await work.Connection.ExecuteScalarAsync<Guid>(new CommandDefinition(
            $"""
            INSERT INTO federation.cameras
                ({InsertColumns}, created_by, updated_by)
            VALUES ({InsertValues}, @ActorId, @ActorId)
            RETURNING id;
            """, WriteArgs(camera, caller), work.Transaction, cancellationToken: ct));
    }

    /// <summary>Full replace of a camera the caller can reach. Returns false if it cannot.</summary>
    public async Task<bool> ReplaceAsync(
        Guid id, Camera camera, CallerContext caller, UnitOfWork work, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(camera);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(work);
        caller.Require("camera.update");

        // A move must be authorised at both ends: the caller may not push a camera into a unit
        // or site they cannot see, nor pull one out of their own view.
        await RequirePlacementAsync(
            work, caller, "camera.update", camera.OrganizationUnitId, camera.GeographicAreaId, ct);

        var affected = await work.Connection.ExecuteAsync(new CommandDefinition($"""
            UPDATE federation.cameras c
            SET camera_code = @Code, name = @Name,
                organization_unit_id = @OrganizationUnitId, geographic_area_id = @GeographicAreaId,
                manufacturer = @Manufacturer, model = @Model, camera_type = @CameraType,
                serial_number = @SerialNumber, latitude = @Latitude, longitude = @Longitude,
                altitude = @Altitude, mounting_height = @MountingHeight,
                azimuth = @Azimuth, tilt = @Tilt, horizontal_fov = @HorizontalFov,
                vertical_fov = @VerticalFov, effective_range = @EffectiveRange,
                ip_address = @IpAddress::inet, port = @Port, protocol = @Protocol,
                vms_id = @VmsId, stream_reference = @StreamReference,
                stream_preference = @StreamPreference, native_hls_url = @NativeHlsUrl,
                native_webrtc_url = @NativeWebrtcUrl,
                credential_reference = @CredentialReference, installation_date = @InstallationDate,
                record_events = @RecordEvents,
                operational_status = @OperationalStatus, connectivity_status = @ConnectivityStatus,
                maintenance_status = @MaintenanceStatus,
                updated_by = @ActorId, updated_at = now()
            WHERE c.id = @Id
              AND c.deleted_at IS NULL
              AND ({Scope("camera.update")});
            """, WriteArgs(camera with { Id = id }, caller), work.Transaction, cancellationToken: ct));

        return affected > 0;
    }

    /// <summary>Applies a partial update. <paramref name="patch"/> carries only the touched fields.</summary>
    public async Task<bool> PatchAsync(
        Guid id, CameraPatch patch, CallerContext caller, UnitOfWork work, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(patch);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(work);
        caller.Require("camera.update");

        if (patch.Assignments.Count == 0)
        {
            // Nothing to change, but still verify reachability so the caller gets 404 not 200.
            return await GetAsync(id, caller, ct) is not null;
        }

        if (patch.NewOrganizationUnitId is { } newOrg)
        {
            await RequirePlacementAsync(work, caller, "camera.update", newOrg, patch.NewGeographicAreaId, ct);
        }
        else if (patch.NewGeographicAreaId is { } newArea)
        {
            await RequirePlacementAsync(work, caller, "camera.update", null, newArea, ct);
        }

        var set = string.Join(", ", patch.Assignments) + ", updated_by = @ActorId, updated_at = now()";

        var args = ScopeArgs(id, caller, "camera.update");
        foreach (var (name, value) in patch.Parameters)
        {
            args.Add(name, value);
        }

        args.Add("ActorId", caller.UserId);

        var affected = await work.Connection.ExecuteAsync(new CommandDefinition($"""
            UPDATE federation.cameras c
            SET {set}
            WHERE c.id = @id
              AND c.deleted_at IS NULL
              AND ({Scope("camera.update")});
            """, args, work.Transaction, cancellationToken: ct));

        return affected > 0;
    }

    /// <summary>Retires (soft-deletes) a camera. Idempotent: an already-retired camera returns true.</summary>
    public async Task<bool> RetireAsync(
        Guid id, CallerContext caller, UnitOfWork work, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(work);
        caller.Require("camera.delete");

        var affected = await work.Connection.ExecuteAsync(new CommandDefinition($"""
            UPDATE federation.cameras c
            SET maintenance_status = 'RETIRED', deleted_at = now(), deleted_by = @ActorId,
                updated_by = @ActorId, updated_at = now()
            WHERE c.id = @id
              AND c.deleted_at IS NULL
              AND ({Scope("camera.delete")});
            """,
            AddActor(ScopeArgs(id, caller, "camera.delete"), caller),
            work.Transaction, cancellationToken: ct));

        if (affected > 0)
        {
            return true;
        }

        // Zero rows: either out of scope (→ false, becomes 404) or already retired (→ true).
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var reachableAndRetired = await conn.ExecuteScalarAsync<bool>(new CommandDefinition($"""
            SELECT EXISTS (
                SELECT 1 FROM federation.cameras c
                WHERE c.id = @id AND c.deleted_at IS NOT NULL AND ({Scope("camera.delete")}));
            """, ScopeArgs(id, caller, "camera.delete"), cancellationToken: ct));

        return reachableAndRetired;
    }

    /// <summary>
    /// Persists a generated <c>credential_reference</c> on a camera that does not have one yet.
    /// Never overwrites an existing reference: the reference is the key into
    /// <c>federation.secret</c>, so changing it out from under an already-sealed credential would
    /// orphan the stored secret. Returns false if the camera is out of scope.
    /// </summary>
    public async Task<bool> SetCredentialReferenceIfMissingAsync(
        Guid id, string reference, CallerContext caller, UnitOfWork work, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(work);
        caller.Require("camera.update");

        var args = ScopeArgs(id, caller, "camera.update");
        args.Add("Reference", reference);

        var affected = await work.Connection.ExecuteAsync(new CommandDefinition($"""
            UPDATE federation.cameras c
            SET credential_reference = @Reference, updated_by = @ActorId, updated_at = now()
            WHERE c.id = @id
              AND c.deleted_at IS NULL
              AND c.credential_reference IS NULL
              AND ({Scope("camera.update")});
            """, AddActor(args, caller), work.Transaction, cancellationToken: ct));

        if (affected > 0)
        {
            return true;
        }

        // Zero rows: out of scope, already retired, or a reference was already set (the
        // ordinary case on a second call — not an error).
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<bool>(new CommandDefinition($"""
            SELECT EXISTS (
                SELECT 1 FROM federation.cameras c
                WHERE c.id = @id AND c.deleted_at IS NULL AND ({Scope("camera.update")}));
            """, ScopeArgs(id, caller, "camera.update"), cancellationToken: ct));
    }

    // -------------------------------------------------------------------
    // Scope helpers
    // -------------------------------------------------------------------

    // The dual-dimension predicate, parameterised by the permission the endpoint requires.
    // Both dimensions are ANDed; each is bypassed only by ITS OWN unscoped flag.
    private static string Scope(string permission) => $"""
        (@UnscopedOrg OR c.organization_unit_id IN (
            SELECT organization_unit_id FROM federation.authorized_org_units(
                p_user_id => @UserId, p_api_key_id => @ApiKeyId, p_permission => @Perm)))
        AND (@UnscopedGeo OR {SiteArea} IN (
            SELECT geographic_area_id FROM federation.authorized_geographic_areas(
                p_user_id => @UserId, p_api_key_id => @ApiKeyId, p_permission => @Perm)))
        """;

    private static DynamicParameters ScopeArgs(Guid id, CallerContext caller, string permission)
    {
        var p = new DynamicParameters();
        p.Add("id", id);
        p.Add("UserId", caller.UserId);
        p.Add("ApiKeyId", caller.ApiKeyId);
        p.Add("Perm", permission);
        p.Add("UnscopedOrg", caller.IsUnscopedFor(permission));
        p.Add("UnscopedGeo", caller.IsUnscopedForGeography(permission));
        return p;
    }

    private static DynamicParameters AddActor(DynamicParameters p, CallerContext caller)
    {
        p.Add("ActorId", caller.UserId);
        return p;
    }

    /// <summary>
    /// Verifies the caller may place a camera in the given unit and/or geographic area — both
    /// inside their authorised sets for the permission — and that the area exists and is ACTIVE.
    /// Throws <see cref="ForbiddenException"/> (endpoint → 404, never disclose the id) or
    /// <see cref="InvalidReferenceException"/> (endpoint → 400) as appropriate.
    /// </summary>
    private static async Task RequirePlacementAsync(
        UnitOfWork work, CallerContext caller, string permission,
        Guid? organizationUnitId, Guid? geographicAreaId, CancellationToken ct)
    {
        if (geographicAreaId is { } areaId)
        {
            var active = await work.Connection.ExecuteScalarAsync<bool>(new CommandDefinition("""
                SELECT EXISTS (SELECT 1 FROM federation.geographic_areas
                               WHERE id = @areaId AND status = 'ACTIVE');
                """, new { areaId }, work.Transaction, cancellationToken: ct));
            if (!active)
            {
                throw new InvalidReferenceException("geographicAreaId", "does not exist or is not ACTIVE");
            }
        }

        var orgOk = organizationUnitId is null || caller.IsUnscopedFor(permission);
        var geoOk = geographicAreaId is null || caller.IsUnscopedForGeography(permission);

        if (orgOk && geoOk)
        {
            return;
        }

        var permitted = await work.Connection.ExecuteScalarAsync<bool>(new CommandDefinition("""
            SELECT
                (@OrgOk OR @OrgUnit::uuid IS NULL OR EXISTS (
                    SELECT 1 FROM federation.authorized_org_units(
                        p_user_id => @UserId, p_api_key_id => @ApiKeyId, p_permission => @Perm)
                    WHERE organization_unit_id = @OrgUnit))
                AND
                (@GeoOk OR @GeographicAreaId::uuid IS NULL OR EXISTS (
                    SELECT 1 FROM federation.authorized_geographic_areas(
                        p_user_id => @UserId, p_api_key_id => @ApiKeyId, p_permission => @Perm)
                    WHERE geographic_area_id = @GeographicAreaId));
            """, new
        {
            caller.UserId, caller.ApiKeyId, Perm = permission,
            OrgOk = orgOk, GeoOk = geoOk,
            OrgUnit = organizationUnitId, GeographicAreaId = geographicAreaId,
        }, work.Transaction, cancellationToken: ct));

        if (!permitted)
        {
            throw new ForbiddenException(permission);
        }
    }

    private const string InsertColumns = """
        camera_code, name, organization_unit_id, geographic_area_id, manufacturer, model, camera_type,
        serial_number, latitude, longitude, altitude, mounting_height, azimuth, tilt,
        horizontal_fov, vertical_fov, effective_range, ip_address, port, protocol,
        vms_id, stream_reference, stream_preference, native_hls_url, native_webrtc_url,
        credential_reference, installation_date, record_events,
        operational_status, connectivity_status, maintenance_status
        """;

    private const string InsertValues = """
        @Code, @Name, @OrganizationUnitId, @GeographicAreaId, @Manufacturer, @Model, @CameraType,
        @SerialNumber, @Latitude, @Longitude, @Altitude, @MountingHeight, @Azimuth, @Tilt,
        @HorizontalFov, @VerticalFov, @EffectiveRange, @IpAddress::inet, @Port, @Protocol,
        @VmsId, @StreamReference, @StreamPreference, @NativeHlsUrl, @NativeWebrtcUrl,
        @CredentialReference, @InstallationDate, @RecordEvents,
        @OperationalStatus, @ConnectivityStatus, @MaintenanceStatus
        """;

    private static object WriteArgs(Camera cam, CallerContext caller) => new
    {
        cam.Id, cam.Code, cam.Name, cam.OrganizationUnitId, cam.GeographicAreaId,
        cam.Manufacturer, cam.Model, cam.CameraType, cam.SerialNumber,
        cam.Latitude, cam.Longitude, cam.Altitude, cam.MountingHeight,
        cam.Azimuth, cam.Tilt, cam.HorizontalFov, cam.VerticalFov, cam.EffectiveRange,
        cam.IpAddress, cam.Port, cam.Protocol, cam.VmsId, cam.StreamReference,
        cam.StreamPreference, cam.NativeHlsUrl, cam.NativeWebrtcUrl,
        cam.CredentialReference,
        InstallationDate = cam.InstallationDate is { } d ? d.ToDateTime(TimeOnly.MinValue) : (DateTime?)null,
        cam.RecordEvents,
        cam.OperationalStatus, cam.ConnectivityStatus, cam.MaintenanceStatus,
        ActorId = caller.UserId,
        // Scope parameters for the UPDATE guard (unused by INSERT).
        caller.UserId, caller.ApiKeyId, Perm = "camera.update",
        UnscopedOrg = caller.IsUnscopedFor("camera.update"),
        UnscopedGeo = caller.IsUnscopedForGeography("camera.update"),
    };

    private sealed class CameraRow
    {
        public Guid Id { get; init; }
        public string Code { get; init; } = "";
        public string Name { get; init; } = "";
        public Guid OrganizationUnitId { get; init; }
        public Guid GeographicAreaId { get; init; }
        public string? Manufacturer { get; init; }
        public string? Model { get; init; }
        public string CameraType { get; init; } = "";
        public string? SerialNumber { get; init; }
        public double Latitude { get; init; }
        public double Longitude { get; init; }
        public double? Altitude { get; init; }
        public double? MountingHeight { get; init; }
        public double? Azimuth { get; init; }
        public double? Tilt { get; init; }
        public double? HorizontalFov { get; init; }
        public double? VerticalFov { get; init; }
        public double? EffectiveRange { get; init; }
        public string? IpAddress { get; init; }
        public int? Port { get; init; }
        public string? Protocol { get; init; }
        public Guid? VmsId { get; init; }
        public string? StreamReference { get; init; }
        public string StreamPreference { get; init; } = CameraVocab.StreamPreferenceRtsp;
        public string? NativeHlsUrl { get; init; }
        public string? NativeWebrtcUrl { get; init; }
        public string? CredentialReference { get; init; }
        public DateTime? InstallationDate { get; init; }
        public bool RecordEvents { get; init; } = true;
        public string OperationalStatus { get; init; } = "";
        public string ConnectivityStatus { get; init; } = "";
        public string MaintenanceStatus { get; init; } = "";
        public DateTimeOffset? LastSeenAt { get; init; }
        public DateTimeOffset? LastHealthCheckAt { get; init; }
        public DateTimeOffset? DeletedAt { get; init; }

        public Camera ToDomain() => new()
        {
            Id = Id,
            Code = Code,
            Name = Name,
            OrganizationUnitId = OrganizationUnitId,
            GeographicAreaId = GeographicAreaId,
            Manufacturer = Manufacturer,
            Model = Model,
            CameraType = CameraType,
            SerialNumber = SerialNumber,
            Latitude = Latitude,
            Longitude = Longitude,
            Altitude = Altitude,
            MountingHeight = MountingHeight,
            Azimuth = Azimuth,
            Tilt = Tilt,
            HorizontalFov = HorizontalFov,
            VerticalFov = VerticalFov,
            EffectiveRange = EffectiveRange,
            IpAddress = IpAddress,
            Port = Port,
            Protocol = Protocol,
            VmsId = VmsId,
            StreamReference = StreamReference,
            StreamPreference = StreamPreference,
            NativeHlsUrl = NativeHlsUrl,
            NativeWebrtcUrl = NativeWebrtcUrl,
            CredentialReference = CredentialReference,
            InstallationDate = InstallationDate is { } d ? DateOnly.FromDateTime(d) : null,
            RecordEvents = RecordEvents,
            OperationalStatus = OperationalStatus,
            ConnectivityStatus = ConnectivityStatus,
            MaintenanceStatus = MaintenanceStatus,
            LastSeenAt = LastSeenAt,
            LastHealthCheckAt = LastHealthCheckAt,
            DeletedAt = DeletedAt,
        };
    }
}

/// <summary>The little a live streaming request needs to know to proxy a camera's feed —
/// <see cref="CameraRepository.GetStreamRoutingAsync"/>'s result. Never includes secret
/// material, only the reference to resolve one if needed.</summary>
public sealed record CameraStreamRouting
{
    public string StreamPreference { get; init; } = CameraVocab.StreamPreferenceRtsp;
    public string? NativeHlsUrl { get; init; }
    public string? NativeWebrtcUrl { get; init; }
    public string? CredentialReference { get; init; }
}

/// <summary>A geographic bounding box: an inclusive lon/lat rectangle.</summary>
public readonly record struct BoundingBox(double MinLon, double MinLat, double MaxLon, double MaxLat);

/// <summary>Everything <see cref="CameraRepository.ListAsync"/> may filter and page on.</summary>
public readonly record struct CameraQuery(
    int Limit,
    string? Cursor,
    Guid? CursorId,
    bool IncludeRetired,
    Guid? OrganizationUnitId,
    Guid? GeographicAreaId,
    string? CameraType,
    string? OperationalStatus,
    string? ConnectivityStatus,
    string? MaintenanceStatus,
    string? Query,
    BoundingBox? Bbox);

/// <summary>One row of <see cref="CameraRepository.GetAgeingInfrastructureAsync"/>'s internal
/// bucket-count query.</summary>
public sealed record AgeBucketRow
{
    public string Bucket { get; init; } = "";
    public int Count { get; init; }
}

/// <summary>One of the oldest cameras with a known installation date — the priority-attention
/// list <see cref="CameraRepository.GetAgeingInfrastructureAsync"/> returns alongside the bucket
/// counts.</summary>
public sealed record AgeingCameraRow
{
    public Guid Id { get; init; }
    public string CameraCode { get; init; } = "";
    public string Name { get; init; } = "";
    public DateTime InstallationDate { get; init; }
    public int AgeYears { get; init; }
    public string MaintenanceStatus { get; init; } = "";
}

/// <summary><see cref="CameraRepository.GetAgeingInfrastructureAsync"/>'s result: bucket counts
/// plus the oldest cameras with a known installation date.</summary>
public sealed record AgeingInfrastructureRow(
    int UnderThreeYears, int ThreeToFiveYears, int FiveToTenYears, int TenPlusYears,
    int UnknownInstallationDate, IReadOnlyList<AgeingCameraRow> OldestCameras);

/// <summary>
/// A resolved partial update: the <c>SET</c> fragments to apply and their parameter values.
/// </summary>
/// <remarks>
/// Built in the API layer from the raw request JSON so that "field absent" (leave alone) and
/// "field present and null" (clear it) stay distinguishable — a plain nullable record cannot
/// express the difference. The repository only applies what it is handed.
/// </remarks>
public sealed class CameraPatch
{
    private readonly List<string> _assignments = [];

    public IReadOnlyList<string> Assignments => _assignments;

    /// <summary><c>parameter name → value</c> for the fragments in <see cref="Assignments"/>.</summary>
    public Dictionary<string, object?> Parameters { get; } = [];

    /// <summary>Set when the patch moves the camera to a different owning unit.</summary>
    public Guid? NewOrganizationUnitId { get; private set; }

    /// <summary>Set when the patch moves the camera to a different geographic area.</summary>
    public Guid? NewGeographicAreaId { get; private set; }

    /// <summary>
    /// Records <c>column = @param</c> with its value. <paramref name="column"/> is a literal.
    /// <paramref name="cast"/> is an optional literal Postgres cast (e.g. <c>"::inet"</c>) appended
    /// to the parameter reference — needed for columns, like <c>ip_address</c>, whose type has no
    /// implicit assignment cast from the CLR type Dapper binds the value as.
    /// </summary>
    public void Set(string column, string parameter, object? value, string? cast = null)
    {
        _assignments.Add($"{column} = @{parameter}{cast}");
        Parameters[parameter] = value;

        if (column == "organization_unit_id" && value is Guid org)
        {
            NewOrganizationUnitId = org;
        }
        else if (column == "geographic_area_id" && value is Guid area)
        {
            NewGeographicAreaId = area;
        }
    }
}
