using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Trinetra.Federation.Core.Abstractions;
using Trinetra.Federation.Core.Errors;

namespace Trinetra.Federation.Adapters.Http;

/// <summary>
/// HTTP Digest authentication (RFC 2617 / RFC 7616) applied per request.
/// </summary>
/// <remarks>
/// <para>
/// Credentials travel on the request, not the handler. <c>HttpClientHandler.Credentials</c> is
/// handler-scoped, so using it would force one handler per target and defeat the shared-handler
/// topology in <see cref="TargetHttpClientProvider"/>.
/// </para>
/// <para>
/// <b>The 401 re-send is protocol handshake, not a retry.</b> Digest is defined as a two-leg
/// exchange: the server must issue a nonce before a client can compute a response. This does not
/// violate the rule that adapters implement no retries — resilience policy stays with the
/// runtime. A <i>second</i> 401 is an <see cref="AuthException"/> and is never retried, because
/// retrying rejected credentials across an 80k-camera estate is how an integration account gets
/// locked out everywhere at once.
/// </para>
/// <para>
/// One implementation serves ONVIF, Hikvision ISAPI, Dahua CGI and the RTSP probe. Several
/// vendors ship subtly non-conformant digest (Dahua's nonce handling in particular), which is
/// itself an argument for owning this rather than depending on a framework's.
/// </para>
/// </remarks>
public sealed class DigestAuthHandler : DelegatingHandler
{
    /// <summary>Per-request credential. Set by adapters before sending.</summary>
    public static readonly HttpRequestOptionsKey<Credential> CredentialOption = new("trinetra.credential");

    /// <summary>Per-request target id, for attributing auth failures. Optional.</summary>
    public static readonly HttpRequestOptionsKey<Guid> TargetIdOption = new("trinetra.targetId");

    // Challenges are cached per origin so the common case is one round trip, not two. Bounded
    // by the worker's target count, which the lease already caps.
    private readonly ConcurrentDictionary<string, DigestChallenge> _challenges =
        new(StringComparer.OrdinalIgnoreCase);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!request.Options.TryGetValue(CredentialOption, out var credential))
        {
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        request.Options.TryGetValue(TargetIdOption, out var targetId);
        var origin = OriginOf(request.RequestUri);

        // Pre-authenticate from a cached challenge where we have one.
        if (origin is not null && _challenges.TryGetValue(origin, out var cached))
        {
            ApplyAuthorization(request, cached, credential);
        }

        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode != HttpStatusCode.Unauthorized)
        {
            return response;
        }

        var challenge = ParseChallenge(response.Headers.WwwAuthenticate);
        if (challenge is null)
        {
            // Server rejected us but offered no digest challenge — Basic-only, or no auth
            // scheme we support. Not retryable.
            response.Dispose();
            throw new AuthException(
                "Server returned 401 with no supported Digest challenge.", targetId);
        }

        if (origin is not null)
        {
            _challenges[origin] = challenge;
        }

        // Second leg of the handshake. The first response is discarded.
        response.Dispose();

        var retry = await CloneAsync(request, cancellationToken).ConfigureAwait(false);
        ApplyAuthorization(retry, challenge, credential);

        var authenticated = await base.SendAsync(retry, cancellationToken).ConfigureAwait(false);

        if (authenticated.StatusCode == HttpStatusCode.Unauthorized)
        {
            // A second 401 after a valid challenge means the credentials are wrong or expired.
            // Permanent until an operator acts; never retried, never circuit-broken.
            authenticated.Dispose();
            if (origin is not null)
            {
                _challenges.TryRemove(origin, out _);
            }

            throw new AuthException(
                $"Digest authentication rejected for user '{credential.Username}'.", targetId);
        }

        return authenticated;
    }

    private static string? OriginOf(Uri? uri) =>
        uri is null ? null : $"{uri.Scheme}://{uri.Host}:{uri.Port}";

    private static void ApplyAuthorization(
        HttpRequestMessage request, DigestChallenge challenge, Credential credential)
    {
        var uri = request.RequestUri?.PathAndQuery ?? "/";
        var method = request.Method.Method;
        var cnonce = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
        var nc = challenge.NextNonceCount();

        var header = BuildAuthorization(challenge, credential, method, uri, cnonce, nc);
        request.Headers.Authorization = new AuthenticationHeaderValue("Digest", header);
    }

    internal static string BuildAuthorization(
        DigestChallenge challenge, Credential credential,
        string method, string uri, string cnonce, string nonceCount)
    {
        var username = credential.Username ?? string.Empty;
        var password = credential.Password ?? string.Empty;

        var ha1 = Hash(challenge.Algorithm, $"{username}:{challenge.Realm}:{password}");

        // MD5-sess / SHA-256-sess rehash HA1 with the nonces, per RFC 7616 §3.4.2.
        if (challenge.IsSession)
        {
            ha1 = Hash(challenge.Algorithm, $"{ha1}:{challenge.Nonce}:{cnonce}");
        }

        var ha2 = Hash(challenge.Algorithm, $"{method}:{uri}");

        var response = challenge.Qop is null
            ? Hash(challenge.Algorithm, $"{ha1}:{challenge.Nonce}:{ha2}")
            : Hash(challenge.Algorithm,
                $"{ha1}:{challenge.Nonce}:{nonceCount}:{cnonce}:{challenge.Qop}:{ha2}");

        var builder = new StringBuilder()
            .Append(CultureInfo.InvariantCulture, $"username=\"{username}\", ")
            .Append(CultureInfo.InvariantCulture, $"realm=\"{challenge.Realm}\", ")
            .Append(CultureInfo.InvariantCulture, $"nonce=\"{challenge.Nonce}\", ")
            .Append(CultureInfo.InvariantCulture, $"uri=\"{uri}\", ")
            .Append(CultureInfo.InvariantCulture, $"response=\"{response}\"");

        if (challenge.Algorithm != DigestAlgorithm.Md5)
        {
            builder.Append(CultureInfo.InvariantCulture,
                $", algorithm={challenge.AlgorithmToken}");
        }

        if (challenge.Qop is not null)
        {
            builder.Append(CultureInfo.InvariantCulture,
                $", qop={challenge.Qop}, nc={nonceCount}, cnonce=\"{cnonce}\"");
        }

        if (challenge.Opaque is not null)
        {
            builder.Append(CultureInfo.InvariantCulture, $", opaque=\"{challenge.Opaque}\"");
        }

        return builder.ToString();
    }

    /// <remarks>
    /// CA5351 flags MD5, correctly, as cryptographically broken. It is suppressed rather than
    /// avoided because the algorithm is not ours to choose: RFC 2617 defines Digest over MD5,
    /// and the overwhelming majority of deployed NVR and camera firmware offers nothing else.
    /// Refusing MD5 would mean refusing to authenticate to most of the estate.
    ///
    /// Mitigations that are ours to choose, and are taken: SHA-256 and SHA-512/256 (RFC 7616)
    /// are preferred automatically whenever a device offers them; MD5 is used only as the
    /// fallback a device forces. The exposure is also bounded by transport — credentials for
    /// these targets should travel over TLS, and a target reachable only over plaintext HTTP
    /// is a network-segmentation finding, not something a hash choice here can repair.
    /// </remarks>
    private static string Hash(DigestAlgorithm algorithm, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
#pragma warning disable CA5351 // Do Not Use Broken Cryptographic Algorithms - RFC 2617 mandates MD5
        var hash = algorithm switch
        {
            DigestAlgorithm.Sha256 => SHA256.HashData(bytes),
            DigestAlgorithm.Sha512Trunc256 => SHA512.HashData(bytes)[..32],
            _ => MD5.HashData(bytes),
        };
#pragma warning restore CA5351

        return Convert.ToHexStringLower(hash);
    }

    internal static DigestChallenge? ParseChallenge(
        HttpHeaderValueCollection<AuthenticationHeaderValue> headers)
    {
        foreach (var header in headers)
        {
            if (!string.Equals(header.Scheme, "Digest", StringComparison.OrdinalIgnoreCase)
                || header.Parameter is null)
            {
                continue;
            }

            var parts = ParseParameters(header.Parameter);
            if (!parts.TryGetValue("realm", out var realm)
                || !parts.TryGetValue("nonce", out var nonce))
            {
                continue;
            }

            parts.TryGetValue("algorithm", out var algorithmToken);
            parts.TryGetValue("qop", out var qop);
            parts.TryGetValue("opaque", out var opaque);

            // A server may offer several qop values; auth is the only one we implement.
            var selectedQop = qop?
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(q => string.Equals(q, "auth", StringComparison.OrdinalIgnoreCase));

            return new DigestChallenge(realm, nonce, algorithmToken, selectedQop, opaque);
        }

        return null;
    }

    private static Dictionary<string, string> ParseParameters(string parameter)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var span = parameter.AsSpan();
        var index = 0;

        while (index < span.Length)
        {
            var equals = span[index..].IndexOf('=');
            if (equals < 0)
            {
                break;
            }

            var key = span[index..(index + equals)].Trim().ToString();
            index += equals + 1;

            string value;
            if (index < span.Length && span[index] == '"')
            {
                index++;
                var close = span[index..].IndexOf('"');
                if (close < 0)
                {
                    break;
                }

                value = span[index..(index + close)].ToString();
                index += close + 1;
            }
            else
            {
                var comma = span[index..].IndexOf(',');
                var end = comma < 0 ? span.Length : index + comma;
                value = span[index..end].Trim().ToString();
                index = end;
            }

            result[key] = value;

            var next = span[index..].IndexOf(',');
            index = next < 0 ? span.Length : index + next + 1;
        }

        return result;
    }

    private static async Task<HttpRequestMessage> CloneAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri)
        {
            Version = request.Version,
            VersionPolicy = request.VersionPolicy,
        };

        foreach (var header in request.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        foreach (var option in (IDictionary<string, object?>)request.Options)
        {
            clone.Options.Set(new HttpRequestOptionsKey<object?>(option.Key), option.Value);
        }

        if (request.Content is not null)
        {
            // Buffer so the body can be replayed on the authenticated leg. Safe here because
            // digest-authenticated requests are small SOAP/CGI calls; the long-lived event
            // streams authenticate once and then never re-send a body.
            var bytes = await request.Content.ReadAsByteArrayAsync(cancellationToken)
                .ConfigureAwait(false);
            var content = new ByteArrayContent(bytes);

            foreach (var header in request.Content.Headers)
            {
                content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            clone.Content = content;
        }

        return clone;
    }
}

internal enum DigestAlgorithm
{
    Md5,
    Sha256,
    Sha512Trunc256,
}

/// <summary>A parsed <c>WWW-Authenticate: Digest</c> challenge, with its nonce counter.</summary>
internal sealed class DigestChallenge
{
    private int _nonceCount;

    public DigestChallenge(string realm, string nonce, string? algorithmToken, string? qop, string? opaque)
    {
        Realm = realm;
        Nonce = nonce;
        AlgorithmToken = algorithmToken ?? "MD5";
        Qop = qop;
        Opaque = opaque;

        IsSession = AlgorithmToken.EndsWith("-sess", StringComparison.OrdinalIgnoreCase);

        Algorithm = AlgorithmToken.ToUpperInvariant() switch
        {
            "SHA-256" or "SHA-256-SESS" => DigestAlgorithm.Sha256,
            "SHA-512-256" or "SHA-512-256-SESS" => DigestAlgorithm.Sha512Trunc256,
            _ => DigestAlgorithm.Md5,
        };
    }

    public string Realm { get; }
    public string Nonce { get; }
    public string AlgorithmToken { get; }
    public string? Qop { get; }
    public string? Opaque { get; }
    public bool IsSession { get; }
    public DigestAlgorithm Algorithm { get; }

    /// <summary>
    /// Monotonic nonce count. Must never repeat for a given nonce or a conformant server treats
    /// the request as a replay attack and rejects it.
    /// </summary>
    public string NextNonceCount() =>
        Interlocked.Increment(ref _nonceCount).ToString("x8", CultureInfo.InvariantCulture);
}
