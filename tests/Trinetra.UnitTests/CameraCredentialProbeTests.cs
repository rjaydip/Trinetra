using System.Reflection;
using Shouldly;
using Trinetra.Federation.Core.Abstractions;
using Trinetra.Federation.Runtime;

namespace Trinetra.UnitTests;

/// <summary>
/// <see cref="CameraCredentialProbe"/>'s pure request-building logic — parsing an RTSP
/// <c>WWW-Authenticate</c> challenge and building the matching <c>Authorization</c> header. The
/// live TCP/HTTP handshakes and the DB-backed claim/poll round trip need a real device or a
/// database and are covered by the integration suite; this exercises what does not need either.
/// </summary>
public sealed class CameraCredentialProbeTests
{
    // Both are private statics -- reflection keeps the test from forcing an accessibility change
    // onto production code, same approach as CameraConnectionTestEndpointsTests.
    private static readonly MethodInfo BuildAuthMethod = typeof(CameraCredentialProbe)
        .GetMethod("BuildRtspAuthorization", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new MissingMethodException(nameof(CameraCredentialProbe), "BuildRtspAuthorization");

    private static readonly MethodInfo ExtractDigestParamMethod = typeof(CameraCredentialProbe)
        .GetMethod("ExtractDigestParam", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new MissingMethodException(nameof(CameraCredentialProbe), "ExtractDigestParam");

    private static string? BuildAuth(string wwwAuthenticate, Credential credential, string method, string uri) =>
        (string?)BuildAuthMethod.Invoke(null, [wwwAuthenticate, credential, method, uri]);

    private static string? ExtractDigestParam(string header, string name) =>
        (string?)ExtractDigestParamMethod.Invoke(null, [header, name]);

    [Fact]
    public void Basic_challenge_produces_a_base64_username_password_header()
    {
        var credential = new Credential(username: "admin", password: "s3cret");

        var header = BuildAuth("Basic realm=\"camera\"", credential, "OPTIONS", "rtsp://10.0.0.5:554/");

        header.ShouldBe("Basic " + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("admin:s3cret")));
    }

    [Fact]
    public void Digest_challenge_produces_a_response_hash_without_disclosing_the_password()
    {
        var credential = new Credential(username: "admin", password: "s3cret");

        var header = BuildAuth(
            "Digest realm=\"IP Camera\", nonce=\"abc123\"", credential, "OPTIONS",
            "rtsp://10.0.0.5:554/");

        header.ShouldNotBeNull();
        header.ShouldStartWith("Digest username=\"admin\", realm=\"IP Camera\", nonce=\"abc123\"");
        header.ShouldContain("response=\"");
        header.ShouldNotContain("s3cret");
    }

    [Fact]
    public void Digest_challenge_missing_realm_or_nonce_cannot_be_answered()
    {
        var credential = new Credential(username: "admin", password: "s3cret");

        BuildAuth("Digest qop=\"auth\"", credential, "OPTIONS", "rtsp://10.0.0.5:554/").ShouldBeNull();
    }

    [Fact]
    public void Unsupported_scheme_is_reported_as_unanswerable_rather_than_guessed_at()
    {
        var credential = new Credential(username: "admin", password: "s3cret");

        // Never silently falls back to Basic for a scheme it does not recognise -- the caller
        // must see "not verifiable", never a false "authenticated".
        BuildAuth("Bearer realm=\"camera\"", credential, "OPTIONS", "rtsp://10.0.0.5:554/")
            .ShouldBeNull();
    }

    [Fact]
    public void Digest_param_extraction_is_case_insensitive_on_the_scheme_but_exact_on_the_key()
    {
        ExtractDigestParam("digest realm=\"Cam\", nonce=\"xyz\"", "realm").ShouldBe("Cam");
        ExtractDigestParam("digest realm=\"Cam\", nonce=\"xyz\"", "nonce").ShouldBe("xyz");
        ExtractDigestParam("digest realm=\"Cam\"", "opaque").ShouldBeNull();
    }
}
