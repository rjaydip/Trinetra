using System.Text.Json;
using Shouldly;
using Trinetra.Federation.Api.Contracts;

namespace Trinetra.UnitTests;

/// <summary>
/// Proves, rather than assumes, what System.Text.Json does with <c>ConnectorTargetRequest</c>'s
/// optional <c>VerifyTls</c> on <c>PUT /vms/{id}</c> — a full replace, not a patch. Finding 9-M1:
/// the concern was that omitting the field might silently downgrade TLS verification. This locks
/// the actual behaviour so an STJ upgrade that changed it would fail CI, not surface in prod.
/// </summary>
public sealed class ConnectorTargetRequestSerializationTests
{
    /// <summary>
    /// Mirrors exactly what <c>[FromBody]</c> actually uses — <c>ApiOptionsExtensions</c> starts
    /// from <see cref="JsonSerializerDefaults.Web"/> (the ASP.NET Core minimal-API default:
    /// case-insensitive property names, camelCase policy) and layers on
    /// <c>RespectRequiredConstructorParameters</c> + <c>RespectNullableAnnotations</c>.
    /// <see cref="JsonSerializerOptions.Default"/> (what a bare
    /// <c>JsonSerializer.Deserialize&lt;T&gt;(json)</c> call uses) is case-SENSITIVE and does
    /// neither of those — using it here would silently test a different code path than the one
    /// a real request goes through, which is exactly the kind of gap this test exists to close.
    /// </summary>
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
    };

    private static string BaseFields => """
        "code": "TGT-1",
        "organizationUnitId": "11111111-1111-1111-1111-111111111111",
        "displayName": "Test target",
        "vendor": "Onvif",
        "endpoint": "http://10.0.0.1",
        "credentialReference": "vault://x"
        """;

    [Fact]
    public void VerifyTls_OmittedFromBody_DefaultsToTrue()
    {
        var json = $$"""{ {{BaseFields}} }""";

        var request = JsonSerializer.Deserialize<ConnectorTargetRequest>(json, Options);

        request.ShouldNotBeNull();
        request.VerifyTls.ShouldBeTrue(
            "PUT is a full replace: omitting a field must fall back to its documented default "
            + "(true, the safe direction), never silently keep whatever the target had before.");
    }

    [Fact]
    public void VerifyTls_ExplicitFalse_IsHonoured()
    {
        var json = $$"""{ {{BaseFields}}, "verifyTls": false }""";

        var request = JsonSerializer.Deserialize<ConnectorTargetRequest>(json, Options);

        request.ShouldNotBeNull();
        request.VerifyTls.ShouldBeFalse();
    }

    [Fact]
    public void VerifyTls_ExplicitNull_ThrowsRatherThanSilentlyDefaulting()
    {
        var json = $$"""{ {{BaseFields}}, "verifyTls": null }""";

        // A non-nullable bool cannot bind null. This is what BadRequestExceptionHandler catches
        // (as a BadHttpRequestException wrapping this) and turns into a clean 400 rather than a
        // silent "verifyTls stayed true" or a 500.
        Should.Throw<JsonException>(() => JsonSerializer.Deserialize<ConnectorTargetRequest>(json, Options));
    }
}
