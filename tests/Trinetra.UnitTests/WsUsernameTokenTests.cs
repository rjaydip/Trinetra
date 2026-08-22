using System.Text;
using Shouldly;
using Trinetra.Federation.Adapters.Onvif;
using Trinetra.Federation.Core.Abstractions;

namespace Trinetra.UnitTests;

public sealed class WsUsernameTokenTests
{
    [Fact]
    public void Digest_MatchesTheWsSecurityProfileFormula()
    {
        // Base64(SHA1(nonce + created + password)), verified against a known vector so a
        // refactor cannot silently change the algorithm and break every device at once.
        var nonce = Convert.FromBase64String("LKqI6G/AikKCQrN0zqZFlg==");
        const string created = "2010-09-16T07:50:45Z";
        const string password = "userpassword";

        var digest = WsUsernameToken.ComputeDigest(nonce, created, password);

        digest.ShouldBe("tuOSpGlFlIXsozq4HFNeeGeFLEI=");
    }

    [Fact]
    public void Security_UsesDeviceTimeNotHostTime()
    {
        // The device rejects a token whose Created falls outside its tolerance (typically five
        // minutes). Cameras with drifted clocks are exactly the population being onboarded, so
        // the timestamp must come from device time or correct credentials fail with an
        // indistinguishable "not authorized".
        var credential = new Credential("admin", "secret");
        var deviceNow = DateTimeOffset.UtcNow.AddHours(-3);

        var header = WsUsernameToken.Create(credential, deviceNow);

        var created = header.Descendants()
            .First(e => e.Name.LocalName == "Created").Value;

        DateTimeOffset.Parse(created, System.Globalization.CultureInfo.InvariantCulture)
            .ShouldBe(deviceNow, tolerance: TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void Security_EmitsUsernameDigestNonceAndCreated()
    {
        var header = WsUsernameToken.Create(new Credential("admin", "secret"), DateTimeOffset.UtcNow);

        header.Descendants().Select(e => e.Name.LocalName)
            .ShouldBe(["UsernameToken", "Username", "Password", "Nonce", "Created"], ignoreOrder: true);

        header.Descendants().First(e => e.Name.LocalName == "Password")
            .Attribute("Type")!.Value.ShouldEndWith("#PasswordDigest");
    }

    [Fact]
    public void Security_ProducesADifferentNonceEachTime()
    {
        // A repeated nonce lets a conformant device reject the request as a replay.
        var credential = new Credential("admin", "secret");
        var now = DateTimeOffset.UtcNow;

        var first = WsUsernameToken.Create(credential, now)
            .Descendants().First(e => e.Name.LocalName == "Nonce").Value;
        var second = WsUsernameToken.Create(credential, now)
            .Descendants().First(e => e.Name.LocalName == "Nonce").Value;

        first.ShouldNotBe(second);
    }

    [Fact]
    public void Credential_RedactsItsPasswordWhenFormatted()
    {
        // Secrets leak through ordinary string interpolation into logs and tracebacks far more
        // often than through anyone deciding to log them.
        var credential = new Credential("admin", "super-secret-value");

        $"{credential}".ShouldNotContain("super-secret-value");
        credential.ToString().ShouldContain("<redacted>");
    }
}
