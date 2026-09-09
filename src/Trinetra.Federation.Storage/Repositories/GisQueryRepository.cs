using Dapper;
using Npgsql;

namespace Trinetra.Federation.Storage.Repositories;

/// <summary>One camera as the GIS map source needs it: a point, a bearing and the sector inputs.</summary>
public sealed record GisCameraRow
{
    public Guid Id { get; init; }
    public string Code { get; init; } = "";
    public string Name { get; init; } = "";
    public Guid OrganizationUnitId { get; init; }
    public string? Manufacturer { get; init; }
    public string CameraType { get; init; } = "";
    public double Latitude { get; init; }
    public double Longitude { get; init; }
    public double? Azimuth { get; init; }
    public double? HorizontalFov { get; init; }
    public double? EffectiveRange { get; init; }
    public string OperationalStatus { get; init; } = "";
    public string ConnectivityStatus { get; init; } = "";
    public string MaintenanceStatus { get; init; } = "";
}

/// <summary>A row of the coverage aggregate: a bucket key and its camera count.</summary>
public sealed record CoverageBucketRow
{
    public string Dimension { get; init; } = "";
    public string Key { get; init; } = "";
    public long Count { get; init; }
}

/// <summary>
/// Read models for the GIS layer, scoped by the GIS permissions.
/// </summary>
/// <remarks>
/// Separate from <see cref="CameraRepository"/> because the map is gated on <c>gis.read</c> /
/// <c>gis.coverage.read</c> rather than <c>camera.read</c>, and returns a deliberately narrow
/// projection — enough to draw a marker and its coverage wedge, nothing operational.
/// </remarks>
public sealed class GisQueryRepository
{
    private const string GeoArea = "c.geographic_area_id";

    private readonly NpgsqlDataSource _dataSource;

    public GisQueryRepository(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    /// <summary>Every in-scope, live camera inside the bounding box, with the optional filters applied.</summary>
    public async Task<PagedRows<GisCameraRow>> FeedAsync(
        BoundingBox bbox, GisFeedFilter filter, CallerContext caller, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require("gis.read");

        await using var c = await _dataSource.OpenConnectionAsync(ct);

        var rows = (await c.QueryAsync<GisCameraRow, long, (GisCameraRow R, long T)>(
            new CommandDefinition($"""
                SELECT c.id, c.camera_code AS code, c.name, c.organization_unit_id,
                       c.manufacturer, c.camera_type, c.latitude, c.longitude,
                       c.azimuth, c.horizontal_fov, c.effective_range,
                       c.operational_status, c.connectivity_status, c.maintenance_status,
                       count(*) OVER() AS total_count
                FROM federation.cameras c
                WHERE c.longitude BETWEEN @MinLon AND @MaxLon
                  AND c.latitude BETWEEN @MinLat AND @MaxLat
                  AND (@IncludeRetired OR c.deleted_at IS NULL)
                  AND (@OrganizationUnitId::uuid IS NULL
                       OR c.organization_unit_id IN (
                           SELECT id FROM federation.org_unit_descendants(@OrganizationUnitId)))
                  AND (@OperationalStatus::text IS NULL OR c.operational_status = @OperationalStatus)
                  AND (@MaintenanceStatus::text IS NULL OR c.maintenance_status = @MaintenanceStatus)
                  AND ({Scope("gis.read")})
                ORDER BY c.camera_code, c.id
                LIMIT @Limit OFFSET @Offset;
                """, FeedArgs(bbox, filter, caller, "gis.read"), cancellationToken: ct),
            (r, t) => (r, t), splitOn: "total_count")).ToList();

        return new PagedRows<GisCameraRow>(
            [.. rows.Select(x => x.R)], rows.Count > 0 ? (int)rows[0].T : 0);
    }

    /// <summary>One camera's point and coverage inputs, or null if the caller cannot reach it.</summary>
    public async Task<GisCameraRow?> PointAsync(Guid id, CallerContext caller, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require("gis.coverage.read");

        await using var c = await _dataSource.OpenConnectionAsync(ct);

        return await c.QuerySingleOrDefaultAsync<GisCameraRow>(new CommandDefinition($"""
            SELECT c.id, c.camera_code AS code, c.name, c.organization_unit_id,
                   c.manufacturer, c.camera_type, c.latitude, c.longitude,
                   c.azimuth, c.horizontal_fov, c.effective_range,
                   c.operational_status, c.connectivity_status, c.maintenance_status
            FROM federation.cameras c
            WHERE c.id = @id AND c.deleted_at IS NULL AND ({Scope("gis.coverage.read")});
            """, ScopeArgs(id, caller, "gis.coverage.read"), cancellationToken: ct));
    }

    /// <summary>Camera counts by status/type/owner within an area or bounding box. No geometry.</summary>
    public async Task<IReadOnlyList<CoverageBucketRow>> SummaryAsync(
        Guid? geographicAreaId, BoundingBox? bbox, Guid? organizationUnitId,
        CallerContext caller, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require("gis.coverage.read");

        await using var c = await _dataSource.OpenConnectionAsync(ct);

        var args = ScopeArgs(Guid.Empty, caller, "gis.coverage.read");
        args.Add("GeographicAreaId", geographicAreaId);
        args.Add("OrganizationUnitId", organizationUnitId);
        args.Add("MinLon", bbox?.MinLon);
        args.Add("MaxLon", bbox?.MaxLon);
        args.Add("MinLat", bbox?.MinLat);
        args.Add("MaxLat", bbox?.MaxLat);

        var rows = await c.QueryAsync<CoverageBucketRow>(new CommandDefinition($"""
            WITH scoped AS (
                SELECT c.operational_status, c.maintenance_status,
                       c.organization_unit_id::text AS org,
                       COALESCE(c.manufacturer, 'UNKNOWN') AS manufacturer,
                       (c.azimuth IS NOT NULL AND c.horizontal_fov IS NOT NULL
                        AND c.effective_range IS NOT NULL) AS has_coverage
                FROM federation.cameras c
                WHERE c.deleted_at IS NULL
                  AND (@GeographicAreaId::uuid IS NULL
                       OR {GeoArea} IN (
                           SELECT id FROM federation.geographic_area_descendants(@GeographicAreaId)))
                  AND (@OrganizationUnitId::uuid IS NULL
                       OR c.organization_unit_id IN (
                           SELECT id FROM federation.org_unit_descendants(@OrganizationUnitId)))
                  AND (@MinLon::numeric IS NULL OR (
                           c.longitude BETWEEN @MinLon AND @MaxLon
                           AND c.latitude BETWEEN @MinLat AND @MaxLat))
                  AND ({Scope("gis.coverage.read")})
            )
            SELECT 'operationalStatus' AS dimension, operational_status AS key, count(*) AS count
            FROM scoped GROUP BY operational_status
            UNION ALL
            SELECT 'maintenanceStatus', maintenance_status, count(*) FROM scoped GROUP BY maintenance_status
            UNION ALL
            SELECT 'manufacturer', manufacturer, count(*) FROM scoped GROUP BY manufacturer
            UNION ALL
            SELECT 'organizationUnit', org, count(*) FROM scoped GROUP BY org
            UNION ALL
            SELECT 'coverageParams', CASE WHEN has_coverage THEN 'present' ELSE 'absent' END, count(*)
            FROM scoped GROUP BY has_coverage;
            """, args, cancellationToken: ct));

        return [.. rows];
    }

    // Same dual-dimension predicate as CameraRepository, parameterised by permission.
    private static string Scope(string permission) => $"""
        (@UnscopedOrg OR c.organization_unit_id IN (
            SELECT organization_unit_id FROM federation.authorized_org_units(
                p_user_id => @UserId, p_api_key_id => @ApiKeyId, p_permission => @Perm)))
        AND (@UnscopedGeo OR {GeoArea} IN (
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

    private static DynamicParameters FeedArgs(
        BoundingBox bbox, GisFeedFilter filter, CallerContext caller, string permission)
    {
        var p = ScopeArgs(Guid.Empty, caller, permission);
        p.Add("MinLon", bbox.MinLon);
        p.Add("MinLat", bbox.MinLat);
        p.Add("MaxLon", bbox.MaxLon);
        p.Add("MaxLat", bbox.MaxLat);
        p.Add("IncludeRetired", filter.IncludeRetired);
        p.Add("OrganizationUnitId", filter.OrganizationUnitId);
        p.Add("OperationalStatus", filter.OperationalStatus);
        p.Add("MaintenanceStatus", filter.MaintenanceStatus);
        p.Add("Limit", filter.Limit);
        p.Add("Offset", filter.Offset);
        return p;
    }
}

/// <summary>Optional narrowing for the GIS camera feed, plus its <c>LIMIT</c>/<c>OFFSET</c> window.</summary>
public readonly record struct GisFeedFilter(
    int Limit,
    bool IncludeRetired,
    Guid? OrganizationUnitId,
    string? OperationalStatus,
    string? MaintenanceStatus,
    int Offset = 0);
