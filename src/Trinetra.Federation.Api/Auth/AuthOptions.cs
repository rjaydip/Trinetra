using System.ComponentModel.DataAnnotations;

namespace Trinetra.Federation.Api.Auth;

/// <summary>Authentication configuration.</summary>
public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    public JwtOptions Jwt { get; set; } = new();
    public SeedAdminOptions SeedAdmin { get; set; } = new();

    /// <summary>Origins permitted to call the API. Never a wildcard: requests carry credentials.</summary>
    public IList<string> AllowedOrigins { get; } = [];
}

public sealed class JwtOptions
{
    public string Issuer { get; set; } = "trinetra";
    public string Audience { get; set; } = "trinetra-api";

    /// <summary>
    /// Base64 symmetric signing key.
    /// </summary>
    /// <remarks>
    /// Used while the platform issues its own tokens. When SSO takes over, validation moves to
    /// the provider's authority and signing keys and this becomes unused — the claims mapping,
    /// policies and endpoints do not change.
    /// </remarks>
    public string SigningKey { get; set; } = "";

    /// <summary>
    /// Access-token lifetime. Deliberately short: permissions are baked in at issue, and the
    /// per-request revocation signal (<c>token_version</c>) only closes the gap to one request —
    /// 15 minutes bounds how long a since-removed group or a stale permission set keeps working
    /// before a client must refresh.
    /// </summary>
    public TimeSpan AccessLifetime { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Refresh-token lifetime, applied fresh on every rotation. The window slides forward on use
    /// and lapses on this much inactivity, at which point the user re-authenticates.
    /// </summary>
    public TimeSpan RefreshLifetime { get; set; } = TimeSpan.FromHours(8);

    /// <summary>
    /// Grace window after a refresh token is rotated during which re-presenting it is treated as
    /// a benign client race (two browser tabs) rather than theft. See
    /// <c>AuthEndpoints.RefreshAsync</c>.
    /// </summary>
    public TimeSpan RefreshReuseGrace { get; set; } = TimeSpan.FromSeconds(10);
}

public sealed class SeedAdminOptions
{
    [Required]
    public string Username { get; set; } = "admin";

    /// <summary>
    /// Bootstrap password. Supply via environment (<c>Auth__SeedAdmin__Password</c>) — putting it
    /// in appsettings.json puts it in source control.
    /// </summary>
    public string Password { get; set; } = "";

    public string DisplayName { get; set; } = "Platform Administrator";
}
