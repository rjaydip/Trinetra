using System.Reflection;
using Microsoft.AspNetCore.Http.HttpResults;
using Shouldly;
using Trinetra.Federation.Api.Contracts;
using Trinetra.Federation.Api.Endpoints;
using Trinetra.Federation.Core.Model;

namespace Trinetra.UnitTests;

/// <summary>
/// <c>cameras.record_events</c> end to end through the write-request build step: a plain
/// operator-set flag, defaulting true when the caller omits it, carried through unchanged
/// otherwise. Not derived from any other field.
/// </summary>
public sealed class CameraRecordEventsMappingTests
{
    // TryBuild is private -- reflection keeps the test from forcing an accessibility change onto
    // production code, same approach as CameraCursorTests.
    private static readonly MethodInfo BuildMethod = typeof(CameraEndpoints)
        .GetMethod("TryBuild", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new MissingMethodException(nameof(CameraEndpoints), "TryBuild");

    private static (bool Ok, Camera? Camera) Build(CameraWriteRequest request)
    {
        var args = new object?[] { request, Guid.NewGuid(), null, null };
        var ok = (bool)BuildMethod.Invoke(null, args)!;
        return (ok, (Camera?)args[2]);
    }

    private static CameraWriteRequest ValidRequest(bool? recordEvents = null) => recordEvents is { } re
        ? new CameraWriteRequest(
            "CAM-001", "Front Gate", Guid.NewGuid(), Guid.NewGuid(), "FIXED", 23.02, 72.57,
            RecordEvents: re)
        : new CameraWriteRequest(
            "CAM-001", "Front Gate", Guid.NewGuid(), Guid.NewGuid(), "FIXED", 23.02, 72.57);

    [Fact]
    public void Omitted_defaults_to_true()
    {
        var (ok, camera) = Build(ValidRequest());

        ok.ShouldBeTrue();
        camera!.RecordEvents.ShouldBeTrue();
    }

    [Fact]
    public void Explicit_false_is_carried_through()
    {
        var (ok, camera) = Build(ValidRequest(false));

        ok.ShouldBeTrue();
        camera!.RecordEvents.ShouldBeFalse();
    }

    [Fact]
    public void Explicit_true_is_carried_through()
    {
        var (ok, camera) = Build(ValidRequest(true));

        ok.ShouldBeTrue();
        camera!.RecordEvents.ShouldBeTrue();
    }

    [Fact]
    public void Domain_model_defaults_to_true_independent_of_the_request_dto()
    {
        // The column and the domain default are true; a caller building a Camera directly
        // (bulk-import row construction, tests, …) without touching RecordEvents at all must
        // still land on the same default as the database column.
        var camera = new Camera
        {
            Id = Guid.NewGuid(), Code = "CAM-002", Name = "Back Gate",
            OrganizationUnitId = Guid.NewGuid(), GeographicAreaId = Guid.NewGuid(),
            CameraType = "FIXED", Latitude = 0, Longitude = 0,
        };

        camera.RecordEvents.ShouldBeTrue();
    }
}
