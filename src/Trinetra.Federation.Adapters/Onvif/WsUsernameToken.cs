using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Trinetra.Federation.Core.Abstractions;

namespace Trinetra.Federation.Adapters.Onvif;

/// <summary>
/// Builds the WS-Security <c>UsernameToken</c> header with a password digest.
/// </summary>
/// <remarks>
/// <para>
/// Digest is <c>Base64(SHA1(nonce + created + password))</c>, where <c>created</c> is a UTC
/// timestamp the <b>device</b> considers current.
/// </para>
/// <para>
/// <b>This is why clock skew is an authentication problem, not just a correlation problem.</b>
/// A device rejects a token whose <c>Created</c> falls outside its tolerance — typically five
/// minutes. Departmental cameras with drifted or unset clocks are exactly the population this
/// platform has to onboard, so the timestamp is computed from <i>device</i> time (host time plus
/// a measured per-target offset), never from host time directly. Get this wrong and a perfectly
/// correct username and password fail with an indistinguishable "not authorized".
/// </para>
/// <para>
/// WCF has never supported <c>PasswordDigest</c> — only <c>PasswordText</c> — so this header is
/// hand-written under any client strategy. It is not extra work caused by hand-rolling SOAP.
/// </para>
/// </remarks>
internal static class WsUsernameToken
{
    /// <summary>
    /// Creates the <c>&lt;wsse:Security&gt;</c> header element.
    /// </summary>
    /// <param name="credential">Resolved secret; never persisted or logged.</param>
    /// <param name="deviceUtcNow">
    /// Current time <i>as the device sees it</i> — host UTC plus the target's measured clock
    /// offset. See <see cref="OnvifSoapClient"/> for how the offset is obtained.
    /// </param>
    internal static XElement Create(Credential credential, DateTimeOffset deviceUtcNow)
    {
        var nonce = RandomNumberGenerator.GetBytes(16);
        var created = deviceUtcNow.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ",
            CultureInfo.InvariantCulture);

        var digest = ComputeDigest(nonce, created, credential.Password ?? string.Empty);

        return new XElement(Ns.Wsse + "Security",
            new XAttribute(XNamespace.Xmlns + "wsse", Ns.Wsse.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "wsu", Ns.Wsu.NamespaceName),
            new XAttribute(Ns.Soap + "mustUnderstand", "1"),
            new XElement(Ns.Wsse + "UsernameToken",
                new XElement(Ns.Wsse + "Username", credential.Username ?? string.Empty),
                new XElement(Ns.Wsse + "Password",
                    new XAttribute("Type", Ns.PasswordDigestType),
                    digest),
                new XElement(Ns.Wsse + "Nonce",
                    new XAttribute("EncodingType", Ns.Base64BinaryType),
                    Convert.ToBase64String(nonce)),
                new XElement(Ns.Wsu + "Created", created)));
    }

    /// <summary>
    /// <c>Base64(SHA1(nonce || created || password))</c> per the WS-Security UsernameToken
    /// profile. SHA-1 is mandated by that profile; it is not a choice available here.
    /// </summary>
    internal static string ComputeDigest(byte[] nonce, string created, string password)
    {
        var createdBytes = Encoding.UTF8.GetBytes(created);
        var passwordBytes = Encoding.UTF8.GetBytes(password);

        var buffer = new byte[nonce.Length + createdBytes.Length + passwordBytes.Length];
        nonce.CopyTo(buffer, 0);
        createdBytes.CopyTo(buffer, nonce.Length);
        passwordBytes.CopyTo(buffer, nonce.Length + createdBytes.Length);

#pragma warning disable CA5350 // Do Not Use Weak Cryptographic Algorithms - profile mandates SHA-1
        var hash = SHA1.HashData(buffer);
#pragma warning restore CA5350

        return Convert.ToBase64String(hash);
    }
}
