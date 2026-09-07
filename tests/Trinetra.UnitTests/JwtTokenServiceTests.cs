using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Shouldly;
using Trinetra.Federation.Api.Auth;

namespace Trinetra.UnitTests;

public sealed class JwtTokenServiceTests
{
    private static string Key() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));

    private static JwtTokenService Scalar(string key) => new(Options.Create(new AuthOptions
    {
        Jwt = new JwtOptions { SigningKey = key },
    }));

    private static JwtTokenService Ring(params (string Kid, string Value)[] keys)
    {
        var opts = new AuthOptions();
        foreach (var (kid, value) in keys)
        {
            opts.Jwt.SigningKeys.Add(new JwtSigningKey { Kid = kid, Value = value });
        }
        return new JwtTokenService(Options.Create(opts));
    }

    private static string IssueFrom(JwtTokenService svc) => svc.Issue(
        Guid.NewGuid(), "u", new HashSet<string>(), new HashSet<string>(), new HashSet<string>(),
        tokenVersion: 0, mustChangePassword: false).Token;

    private static async Task<bool> ValidatesAsync(string token, JwtTokenService against) =>
        (await new JsonWebTokenHandler().ValidateTokenAsync(token, against.ValidationParameters)).IsValid;

    [Fact]
    public void ValidationParameters_PinTheAlgorithm()
    {
        Ring(("a", Key())).ValidationParameters.ValidAlgorithms
            .ShouldBe([SecurityAlgorithms.HmacSha256]);
    }

    [Fact]
    public void ValidationParameters_CarryEveryRingKey_AndNoSingularKey()
    {
        var vp = Ring(("a", Key()), ("b", Key())).ValidationParameters;

        vp.IssuerSigningKey.ShouldBeNull();
        vp.IssuerSigningKeys!.Select(k => k.KeyId).OrderBy(x => x).ShouldBe(["a", "b"]);
    }

    [Fact]
    public void Issue_StampsThePrimaryKid()
    {
        var token = IssueFrom(Ring(("primary", Key()), ("old", Key())));
        new JsonWebTokenHandler().ReadJsonWebToken(token).Kid.ShouldBe("primary");
    }

    [Fact]
    public async Task Validate_TokenSignedByPrimary_Passes()
    {
        var svc = Ring(("a", Key()));
        (await ValidatesAsync(IssueFrom(svc), svc)).ShouldBeTrue();
    }

    [Fact]
    public async Task Validate_OldTokenAfterPromotion_StillValid()
    {
        var (a, b) = (Key(), Key());
        var beforeRotation = Ring(("a", a));
        var token = IssueFrom(beforeRotation);

        // b promoted to primary, a kept in the ring for the overlap window.
        var afterRotation = Ring(("b", b), ("a", a));

        (await ValidatesAsync(token, afterRotation)).ShouldBeTrue();
        new JsonWebTokenHandler().ReadJsonWebToken(IssueFrom(afterRotation)).Kid.ShouldBe("b");
    }

    [Fact]
    public async Task Validate_KidNotInRing_Rejected()
    {
        var token = IssueFrom(Ring(("a", Key())));
        (await ValidatesAsync(token, Ring(("b", Key())))).ShouldBeFalse();
    }

    [Fact]
    public async Task Validate_TamperedSignature_Rejected()
    {
        var token = IssueFrom(Ring(("a", Key())));
        var parts = token.Split('.');
        parts[2] = parts[2][..^2] + (parts[2][^1] == 'A' ? "BB" : "AA");
        (await ValidatesAsync(string.Join('.', parts), Ring(("a", Key())))).ShouldBeFalse();
    }

    [Fact]
    public void Ctor_BothEmpty_Throws()
    {
        var ex = Should.Throw<InvalidOperationException>(() =>
            new JwtTokenService(Options.Create(new AuthOptions())));
        ex.Message.ShouldContain("SigningKeys");
    }

    [Fact]
    public void Ctor_ScalarAliasOnly_Works_AndKidIsLegacy()
    {
        var token = IssueFrom(Scalar(Key()));
        new JsonWebTokenHandler().ReadJsonWebToken(token).Kid.ShouldBe("legacy");
    }

    [Fact]
    public void Ctor_RingEntryUnder32Bytes_Throws()
    {
        var ex = Should.Throw<InvalidOperationException>(() =>
            Ring(("shorty", Convert.ToBase64String(new byte[16]))));
        ex.Message.ShouldContain("shorty");
    }

    [Fact]
    public void Ctor_RingEntryBadBase64_Throws()
    {
        Should.Throw<InvalidOperationException>(() => Ring(("bad", "not base64!"))).Message
            .ShouldContain("bad");
    }

    [Fact]
    public void Ctor_DuplicateKid_Throws()
    {
        Should.Throw<InvalidOperationException>(() => Ring(("x", Key()), ("x", Key())));
    }

    [Fact]
    public void Ctor_RingEntryMissingKid_Throws()
    {
        Should.Throw<InvalidOperationException>(() => Ring(("", Key())));
    }
}
