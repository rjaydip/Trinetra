using System.Reflection;
using System.Text.Json;
using Shouldly;
using Trinetra.Federation.Api.Contracts;
using Trinetra.Federation.Api.Endpoints;
using Trinetra.Federation.Storage.Repositories;

namespace Trinetra.UnitTests;

/// <summary>
/// Finding 10-M8: <c>GeoJsonFeature.Properties</c> changed from
/// <c>IReadOnlyDictionary&lt;string, JsonElement&gt;</c> to
/// <c>IReadOnlyDictionary&lt;string, object?&gt;</c> — a raw value serializes to the exact same
/// JSON a <c>JsonElement</c> holding it would, without the per-value serialize-then-reparse
/// round trip <c>JsonSerializer.SerializeToElement</c> did on every property, of every camera, on
/// the map's own render hot path. This proves the wire shape didn't change.
/// </summary>
public sealed class GeoJsonFeatureSerializationTests
{
    [Fact]
    public void PropertiesBag_MixedValueTypes_SerializeAsPlainJsonNotNestedOrEscaped()
    {
        var id = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var feature = new GeoJsonFeature(
            "Feature",
            new GeoJsonGeometry("Point", new[] { 72.58, 23.03 }),
            new Dictionary<string, object?>
            {
                ["cameraId"] = id,
                ["cameraCode"] = "CAM-001",
                ["azimuth"] = 90.5,
                ["effectiveRange"] = (double?)null,
                ["hasCoverage"] = false,
                ["coverageSector"] = new[] { new[] { 1.0, 2.0 }, new[] { 3.0, 4.0 } },
            });

        var json = JsonSerializer.Serialize(feature);
        using var doc = JsonDocument.Parse(json);
        var props = doc.RootElement.GetProperty("properties");

        // Plain values at the leaf — not a string containing escaped JSON, and not a nested
        // object wrapping the value (both of which a double-serialization mistake would produce).
        props.GetProperty("cameraId").GetString().ShouldBe(id.ToString());
        props.GetProperty("cameraCode").GetString().ShouldBe("CAM-001");
        props.GetProperty("azimuth").GetDouble().ShouldBe(90.5);
        props.GetProperty("effectiveRange").ValueKind.ShouldBe(JsonValueKind.Null);
        props.GetProperty("hasCoverage").GetBoolean().ShouldBeFalse();

        var sector = props.GetProperty("coverageSector");
        sector.ValueKind.ShouldBe(JsonValueKind.Array);
        sector.GetArrayLength().ShouldBe(2);
        sector[0][0].GetDouble().ShouldBe(1.0);

        doc.RootElement.GetProperty("type").GetString().ShouldBe("Feature");
        doc.RootElement.GetProperty("geometry").GetProperty("type").GetString().ShouldBe("Point");
    }

    [Fact]
    public void NullGeometry_SerializesAsJsonNull_PerRfc7946()
    {
        var feature = new GeoJsonFeature(
            "Feature", Geometry: null,
            new Dictionary<string, object?> { ["hasCoverage"] = false });

        var json = JsonSerializer.Serialize(feature);
        using var doc = JsonDocument.Parse(json);

        doc.RootElement.GetProperty("geometry").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    private static readonly MethodInfo BuildCameraProperties = typeof(GisEndpoints)
        .GetMethod("BuildCameraProperties", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new MissingMethodException(nameof(GisEndpoints), "BuildCameraProperties");

    private static Dictionary<string, object?> Invoke(GisCameraRow row, bool withSectors) =>
        (Dictionary<string, object?>)BuildCameraProperties.Invoke(null, [row, withSectors])!;

    private static GisCameraRow SampleRow(bool withOptics) => new()
    {
        Id = Guid.Parse("22222222-2222-2222-2222-222222222222"),
        Code = "CAM-002",
        Name = "Gate Camera",
        OrganizationUnitId = Guid.Parse("33333333-3333-3333-3333-333333333333"),
        Manufacturer = "Acme",
        CameraType = "PTZ",
        Latitude = 23.03,
        Longitude = 72.58,
        Azimuth = withOptics ? 90.0 : null,
        HorizontalFov = withOptics ? 60.0 : null,
        EffectiveRange = withOptics ? 50.0 : null,
        OperationalStatus = "ACTIVE",
        ConnectivityStatus = "ONLINE",
        MaintenanceStatus = "NONE",
    };

    /// <summary>
    /// BA review: the two tests above prove a hand-built dictionary serializes correctly, but a
    /// copy-paste key-name typo introduced editing <c>GisEndpoints.BuildCameraProperties</c>
    /// itself — the method actually on the render hot path — would never be seen by either of
    /// them. This calls the extracted method directly and pins its exact key set.
    /// </summary>
    [Fact]
    public void BuildCameraProperties_WithoutOptics_ProducesExpectedKeysAndValues_NoCoverageSector()
    {
        var props = Invoke(SampleRow(withOptics: false), withSectors: true);

        props.Keys.ShouldBe(
            new[]
            {
                "cameraId", "cameraCode", "name", "organizationUnitId", "manufacturer",
                "cameraType", "operationalStatus", "connectivityStatus", "maintenanceStatus",
                "azimuth", "horizontalFov", "effectiveRange", "hasCoverage",
            },
            ignoreOrder: true);

        props["cameraId"].ShouldBe(Guid.Parse("22222222-2222-2222-2222-222222222222"));
        props["cameraCode"].ShouldBe("CAM-002");
        props["hasCoverage"].ShouldBe(false);
        props.ShouldNotContainKey("coverageSector");
        props.ShouldNotContainKey("coverageEstimated");
    }

    [Fact]
    public void BuildCameraProperties_WithOpticsAndSectorsRequested_AddsCoverageSector()
    {
        var props = Invoke(SampleRow(withOptics: true), withSectors: true);

        props["hasCoverage"].ShouldBe(true);
        props.ShouldContainKey("coverageSector");
        props["coverageEstimated"].ShouldBe(true);
    }

    [Fact]
    public void BuildCameraProperties_WithOpticsButSectorsNotRequested_OmitsCoverageSector()
    {
        var props = Invoke(SampleRow(withOptics: true), withSectors: false);

        props["hasCoverage"].ShouldBe(true);
        props.ShouldNotContainKey("coverageSector");
        props.ShouldNotContainKey("coverageEstimated");
    }
}
