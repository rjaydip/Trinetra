namespace Trinetra.Federation.Core.Geo;

/// <summary>
/// The estimated ground area a camera can see, as a polygon.
/// </summary>
/// <remarks>
/// <para>
/// A circular sector: apex at the camera, bisector along its compass <c>azimuth</c>, angular
/// width <c>horizontalFov</c>, radius <c>effectiveRange</c> metres. The arc is approximated with
/// a handful of straight segments and each vertex is projected back to WGS84 with the
/// destination-point formula. At ranges under a few kilometres the spherical-earth error is well
/// below the uncertainty already inherent in the estimate.
/// </para>
/// <para>
/// <b>Estimated, not measured</b> (<c>CLAUDE.md</c>): terrain, buildings, lighting, occlusion
/// and lens characteristics are not modelled. Present it as a planning aid.
/// </para>
/// <para>
/// Pure and allocation-modest by design — it runs once per camera in a GIS viewport. No I/O.
/// </para>
/// </remarks>
public static class CoverageSector
{
    private const double EarthRadiusMetres = 6_371_008.8;

    /// <summary>Degrees of arc each straight segment spans. Smaller = smoother, more vertices.</summary>
    private const double SegmentDegrees = 5.0;

    /// <summary>Upper bound on arc vertices, so a 360° "FOV" cannot produce an unbounded ring.</summary>
    private const int MaxArcVertices = 72;

    /// <summary>The disclaimer that must travel with every rendered sector.</summary>
    public const string EstimateDisclaimer =
        "Estimated coverage. Terrain, buildings, lighting and lens characteristics are not "
        + "modelled; treat this as a planning aid, not a guarantee of visibility.";

    /// <summary>Whether the three parameters a sector needs are all present.</summary>
    public static bool CanCompute(double? azimuth, double? horizontalFov, double? effectiveRange) =>
        azimuth is not null && horizontalFov is not null && effectiveRange is not null;

    /// <summary>
    /// Builds the sector polygon as a closed GeoJSON linear ring: <c>[apex, arc…, apex]</c>,
    /// each coordinate <c>[longitude, latitude]</c> in WGS84.
    /// </summary>
    /// <returns>
    /// The ring, or an empty list when <paramref name="horizontalFov"/> or
    /// <paramref name="effectiveRange"/> is not positive (nothing to draw).
    /// </returns>
    public static IReadOnlyList<double[]> Ring(
        double latitude, double longitude,
        double azimuth, double horizontalFov, double effectiveRange)
    {
        if (horizontalFov <= 0 || effectiveRange <= 0)
        {
            return [];
        }

        var half = Math.Min(horizontalFov, 360.0) / 2.0;
        var start = azimuth - half;
        var end = azimuth + half;

        var steps = Math.Clamp((int)Math.Ceiling(horizontalFov / SegmentDegrees), 1, MaxArcVertices);

        var apex = new[] { longitude, latitude };
        var ring = new List<double[]>(steps + 3) { apex };

        for (var i = 0; i <= steps; i++)
        {
            var bearing = start + ((end - start) * i / steps);
            var (lat, lon) = Destination(latitude, longitude, bearing, effectiveRange);
            ring.Add(new[] { lon, lat });
        }

        ring.Add(apex);
        return ring;
    }

    /// <summary>
    /// The point reached by travelling <paramref name="distanceMetres"/> from an origin along a
    /// constant compass <paramref name="bearingDegrees"/> (0 = north, clockwise), on a sphere.
    /// </summary>
    private static (double Lat, double Lon) Destination(
        double latDeg, double lonDeg, double bearingDegrees, double distanceMetres)
    {
        var angular = distanceMetres / EarthRadiusMetres;
        var bearing = DegToRad(bearingDegrees);
        var lat1 = DegToRad(latDeg);
        var lon1 = DegToRad(lonDeg);

        var sinLat1 = Math.Sin(lat1);
        var cosLat1 = Math.Cos(lat1);
        var sinAngular = Math.Sin(angular);
        var cosAngular = Math.Cos(angular);

        var sinLat2 = (sinLat1 * cosAngular) + (cosLat1 * sinAngular * Math.Cos(bearing));
        var lat2 = Math.Asin(Math.Clamp(sinLat2, -1.0, 1.0));

        var y = Math.Sin(bearing) * sinAngular * cosLat1;
        var x = cosAngular - (sinLat1 * sinLat2);
        var lon2 = lon1 + Math.Atan2(y, x);

        return (RadToDeg(lat2), NormaliseLongitude(RadToDeg(lon2)));
    }

    private static double DegToRad(double d) => d * Math.PI / 180.0;

    private static double RadToDeg(double r) => r * 180.0 / Math.PI;

    private static double NormaliseLongitude(double lon) => ((lon + 540.0) % 360.0) - 180.0;
}
