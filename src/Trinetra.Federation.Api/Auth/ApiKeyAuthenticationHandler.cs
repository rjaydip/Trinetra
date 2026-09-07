using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Caching.Memory;
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
/// <para>
/// Before any database work: a per-node fixed-window rate limit (finding 4-H5) and a
/// 64-hex-char format check, so a flood of junk keys cannot amplify into a flood of lookups. On
/// a cache miss the resolved key + grants are held for ~45s (finding 8-NEW-H) and
/// <c>last_used_at</c> is stamped at most once per key per five minutes.
/// </para>
/// </remarks>
public sealed class ApiKeyAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "ApiKey";
    public const string HeaderName = "X-Api-Key";

    private readonly ApiKeyRepository _keys;
    private readonly IMemoryCache _cache;
    private readonly AuthAuditRepository _audit;

    public ApiKeyAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        ApiKeyRepository keys,
        IMemoryCache cache,
        AuthAuditRepository audit)
        : base(options, logger, encoder)
    {
        _keys = keys;
        _cache = cache;
        _audit = audit;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // The request's own token, so a client disconnect stops this work.
        var ct = Context.RequestAborted;

        if (!Request.Headers.TryGetValue(HeaderName, out var presented)
            || string.IsNullOrWhiteSpace(presented))
        {
            return AuthenticateResult.NoResult();
        }

        var raw = presented.ToString();

        // Hash the raw value regardless of format, so a malformed flood is still counted.
        var fullHash = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
        var ip = Context.Connection.RemoteIpAddress?.ToString();
        var ua = Request.Headers.UserAgent.ToString() is { Length: > 0 } s ? s : null;

        // 1. Rate limit — before the format check and before any DB work.
        var limited = ApiKeyRateLimiter.Register(_cache, fullHash[..16] + "|" + (ip ?? "unknown"));
        if (limited != ApiKeyRateLimiter.Result.Allowed)
        {
            // One audit row per window, on the attempt that trips it — a sustained flood must
            // not become a sustained stream of INSERTs (that is the amplification 4-H5 removes).
            if (limited == ApiKeyRateLimiter.Result.JustLimited)
            {
                await _audit.WriteBestEffortAsync(new AuthAuditEntry
                {
                    EventType = AuthAuditEvents.ApiKeyRateLimited, Outcome = "failure",
                    SourceAddress = ip, UserAgent = ua,
                }, ct);
            }

            Context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            return AuthenticateResult.Fail("Too many API-key authentication attempts.");
        }

        // 2. Format check — cheap reject before the hash is used for a lookup.
        if (!ApiKeyFormat.IsWellFormed(raw))
        {
            await _audit.WriteBestEffortAsync(new AuthAuditEntry
            {
                EventType = AuthAuditEvents.ApiKeyAuth, Outcome = "failure",
                SourceAddress = ip, UserAgent = ua, Detail = new { reason = "malformed" },
            }, ct);
            return AuthenticateResult.Fail("Invalid API key.");
        }

        // 3. Grant cache (per-node, ~45s).
        if (!_cache.TryGetValue(ApiKeyGrantCache.GrantKey(fullHash), out ApiKeyCacheEntry? entry)
            || entry is null)
        {
            var key = await _keys.FindAsync(fullHash, ct);
            if (key is null)
            {
                // Deliberately uniform: unknown, revoked and expired keys all fail the same way.
                await _audit.WriteBestEffortAsync(new AuthAuditEntry
                {
                    EventType = AuthAuditEvents.ApiKeyAuth, Outcome = "failure",
                    SourceAddress = ip, UserAgent = ua,
                }, ct);
                return AuthenticateResult.Fail("Invalid API key.");
            }

            var grants = await _keys.GrantsAsync(key.Id, ct);
            entry = new ApiKeyCacheEntry(key, grants);

            var opts = new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = ApiKeyGrantCache.Ttl,
            };
            _cache.Set(ApiKeyGrantCache.GrantKey(fullHash), entry, opts);
            _cache.Set(ApiKeyGrantCache.IdIndexKey(key.Id), fullHash, opts);

            // Coarse last_used_at + coarse success audit: only when a real resolution happened
            // AND the five-minute touch window had lapsed.
            if (await _keys.TouchAsync(key.Id, ct))
            {
                await _audit.WriteBestEffortAsync(new AuthAuditEntry
                {
                    EventType = AuthAuditEvents.ApiKeyAuth, Outcome = "success",
                    ApiKeyId = key.Id, SourceAddress = ip, UserAgent = ua,
                }, ct);
            }
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, entry.Key.Id.ToString()),
            new(ClaimTypes.Name, entry.Key.DisplayName),
            new("trinetra:apikey", entry.Key.KeyId),
        };

        claims.AddRange(entry.Grants.Permissions.Select(p => new Claim(TrinetraClaims.Permission, p)));

        // Per permission, never one flag for the key as a whole. A key scoped for writes but
        // unscoped for reads must not carry its read freedom into a write.
        claims.AddRange(entry.Grants.UnscopedOrganization.Select(
            p => new Claim(TrinetraClaims.UnscopedPermission, p)));

        claims.AddRange(entry.Grants.UnscopedGeography.Select(
            p => new Claim(TrinetraClaims.UnscopedGeography, p)));

        var identity = new ClaimsIdentity(claims, SchemeName);
        return AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }

    // Keep an already-set 429 from being overwritten by the framework's 401 challenge.
    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        if (Context.Response.StatusCode != StatusCodes.Status429TooManyRequests)
        {
            Context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        }

        return Task.CompletedTask;
    }
}
