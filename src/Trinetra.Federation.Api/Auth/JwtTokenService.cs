using Microsoft.IdentityModel.JsonWebTokens;
using System.Globalization;
using System.Security.Claims;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

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
    private readonly SigningCredentials _credentials;

    public JwtTokenService(IOptions<AuthOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value.Jwt;

        if (string.IsNullOrWhiteSpace(_options.SigningKey))
        {
            throw new InvalidOperationException(
                "Auth:Jwt:SigningKey is not configured.\n\n"
                + "  It lives in config/trinetra.settings.json, which every host reads.\n"
                + "  Generate a replacement with:  openssl rand -base64 48\n\n"
                + "  An environment variable named Auth__Jwt__SigningKey overrides that file, "
                + "which is how a real deployment should supply it.\n\n"
                + "  Startup is refused rather than continuing, because an unsigned or "
                + "predictably-signed token is the same as no authentication at all.");
        }

        var keyBytes = Convert.FromBase64String(_options.SigningKey);

        if (keyBytes.Length < 32)
        {
            throw new InvalidOperationException(
                "Auth:Jwt:SigningKey must be at least 32 bytes for HMAC-SHA256.");
        }

        _credentials = new SigningCredentials(
            new SymmetricSecurityKey(keyBytes), SecurityAlgorithms.HmacSha256);
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
            SigningCredentials = _credentials,
        };

        return (Handler.CreateToken(descriptor), expires);
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
        IssuerSigningKey = _credentials.Key,
        ValidateLifetime = true,

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
