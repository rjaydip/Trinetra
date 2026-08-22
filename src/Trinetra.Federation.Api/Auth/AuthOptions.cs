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
    /// Token lifetime. Short because permissions are resolved at issue time: a revoked group
    /// keeps working until the token expires, so the window should be minutes-to-hours, not days.
    /// </summary>
    public TimeSpan Lifetime { get; set; } = TimeSpan.FromHours(8);
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
