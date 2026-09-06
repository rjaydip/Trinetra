using System.Globalization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Trinetra.Federation.Storage.Repositories;

namespace Trinetra.Federation.Api.Auth;

/// <summary>
/// Supplies JWT validation parameters, and the per-request revocation check.
/// </summary>
/// <remarks>
/// A separate configurator rather than a closure in <c>Program</c>, because resolving
/// <see cref="JwtTokenService"/> there would require building a second service provider — which
/// creates a duplicate set of singletons, so the token service validating requests would be a
/// different instance from the one issuing them.
/// </remarks>
public sealed class ConfigureJwtBearer : IConfigureNamedOptions<JwtBearerOptions>
{
    private readonly JwtTokenService _tokens;

    public ConfigureJwtBearer(JwtTokenService tokens) => _tokens = tokens;

    public void Configure(JwtBearerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.TokenValidationParameters = _tokens.ValidationParameters;

        // Claims are read exactly as issued. The default mapping rewrites short names like `sub`
        // into long WS-Federation URIs, which would silently break every lookup by claim type.
        options.MapInboundClaims = false;

        // Never echo a validation-failure reason (including a context.Fail message from the
        // revocation check below) in the WWW-Authenticate error_description. Every 401 stays
        // opaque.
        options.IncludeErrorDetails = false;

        options.Events = new JwtBearerEvents
        {
            // Signature, issuer, audience and lifetime have already passed. This is the
            // per-request server-side revocation check: the token carries the token_version it
            // was minted against, and a bump on the user row (logout, password change/reset,
            // deactivation, group removal) — or the user no longer being ACTIVE — makes every
            // older token stale immediately rather than at expiry. A failure here produces the
            // same generic 401 (`WWW-Authenticate: Bearer error="invalid_token"`, empty body) as
            // an expired token, so a stolen token learns nothing about why it stopped working.
            OnTokenValidated = static async context =>
            {
                var principal = context.Principal;
                var sub = principal?.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
                var claimed = principal?.FindFirst(TrinetraClaims.TokenVersion)?.Value;

                if (!Guid.TryParse(sub, out var userId)
                    || !int.TryParse(
                            claimed, NumberStyles.Integer, CultureInfo.InvariantCulture,
                            out var claimedVersion))
                {
                    // A token minted before v1.8 has no tokenver claim. It is at most one access
                    // lifetime from expiry anyway; rejecting it forces re-login deterministically.
                    context.Fail("token version claim missing or malformed");
                    return;
                }

                var users = context.HttpContext.RequestServices
                    .GetRequiredService<UserRepository>();

                var current = await users.GetTokenVersionAsync(
                    userId, context.HttpContext.RequestAborted);

                if (current is null || current.Value != claimedVersion)
                {
                    context.Fail("token version stale, or user no longer active");
                }
            },
        };
    }

    public void Configure(string? name, JwtBearerOptions options) => Configure(options);
}
