using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

using Trinetra.Federation.Storage.Repositories;

namespace Trinetra.Federation.Api.Auth;

/// <summary>
/// Authenticates machine-to-machine callers presenting an API key.
/// </summary>
/// <remarks>
/// <para>
/// People authenticate with JWT through the frontend; service integrations — dashboards,
/// reporting jobs, another department's system — have no human to log in and use a key instead.
/// </para>
/// <para>
/// A key acts <b>through an access group</b>, exactly as a user does, so both paths share one
/// permission and scope model. Two parallel authorization models would inevitably drift, and the
/// weaker one would quietly become the real policy.
/// </para>
/// </remarks>
public sealed class ApiKeyAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "ApiKey";
    public const string HeaderName = "X-Api-Key";

    private readonly ApiKeyRepository _keys;

    public ApiKeyAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        ApiKeyRepository keys)
        : base(options, logger, encoder) => _keys = keys;

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // The request's own token, so a client disconnect stops this work. This path runs on
        // every machine-to-machine request and was previously entirely uncancellable.
        var ct = Context.RequestAborted;

        if (!Request.Headers.TryGetValue(HeaderName, out var presented)
            || string.IsNullOrWhiteSpace(presented))
        {
            return AuthenticateResult.NoResult();
        }

        // The key is never stored; only its hash is. A database dump must not hand someone the
        // ability to rewrite camera credentials.
        var hash = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(presented.ToString())));

        var key = await _keys.FindAsync(hash, ct);

        if (key is null)
        {
            // Deliberately uniform: an unknown key, a revoked key and an expired key all fail the
            // same way, so probing cannot distinguish them.
            return AuthenticateResult.Fail("Invalid API key.");
        }

        var grants = await _keys.GrantsAsync(key.Id, ct);

        // Best effort: a failure to stamp last_used_at must not deny a valid caller.
        await _keys.TouchAsync(key.Id, ct);

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, key.Id.ToString()),
            new(ClaimTypes.Name, key.DisplayName),
            new("trinetra:apikey", key.KeyId),
        };

        claims.AddRange(grants.Permissions.Select(p => new Claim(TrinetraClaims.Permission, p)));

        // Per permission, never one flag for the key as a whole. A key scoped for writes but
        // unscoped for reads must not carry its read freedom into a write.
        claims.AddRange(grants.UnscopedOrganization.Select(
            p => new Claim(TrinetraClaims.UnscopedPermission, p)));


        claims.AddRange(grants.UnscopedGeography.Select(
            p => new Claim(TrinetraClaims.UnscopedGeography, p)));

        var identity = new ClaimsIdentity(claims, SchemeName);
        return AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }

}
