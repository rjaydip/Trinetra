using System.Reflection;
using Shouldly;
using Trinetra.Federation.Api.Endpoints;

namespace Trinetra.UnitTests;

/// <summary>
/// The pure reference-minting rule behind <c>PUT /cameras/{id}/credential</c>. The permission
/// gate, scope/404 behaviour, and the sealing round trip through <c>SecretWriter</c> all need a
/// live database and are covered by the integration suite; this exercises what does not need one.
/// </summary>
public sealed class CameraCredentialEndpointsTests
{
    // ResolveReference is private -- reflection keeps the test from forcing an accessibility
    // change onto production code, same approach as CameraConnectionTestEndpointsTests.
    private static readonly MethodInfo ResolveMethod = typeof(CameraCredentialEndpoints)
        .GetMethod("ResolveReference", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new MissingMethodException(nameof(CameraCredentialEndpoints), "ResolveReference");

    private static string Resolve(Guid id, string? existing) =>
        (string)ResolveMethod.Invoke(null, [id, existing])!;

    [Fact]
    public void Camera_with_no_reference_gets_one_derived_from_its_id()
    {
        var id = Guid.Parse("11111111-1111-1111-1111-111111111111");

        Resolve(id, existing: null).ShouldBe($"camera:{id}");
    }

    [Fact]
    public void Camera_with_an_existing_reference_keeps_it_unchanged()
    {
        var id = Guid.NewGuid();

        // Must never be replaced: the reference is the key into federation.secret, so changing
        // it out from under an already-sealed credential would orphan the stored secret.
        Resolve(id, existing: "vault://already-set").ShouldBe("vault://already-set");
    }

    [Fact]
    public void Two_different_cameras_never_collide_on_a_minted_reference()
    {
        var a = Resolve(Guid.NewGuid(), existing: null);
        var b = Resolve(Guid.NewGuid(), existing: null);

        a.ShouldNotBe(b);
    }
}
