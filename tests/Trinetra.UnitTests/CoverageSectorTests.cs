using Shouldly;
using Trinetra.Federation.Core.Geo;

namespace Trinetra.UnitTests;

/// <summary>
/// The coverage-sector geometry helper. Pure trigonometry — no I/O, no database — so it is
/// tested directly. Model 1's GIS layer renders one of these per camera in a viewport.
/// </summary>
public sealed class CoverageSectorTests
{
    [Fact]
    public void CanCompute_requires_all_three_optics_parameters()
    {
        CoverageSector.CanCompute(90, 60, 120).ShouldBeTrue();
        CoverageSector.CanCompute(null, 60, 120).ShouldBeFalse();
        CoverageSector.CanCompute(90, null, 120).ShouldBeFalse();
        CoverageSector.CanCompute(90, 60, null).ShouldBeFalse();
    }

    [Fact]
    public void Ring_is_a_closed_polygon_starting_and_ending_at_the_camera()
    {
        var ring = CoverageSector.Ring(23.0225, 72.5714, azimuth: 90, horizontalFov: 60, effectiveRange: 150);

        ring.Count.ShouldBeGreaterThan(3);
        ring[0].ShouldBe(ring[^1]);
        ring[0][0].ShouldBe(72.5714, 1e-9);
        ring[0][1].ShouldBe(23.0225, 1e-9);
    }

    [Fact]
    public void Ring_coordinates_are_lon_lat_order_and_near_the_camera()
    {
        var ring = CoverageSector.Ring(0, 0, azimuth: 0, horizontalFov: 90, effectiveRange: 1000);

        foreach (var point in ring)
        {
            point.Length.ShouldBe(2);
            Math.Abs(point[0]).ShouldBeLessThan(0.02);   // ~1 km is well under 0.02°
            Math.Abs(point[1]).ShouldBeLessThan(0.02);
        }
    }

    [Fact]
    public void Ring_bearing_due_north_moves_the_arc_north_of_the_camera()
    {
        var ring = CoverageSector.Ring(0, 0, azimuth: 0, horizontalFov: 20, effectiveRange: 500);

        // Every arc vertex (all but the apex endpoints) should be north — positive latitude.
        ring.Skip(1).SkipLast(1).ShouldAllBe(p => p[1] > 0);
    }

    [Fact]
    public void Ring_wider_fov_produces_more_vertices()
    {
        var narrow = CoverageSector.Ring(0, 0, 90, 10, 100);
        var wide = CoverageSector.Ring(0, 0, 90, 180, 100);

        wide.Count.ShouldBeGreaterThan(narrow.Count);
    }

    [Fact]
    public void Ring_is_empty_when_fov_or_range_is_not_positive()
    {
        CoverageSector.Ring(0, 0, 90, 0, 100).ShouldBeEmpty();
        CoverageSector.Ring(0, 0, 90, 60, 0).ShouldBeEmpty();
    }
}
