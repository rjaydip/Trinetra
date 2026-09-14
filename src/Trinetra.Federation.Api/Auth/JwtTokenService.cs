using Microsoft.IdentityModel.JsonWebTokens;
using System.Globalization;
using System.Security.Claims;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Trinetra.Federation.Core.Model;

namespace Trinetra.Federation.Api.Auth;

/// <summary>Claim types this platform issues and reads.</summary>
public static class TrinetraClaims
{
    /// <summary>One claim per permission the user holds.</summary>
    public const string Permission = "trinetra:perm";

    /// <summary>
    /// One claim per permission the user exercises without organization scope.
    /// </summary>
    /// <remarks>
    /// Per permission rather than a single flag. A single flag meant an unscoped read permission
    /// silently conferred unscoped write and administration on everything else.
    /// </remarks>
    public const string UnscopedPermission = "trinetra:unscoped-perm";

    /// <summary>
    /// One claim per permission the user exercises without GEOGRAPHIC scope.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="UnscopedPermission"/>, which is the organization dimension. The
    /// two are independent and ANDed: reusing one for the other would let a user confined to one
    /// department reach every district, or the reverse.
    /// </remarks>
    public const string UnscopedGeography = "trinetra:unscoped-geo";

    /// <summary>Set while the user must rotate a bootstrap or reset password.</summary>
    public const string MustChangePassword = "trinetra:pwchange";

    /// <summary>
    /// The single camera id a stream-session token (<see cref="JwtTokenService.IssueStreamSession"/>)
    /// is scoped to. Present on stream-session tokens only.
    /// </summary>
    public const string CameraId = "trinetra:camera-id";

    /// <summary>
    /// Which source a stream-session token's proxy requests should be served from — one of
    /// <c>Trinetra.Federation.Core.Model.CameraVocab.StreamPreferences</c> (v1.25), copied onto
    /// the token at mint time so <c>StreamSessionEndpoints.ProxyAsync</c> doesn't need a database
    /// round trip on every playlist/segment request just to learn it. Present on stream-session
    /// tokens only.
    /// </summary>
    public const string StreamMode = "trinetra:stream-mode";

    /// <summary>
    /// Distinguishes a token's purpose so one kind can never be replayed where another is
    /// expected. A stream-session token carries <c>"stream-session"</c> here; an ordinary access
    /// token has no such claim at all.
    /// </summary>
    public const string Purpose = "trinetra:purpose";

    /// <summary>
    /// The <c>platform_users.token_version</c> the access token was minted against. Checked per
    /// request in <c>ConfigureJwtBearer</c>'s <c>OnTokenValidated</c>; a bump on the user row
    /// (logout, password change/reset, deactivation, group removal) makes every older token stale
    /// immediately rather than at expiry.
    /// </summary>
    public const string TokenVersion = "trinetra:tokenver";
}

/// <summary>Issues signed access tokens.</summary>
/// <remarks>
/// <para>
/// Permissions are baked into the token; <b>scope is not</b>. Scope resolution happens per
/// request against the live hierarchy, so a unit added a moment ago is immediately in scope —
/// whereas a token carrying a resolved list would be stale the instant anyone edited the tree.
/// </para>
/// <para>
/// The trade-off is that a permission GRANT takes effect at the next token issue rather than
/// immediately; a REVOCATION is immediate, because every token carries a
/// <see cref="TrinetraClaims.TokenVersion"/> that <c>OnTokenValidated</c> checks against the
/// live user row. This is why <see cref="JwtOptions.AccessLifetime"/> is minutes — it only has
/// to bound how long a stale grant lingers.
/// </para>
/// </remarks>
public sealed class JwtTokenService
{
    private readonly JwtOptions _options;
    private readonly SigningCredentials _signing;                  // first ring entry — signs
    private readonly IReadOnlyList<SecurityKey> _validationKeys;   // every ring entry — validates

    public JwtTokenService(IOptions<AuthOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value.Jwt;

        var ring = ResolveRing(_options);

        if (ring.Count == 0)
        {
            throw new InvalidOperationException(
                "Auth:Jwt:SigningKeys is not configured.\n\n"
                + "  It lives in config/trinetra.settings.json, which every host reads.\n"
                + "  Provide an ordered ring; the first entry signs, all entries validate:\n\n"
                + "      \"SigningKeys\": [ { \"Kid\": \"2026-09\", \"Value\": \"<key>\" } ]\n\n"
                + "  Generate a key with:  openssl rand -base64 48\n"
                + "  A single legacy env var named Auth__Jwt__SigningKey is still accepted and is\n"
                + "  treated as a one-element ring with kid \"legacy\".\n\n"
                + "  Startup is refused rather than continuing, because an unsigned or "
                + "predictably-signed token is the same as no authentication at all.");
        }

        var keys = new List<SecurityKey>(ring.Count);
        foreach (var k in ring)
        {
            if (string.IsNullOrWhiteSpace(k.Kid))
            {
                throw new InvalidOperationException(
                    "Auth:Jwt:SigningKeys: every entry needs a non-empty Kid.");
            }

            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(k.Value);
            }
            catch (FormatException ex)
            {
                throw new InvalidOperationException(
                    $"Auth:Jwt:SigningKeys entry '{k.Kid}': Value is not valid base64.", ex);
            }

            if (bytes.Length < 32)
            {
                throw new InvalidOperationException(
                    $"Auth:Jwt:SigningKeys entry '{k.Kid}': key must be at least 32 bytes "
                    + "for HMAC-SHA256.");
            }

            keys.Add(new SymmetricSecurityKey(bytes) { KeyId = k.Kid });
        }

        if (keys.Select(s => s.KeyId).Distinct(StringComparer.Ordinal).Count() != keys.Count)
        {
            throw new InvalidOperationException("Auth:Jwt:SigningKeys: duplicate Kid values.");
        }

        _validationKeys = keys;
        _signing = new SigningCredentials(keys[0], SecurityAlgorithms.HmacSha256);
    }

    /// <summary>
    /// The ring in priority order: <see cref="JwtOptions.SigningKeys"/> as configured, else the
    /// legacy scalar <see cref="JwtOptions.SigningKey"/> as a one-element ring keyed
    /// <c>"legacy"</c>, else empty (the caller throws).
    /// </summary>
    private static IReadOnlyList<JwtSigningKey> ResolveRing(JwtOptions o)
    {
        if (o.SigningKeys.Count > 0)
        {
            return [.. o.SigningKeys];
        }

        if (!string.IsNullOrWhiteSpace(o.SigningKey))
        {
            return [new JwtSigningKey { Kid = "legacy", Value = o.SigningKey }];
        }

        return [];
    }

    /// <summary>Access-token lifetime, exposed so the auth endpoints can compute <c>expires_in</c>.</summary>
    public TimeSpan AccessLifetime => _options.AccessLifetime;

    /// <summary>Refresh-token lifetime, applied fresh on issue and on every rotation.</summary>
    public TimeSpan RefreshLifetime => _options.RefreshLifetime;

    /// <summary>How long after a rotation a re-presented refresh token is a race, not theft.</summary>
    public TimeSpan RefreshReuseGrace => _options.RefreshReuseGrace;

    public (string Token, DateTimeOffset ExpiresAt) Issue(
        Guid userId, string username, IReadOnlySet<string> permissions,
        IReadOnlySet<string> unscopedPermissions, IReadOnlySet<string> unscopedGeography,
        int tokenVersion, bool mustChangePassword)
    {
        ArgumentNullException.ThrowIfNull(permissions);
        ArgumentNullException.ThrowIfNull(unscopedPermissions);
        ArgumentNullException.ThrowIfNull(unscopedGeography);

        var expires = DateTimeOffset.UtcNow.Add(_options.AccessLifetime);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, userId.ToString()),
            new(JwtRegisteredClaimNames.UniqueName, username),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
        };

        claims.AddRange(permissions.Select(p => new Claim(TrinetraClaims.Permission, p)));

        claims.AddRange(unscopedPermissions.Select(p => new Claim(TrinetraClaims.UnscopedPermission, p)));
        claims.AddRange(unscopedGeography.Select(p => new Claim(TrinetraClaims.UnscopedGeography, p)));

        claims.Add(new Claim(
            TrinetraClaims.TokenVersion, tokenVersion.ToString(CultureInfo.InvariantCulture)));

        if (mustChangePassword)
        {
            claims.Add(new Claim(TrinetraClaims.MustChangePassword, "true"));
        }

        // JsonWebTokenHandler, not the legacy JwtSecurityTokenHandler. It is roughly twice as
        // fast and, more usefully, it is what the JwtBearer middleware already uses to VALIDATE
        // tokens — so issuing and validating now go through one implementation rather than two
        // that can disagree about claim handling.
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = _options.Issuer,
            Audience = _options.Audience,
            Subject = new ClaimsIdentity(claims),
            NotBefore = DateTime.UtcNow,
            Expires = expires.UtcDateTime,
            // The signing key carries its Kid, so JsonWebTokenHandler stamps the `kid` header
            // itself — no AdditionalHeaderClaims needed.
            SigningCredentials = _signing,
        };

        return (Handler.CreateToken(descriptor), expires);
    }

    /// <summary>
    /// The value <see cref="TrinetraClaims.Purpose"/> carries on a stream-session token.
    /// </summary>
    public const string StreamSessionPurpose = "stream-session";

    /// <summary>
    /// Distinct audience for stream-session tokens (<c>docs/STREAMING-GATEWAY-PLAN.md</c> G2/G4).
    /// Deliberately different from <see cref="JwtOptions.Audience"/> so a stream-session token
    /// fails the ordinary bearer-token validation used everywhere else on this API (which pins
    /// <c>ValidAudience</c> to <see cref="JwtOptions.Audience"/>) — a leaked stream token cannot
    /// be replayed as a general-purpose access token.
    /// </summary>
    public const string StreamSessionAudience = "trinetra-stream";

    /// <summary>
    /// Stream-session token lifetime. A few minutes: the token exists only to get the browser's
    /// HLS player through the handful of playlist/segment requests one viewing needs; a leaked
    /// token should stop being useful quickly.
    /// </summary>
    public static readonly TimeSpan StreamSessionLifetime = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Mints a short-lived token scoped to exactly one camera — <c>GET
    /// /api/v1/streams/{cameraId}/session</c> (G2). The claim set is minimal on purpose: a camera
    /// id and an expiry, no user or role claims a client needs to read, and
    /// <see cref="TrinetraClaims.Purpose"/> so it can never pass for an ordinary access token
    /// even if the audience check were somehow bypassed downstream.
    /// </summary>
    public (string Token, DateTimeOffset ExpiresAt) IssueStreamSession(Guid cameraId, string streamMode)
    {
        var expires = DateTimeOffset.UtcNow.Add(StreamSessionLifetime);

        var claims = new List<Claim>
        {
            new(TrinetraClaims.CameraId, cameraId.ToString()),
            new(TrinetraClaims.Purpose, StreamSessionPurpose),
            new(TrinetraClaims.StreamMode, streamMode),
        };

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = _options.Issuer,
            Audience = StreamSessionAudience,
            Subject = new ClaimsIdentity(claims),
            NotBefore = DateTime.UtcNow,
            Expires = expires.UtcDateTime,
            SigningCredentials = _signing,
        };

        return (Handler.CreateToken(descriptor), expires);
    }

    /// <summary>
    /// Validates a stream-session token — <c>GET /api/v1/streams/validate</c> (G4, the
    /// nginx <c>auth_request</c> target). Checks signature, issuer, the distinct
    /// <see cref="StreamSessionAudience"/>, lifetime and the purpose claim; does <b>not</b> touch
    /// the database or re-run the camera scope check — that already happened once, at mint time
    /// in <see cref="IssueStreamSession"/>, and the token's short lifetime is what stands in for
    /// re-checking it. Returns the camera id on success.
    /// </summary>
    public async Task<StreamSessionClaims?> ValidateStreamSessionAsync(string token, CancellationToken ct)
    {
        var parameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = _options.Issuer,
            ValidateAudience = true,
            ValidAudience = StreamSessionAudience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = _validationKeys,
            ValidateLifetime = true,
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            ClockSkew = TimeSpan.Zero,
        };

        TokenValidationResult result;
        try
        {
            result = await Handler.ValidateTokenAsync(token, parameters).WaitAsync(ct);
        }
        catch (SecurityTokenException)
        {
            return null;
        }

        if (!result.IsValid)
        {
            return null;
        }

        var purpose = result.ClaimsIdentity?.FindFirst(TrinetraClaims.Purpose)?.Value;
        if (!string.Equals(purpose, StreamSessionPurpose, StringComparison.Ordinal))
        {
            return null;
        }

        var cameraIdClaim = result.ClaimsIdentity?.FindFirst(TrinetraClaims.CameraId)?.Value;
        if (!Guid.TryParse(cameraIdClaim, out var cameraId))
        {
            return null;
        }

        // Absent on a token minted before v1.25 shipped this claim — RTSP (MediaMTX) was the
        // only mode that ever existed then, so that is the correct default for an old token to
        // fall back to, not a validation failure.
        var mode = result.ClaimsIdentity?.FindFirst(TrinetraClaims.StreamMode)?.Value
            ?? CameraVocab.StreamPreferenceRtsp;

        return new StreamSessionClaims(cameraId, mode);
    }

    // Thread-safe and stateless; allocating one per token issued is pure waste on a path that
    // runs on every login.
    private static readonly JsonWebTokenHandler Handler = new();

    /// <summary>Validation parameters. Replaced wholesale when SSO takes over token issue.</summary>
    public TokenValidationParameters ValidationParameters => new()
    {
        ValidateIssuer = true,
        ValidIssuer = _options.Issuer,
        ValidateAudience = true,
        ValidAudience = _options.Audience,
        ValidateIssuerSigningKey = true,
        // The whole ring, each key carrying its Kid. JsonWebTokenHandler matches the token's
        // `kid` header against this set, so a key added for rotation validates old and new
        // tokens side by side, and a token naming a retired key fails to the generic 401.
        IssuerSigningKeys = _validationKeys,
        ValidateLifetime = true,

        // Pinned, not left to library defaults: an unpinned validator can be talked into
        // accepting `alg: none` or a downgraded algorithm (algorithm-confusion).
        ValidAlgorithms = [SecurityAlgorithms.HmacSha256],

        // Without this, MapInboundClaims = false leaves Identity.Name null, and every audit row
        // records the subject GUID instead of the username — technically correct and useless to
        // anyone reading the log a year later.
        NameClaimType = JwtRegisteredClaimNames.UniqueName,

        // No grace period. The default five minutes means a revoked or expired token keeps
        // working for five minutes past its stated life, which is not what "expired" should mean
        // on a system holding camera credentials.
        ClockSkew = TimeSpan.Zero,
    };
}

/// <summary>What a validated stream-session token authorizes: the one camera, and which source
/// its proxy requests should be served from — <see cref="JwtTokenService.ValidateStreamSessionAsync"/>.</summary>
public sealed record StreamSessionClaims(Guid CameraId, string Mode);
