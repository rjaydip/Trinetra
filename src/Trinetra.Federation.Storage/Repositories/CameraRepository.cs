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
        c.vms_id, c.stream_reference, c.credential_reference, c.installation_date,
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

        var row = await c.QuerySingleOrDefaultAsync<CameraRow>(new CommandDefinition($"""
            SELECT {Columns}
            FROM federation.cameras c
            WHERE c.id = @id
              AND ({Scope("camera.read")})
            """, ScopeArgs(id, caller, "camera.read"), cancellationToken: ct));

        return row?.ToDomain();
    }

    /// <summary>One page of the registry, keyset-ordered by <c>camera_code</c>.</summary>
    public async Task<IReadOnlyList<Camera>> ListAsync(
        CameraQuery query, CallerContext caller, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require("camera.read");

        await using var c = await _dataSource.OpenConnectionAsync(ct);

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
              AND (@Cursor::text IS NULL OR c.camera_code > @Cursor)
            ORDER BY c.camera_code
            LIMIT @Limit
            """;

        var args = new DynamicParameters(ScopeArgs(Guid.Empty, caller, "camera.read"));
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
                credential_reference = @CredentialReference, installation_date = @InstallationDate,
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
        vms_id, stream_reference, credential_reference, installation_date,
        operational_status, connectivity_status, maintenance_status
        """;

    private const string InsertValues = """
        @Code, @Name, @OrganizationUnitId, @GeographicAreaId, @Manufacturer, @Model, @CameraType,
        @SerialNumber, @Latitude, @Longitude, @Altitude, @MountingHeight, @Azimuth, @Tilt,
        @HorizontalFov, @VerticalFov, @EffectiveRange, @IpAddress::inet, @Port, @Protocol,
        @VmsId, @StreamReference, @CredentialReference, @InstallationDate,
        @OperationalStatus, @ConnectivityStatus, @MaintenanceStatus
        """;

    private static object WriteArgs(Camera cam, CallerContext caller) => new
    {
        cam.Id, cam.Code, cam.Name, cam.OrganizationUnitId, cam.GeographicAreaId,
        cam.Manufacturer, cam.Model, cam.CameraType, cam.SerialNumber,
        cam.Latitude, cam.Longitude, cam.Altitude, cam.MountingHeight,
        cam.Azimuth, cam.Tilt, cam.HorizontalFov, cam.VerticalFov, cam.EffectiveRange,
        cam.IpAddress, cam.Port, cam.Protocol, cam.VmsId, cam.StreamReference,
        cam.CredentialReference,
        InstallationDate = cam.InstallationDate is { } d ? d.ToDateTime(TimeOnly.MinValue) : (DateTime?)null,
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
        public string? CredentialReference { get; init; }
        public DateTime? InstallationDate { get; init; }
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
            CredentialReference = CredentialReference,
            InstallationDate = InstallationDate is { } d ? DateOnly.FromDateTime(d) : null,
            OperationalStatus = OperationalStatus,
            ConnectivityStatus = ConnectivityStatus,
            MaintenanceStatus = MaintenanceStatus,
            LastSeenAt = LastSeenAt,
            LastHealthCheckAt = LastHealthCheckAt,
            DeletedAt = DeletedAt,
        };
    }
}

/// <summary>A geographic bounding box: an inclusive lon/lat rectangle.</summary>
public readonly record struct BoundingBox(double MinLon, double MinLat, double MaxLon, double MaxLat);

/// <summary>Everything <see cref="CameraRepository.ListAsync"/> may filter and page on.</summary>
public readonly record struct CameraQuery(
    int Limit,
    string? Cursor,
    bool IncludeRetired,
    Guid? OrganizationUnitId,
    Guid? GeographicAreaId,
    string? CameraType,
    string? OperationalStatus,
    string? ConnectivityStatus,
    string? MaintenanceStatus,
    string? Query,
    BoundingBox? Bbox);

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

    /// <summary>Records <c>column = @param</c> with its value. <paramref name="column"/> is a literal.</summary>
    public void Set(string column, string parameter, object? value)
    {
        _assignments.Add($"{column} = @{parameter}");
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
