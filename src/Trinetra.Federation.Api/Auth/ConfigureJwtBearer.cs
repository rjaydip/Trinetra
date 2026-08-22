using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;

namespace Trinetra.Federation.Api.Auth;

/// <summary>
/// Supplies JWT validation parameters from the container.
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
    }

    public void Configure(string? name, JwtBearerOptions options) => Configure(options);
}
