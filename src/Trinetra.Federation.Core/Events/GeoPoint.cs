namespace Trinetra.Federation.Core.Events;

/// <summary>A WGS84 point. Validated on construction — an out-of-range coordinate silently
/// breaks every spatial correlation query, so it is rejected at the boundary.</summary>
public readonly record struct GeoPoint
{
    public GeoPoint(double latitude, double longitude, double? altitude = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(latitude, -90);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(latitude, 90);
        ArgumentOutOfRangeException.ThrowIfLessThan(longitude, -180);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(longitude, 180);

        Latitude = latitude;
        Longitude = longitude;
        Altitude = altitude;
    }

    public double Latitude { get; }
    public double Longitude { get; }
    public double? Altitude { get; }
}
