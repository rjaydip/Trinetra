using System.Reflection;
using Microsoft.AspNetCore.Http.HttpResults;
using Shouldly;
using Trinetra.Federation.Api.Contracts;
using Trinetra.Federation.Api.Endpoints;

namespace Trinetra.UnitTests;

/// <summary>
/// The pre-save camera reachability endpoint's pure request validation, and the shape of what it
/// returns. Permission gating and the DB-backed claim/poll round trip need a live pipeline and are
/// covered by the integration suite; this exercises what does not need one.
/// </summary>
public sealed class CameraConnectionTestEndpointsTests
{
    // TryValidate is private -- reflection keeps the test from forcing an accessibility change
    // onto production code, same approach as CameraCursorTests for CameraEndpoints.
    private static readonly MethodInfo ValidateMethod = typeof(CameraConnectionTestEndpoints)
        .GetMethod("TryValidate", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new MissingMethodException(nameof(CameraConnectionTestEndpoints), "TryValidate");

    private static (bool Ok, string? Protocol, string? IpAddress, ProblemHttpResult? Problem) Validate(
        CameraConnectionTestRequest request)
    {
        var args = new object?[] { request, null, null, null };
        var ok = (bool)ValidateMethod.Invoke(null, args)!;
        return (ok, (string?)args[1], (string?)args[2], (ProblemHttpResult?)args[3]);
    }

    [Fact]
    public void Valid_request_normalizes_protocol_and_trims_address()
    {
        var (ok, protocol, ip, problem) = Validate(new CameraConnectionTestRequest("rtsp", " 10.0.0.5 ", 554));

        ok.ShouldBeTrue();
        protocol.ShouldBe("RTSP");
        ip.ShouldBe("10.0.0.5");
        problem.ShouldBeNull();
    }

    [Fact]
    public void Unknown_protocol_is_rejected()
    {
        var (ok, _, _, problem) = Validate(new CameraConnectionTestRequest("FTP", "10.0.0.5", 21));

        ok.ShouldBeFalse();
        problem.ShouldNotBeNull();
        problem!.ProblemDetails.Status.ShouldBe(400);
    }

    [Theory]
    [InlineData("not-an-ip")]
    [InlineData("")]
    [InlineData("999.999.999.999")]
    public void Invalid_ip_address_is_rejected(string ip)
    {
        var (ok, _, _, problem) = Validate(new CameraConnectionTestRequest("HTTP", ip, 80));

        ok.ShouldBeFalse();
        problem.ShouldNotBeNull();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public void Out_of_range_port_is_rejected(int port)
    {
        var (ok, _, _, problem) = Validate(new CameraConnectionTestRequest("HTTP", "10.0.0.5", port));

        ok.ShouldBeFalse();
        problem.ShouldNotBeNull();
    }

    [Fact]
    public void Valid_request_carries_no_credential_or_camera_id_field()
    {
        // The whole point of this endpoint: it must be impossible to pass a credential or an
        // existing camera id through it, because it runs before the camera row exists.
        typeof(CameraConnectionTestRequest).GetProperties().Select(p => p.Name)
            .ShouldBe(["Protocol", "IpAddress", "Port"], ignoreOrder: true);
    }
}
