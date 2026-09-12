using System.Globalization;
using Microsoft.AspNetCore.Http.HttpResults;
using Trinetra.Federation.Api.Auth;
using Trinetra.Federation.Api.Contracts;
using Trinetra.Federation.Api.OpenApi;
using Trinetra.Federation.Core.Geo;
using Trinetra.Federation.Storage.Repositories;

namespace Trinetra.Federation.Api.Endpoints;

/// <summary>
/// The GIS map source and coverage analysis over the camera registry.
/// </summary>
/// <remarks>
/// <para>
/// Coverage sectors are computed on read from <c>azimuth</c> + <c>horizontalFov</c> +
/// <c>effectiveRange</c>. They are an <b>estimate</b>: terrain, buildings, lighting and lens
/// characteristics are not modelled (<c>CLAUDE.md</c>). Every rendered sector carries the
/// disclaimer.
/// </para>
/// <para>
/// Nothing here is stored as geometry and nothing needs PostGIS. Coverage-gap analysis does —
/// <c>GET /gis/gaps</c> returns 501 until the version that introduces it.
/// </para>
/// </remarks>
public static class GisEndpoints
{
    /// <summary>Widest bbox span accepted by the feed, so one call cannot pull the whole estate.</summary>
    private const double MaxBboxDegrees = 2.0;

    /// <summary>
    /// Hard cap on a single non-paginated feed request. Above this the response is trimmed and
    /// carries <c>X-Result-Capped: true</c>; page through with <c>page</c> / <c>pageSize</c> for
    /// the rest (finding 10-M2 — the old behaviour silently dropped everything past this).
    /// </summary>
    private const int FeedLimit = 5000;

    private const string GeoJsonMediaType = "application/geo+json";

    public static void MapGisEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/gis/cameras", FeedAsync)
           .RequireAuthorization().WithTags(ApiTags.Gis).RequirePermission("gis.read")
           .WithSummary("Camera map source (GeoJSON)")
           .WithDescription(
               "A GeoJSON `FeatureCollection`, one `Point` feature per in-scope live camera "
               + "inside `bbox` (**required**, `minLon,minLat,maxLon,maxLat`, span at most "
               + $"{MaxBboxDegrees}° each way). Feature properties carry the camera id, code, "
               + "owner, type, the three status axes and the sector inputs. Pass "
               + "`includeSectors=true` to attach each camera's computed coverage `Polygon`. "
               + "Narrow with `organizationUnitId`, `operationalStatus`, `maintenanceStatus`.\n\n"
               + $"**The body is always a `FeatureCollection`.** Without `page` it is capped at "
               + $"{FeedLimit} features — when the cap trims the result the response carries "
               + "`X-Result-Capped: true`, and `X-Total-Count` always gives the full match "
               + "count. Send `page` (1-based) and optionally `pageSize` to walk the whole set; "
               + "`X-Page` / `X-Page-Size` echo the window.");

        app.MapGet("/api/v1/cameras/{id:guid}/coverage", CoverageAsync)
           .RequireAuthorization().WithTags(ApiTags.Gis).RequirePermission("gis.coverage.read")
           .WithSummary("One camera's estimated coverage sector")
           .WithDescription(
               "A GeoJSON `Feature` whose `Polygon` is the estimated ground area the camera can "
               + "see: apex at the camera, bisector along its azimuth, width `horizontalFov`, "
               + "radius `effectiveRange`, with `estimated: true` and the modelling disclaimer. "
               + "When any of those three optics is missing the Feature still returns with "
               + "`geometry: null` and `properties.hasCoverage: false`. `404` only if the camera "
               + "is absent or out of the caller's scope.");

        app.MapGet("/api/v1/gis/coverage", SummaryAsync)
           .RequireAuthorization().WithTags(ApiTags.Gis).RequirePermission("gis.coverage.read")
           .WithSummary("Coverage aggregate — counts, no geometry")
           .WithDescription(
               "Camera counts over an area (`geographicAreaId`, includes descendants) or a "
               + "`bbox`, one of which is required, bucketed by operational status, maintenance "
               + "status, manufacturer, owning unit, and whether the sector inputs are present. "
               + "Optionally narrowed by `organizationUnitId`. No merged coverage polygon — that "
               + "needs a spatial engine.");

        app.MapGet("/api/v1/gis/gaps", GapsAsync)
           .RequireAuthorization().WithTags(ApiTags.Gis).RequirePermission("gis.coverage.read")
           .WithSummary("Coverage-gap layer — not available yet")
           .WithDescription(
               "Planned for the release that introduces PostGIS and administrative boundary "
               + "polygons. Returns 501 until then; the route exists so the contract is stable.");
    }

    // -------------------------------------------------------------------

    private static async Task<Results<JsonHttpResult<GeoJsonFeatureCollection>, ProblemHttpResult>> FeedAsync(
        string? bbox, bool? includeSectors, bool? includeRetired,
        Guid? organizationUnitId, string? operationalStatus, string? maintenanceStatus,
        int? page, int? pageSize,
        GisQueryRepository gis, HttpContext http, CancellationToken ct)
    {
        var caller = CallerContextFactory.From(http);

        if (string.IsNullOrWhiteSpace(bbox))
        {
            return Problem("bbox is required", "Supply bbox as minLon,minLat,maxLon,maxLat.");
        }

        if (!TryParseBbox(bbox, out var box, out var problem))
        {
            return problem!;
        }

        if (box.MaxLon - box.MinLon > MaxBboxDegrees || box.MaxLat - box.MinLat > MaxBboxDegrees)
        {
            return Problem("bbox too large",
                $"Each side may span at most {MaxBboxDegrees}°. Zoom in and request again.");
        }

        var q = new PageQuery(page, pageSize);
        var window = new PageWindow(q.Limit(FeedLimit, FeedLimit), q.Offset(FeedLimit));

        var rows = await gis.FeedAsync(
            box,
            new GisFeedFilter(
                window.Limit, includeRetired ?? false, organizationUnitId,
                Upper(operationalStatus), Upper(maintenanceStatus), window.Offset),
            caller, ct);

        http.Response.Headers["X-Total-Count"] = rows.Total.ToString(CultureInfo.InvariantCulture);
        if (q.Enabled)
        {
            http.Response.Headers["X-Page"] = q.Page!.Value.ToString(CultureInfo.InvariantCulture);
            http.Response.Headers["X-Page-Size"] =
                q.ResolvedPageSize(FeedLimit).ToString(CultureInfo.InvariantCulture);
        }
        else if (rows.Total > rows.Items.Count)
        {
            http.Response.Headers["X-Result-Capped"] = "true";
        }

        var withSectors = includeSectors ?? false;
        var features = new List<GeoJsonFeature>(rows.Items.Count);

        foreach (var r in rows.Items)
        {
            features.Add(new GeoJsonFeature(
                "Feature",
                new GeoJsonGeometry("Point", new[] { r.Longitude, r.Latitude }),
                BuildCameraProperties(r, withSectors)));
        }

        return TypedResults.Json(
            new GeoJsonFeatureCollection("FeatureCollection", features),
            contentType: GeoJsonMediaType);
    }

    private static async Task<Results<JsonHttpResult<GeoJsonFeature>, NotFound>> CoverageAsync(
        Guid id, GisQueryRepository gis, HttpContext http, CancellationToken ct)
    {
        var caller = CallerContextFactory.From(http);

        var camera = await gis.PointAsync(id, caller, ct);
        if (camera is null)
        {
            return TypedResults.NotFound();
        }

        var hasCoverage = CoverageSector.CanCompute(
            camera.Azimuth, camera.HorizontalFov, camera.EffectiveRange);

        var props = new Dictionary<string, object?>
        {
            ["cameraId"] = camera.Id,
            ["cameraCode"] = camera.Code,
            ["azimuth"] = camera.Azimuth,
            ["horizontalFov"] = camera.HorizontalFov,
            ["effectiveRange"] = camera.EffectiveRange,
            ["hasCoverage"] = hasCoverage,
        };

        // A camera the caller can see always returns a Feature (finding 10-L5): a 204 for
        // "in scope but no optics" against a 404 for "absent / out of scope" let a caller probe
        // which ids are real. When the optics are missing, geometry is null.
        GeoJsonGeometry? geometry = null;
        if (hasCoverage)
        {
            var ring = CoverageSector.Ring(
                camera.Latitude, camera.Longitude,
                camera.Azimuth!.Value, camera.HorizontalFov!.Value, camera.EffectiveRange!.Value);
            geometry = new GeoJsonGeometry("Polygon", new[] { ring });
            props["estimated"] = true;
            props["disclaimer"] = CoverageSector.EstimateDisclaimer;
        }

        return TypedResults.Json(
            new GeoJsonFeature("Feature", geometry, props),
            contentType: GeoJsonMediaType);
    }

    private static async Task<Results<Ok<CoverageSummaryResponse>, ProblemHttpResult>> SummaryAsync(
        Guid? geographicAreaId, string? bbox, Guid? organizationUnitId,
        GisQueryRepository gis, HttpContext http, CancellationToken ct)
    {
        var caller = CallerContextFactory.From(http);

        BoundingBox? box = null;
        if (!string.IsNullOrWhiteSpace(bbox))
        {
            if (!TryParseBbox(bbox, out var parsed, out var problem))
            {
                return problem!;
            }

            box = parsed;
        }

        if (geographicAreaId is null && box is null)
        {
            return Problem("area required", "Supply one of geographicAreaId or bbox.");
        }

        var rows = await gis.SummaryAsync(geographicAreaId, box, organizationUnitId, caller, ct);

        var buckets = rows
            .GroupBy(r => r.Dimension)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyDictionary<string, long>)g.ToDictionary(x => x.Key, x => x.Count));

        return TypedResults.Ok(new CoverageSummaryResponse(buckets));
    }

    private static Results<Ok, ProblemHttpResult> GapsAsync() =>
        TypedResults.Problem(
            title: "Coverage-gap analysis is not available yet",
            detail: "Planned for the release that introduces spatial querying and administrative "
                  + "boundary polygons.",
            statusCode: StatusCodes.Status501NotImplemented);

    // -------------------------------------------------------------------

    /// <summary>
    /// The GeoJSON <c>properties</c> bag for one camera in the feed. Extracted so a test can
    /// assert on the actual keys this endpoint builds — a hand-built dictionary in a test proves
    /// serialization is correct (finding 10-M8) but can't catch a typo introduced editing this
    /// method itself, since it would never see the mistake.
    /// </summary>
    private static Dictionary<string, object?> BuildCameraProperties(GisCameraRow r, bool withSectors)
    {
        var props = new Dictionary<string, object?>
        {
            ["cameraId"] = r.Id,
            ["cameraCode"] = r.Code,
            ["name"] = r.Name,
            ["organizationUnitId"] = r.OrganizationUnitId,
            ["manufacturer"] = r.Manufacturer,
            ["cameraType"] = r.CameraType,
            ["operationalStatus"] = r.OperationalStatus,
            ["connectivityStatus"] = r.ConnectivityStatus,
            ["maintenanceStatus"] = r.MaintenanceStatus,
            ["azimuth"] = r.Azimuth,
            ["horizontalFov"] = r.HorizontalFov,
            ["effectiveRange"] = r.EffectiveRange,
            ["hasCoverage"] = CoverageSector.CanCompute(r.Azimuth, r.HorizontalFov, r.EffectiveRange),
        };

        if (withSectors && CoverageSector.CanCompute(r.Azimuth, r.HorizontalFov, r.EffectiveRange))
        {
            var ring = CoverageSector.Ring(
                r.Latitude, r.Longitude, r.Azimuth!.Value, r.HorizontalFov!.Value, r.EffectiveRange!.Value);
            props["coverageSector"] = new[] { ring };
            props["coverageEstimated"] = true;
        }

        return props;
    }

    private static string? Upper(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim().ToUpperInvariant();

    private static ProblemHttpResult Problem(string title, string detail) =>
        TypedResults.Problem(title: title, detail: detail, statusCode: StatusCodes.Status400BadRequest);

    private static bool TryParseBbox(string raw, out BoundingBox box, out ProblemHttpResult? problem)
    {
        box = default;
        problem = null;

        var parts = raw.Split(',');
        if (parts.Length != 4
            || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var minLon)
            || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var minLat)
            || !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var maxLon)
            || !double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var maxLat))
        {
            problem = Problem("invalid bbox", "bbox must be four comma-separated numbers.");
            return false;
        }

        if (minLon >= maxLon || minLat >= maxLat
            || minLon < -180 || maxLon > 180 || minLat < -90 || maxLat > 90)
        {
            problem = Problem("invalid bbox", "bbox is out of range or has min >= max.");
            return false;
        }

        box = new BoundingBox(minLon, minLat, maxLon, maxLat);
        return true;
    }
}
