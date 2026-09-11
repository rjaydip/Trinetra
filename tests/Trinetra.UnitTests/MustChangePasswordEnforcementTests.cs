using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Shouldly;
using Trinetra.Federation.Api.Auth;

namespace Trinetra.UnitTests;

/// <summary>
/// Finding 4-M1 / 5-L5 — <c>CallerContextFactory.MustChangePassword</c> is the predicate the new
/// pipeline middleware (<c>ApiMiddlewareExtensions.EnforceMustChangePasswordAsync</c>) gates on.
/// The end-to-end pipeline wiring (endpoint metadata → 403) is the same accepted gap as
/// <c>OnTokenValidated</c> — no <c>WebApplicationFactory</c> in this suite (see
/// <c>TokenRevocationTests</c>) — so this covers the claim-reading logic directly instead.
/// </summary>
public sealed class MustChangePasswordEnforcementTests
{
    private static DefaultHttpContext ContextFor(params Claim[] claims) => new()
    {
        User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")),
    };

    [Fact]
    public void NoClaim_ReturnsFalse() =>
        CallerContextFactory.MustChangePassword(ContextFor()).ShouldBeFalse();

    [Fact]
    public void ClaimTrue_ReturnsTrue() =>
        CallerContextFactory
            .MustChangePassword(ContextFor(new Claim(TrinetraClaims.MustChangePassword, "true")))
            .ShouldBeTrue();

    // Only "false" is ever minted (JwtTokenService omits the claim entirely rather than issuing
    // it with a false value), but a stray future caller minting "false" must not read as "must
    // change" — HasClaim(type, "true") is an exact value match, not presence-only.
    [Fact]
    public void ClaimFalse_ReturnsFalse() =>
        CallerContextFactory
            .MustChangePassword(ContextFor(new Claim(TrinetraClaims.MustChangePassword, "false")))
            .ShouldBeFalse();
}
