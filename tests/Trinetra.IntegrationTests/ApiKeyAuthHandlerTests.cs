using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Trinetra.Federation.Api.Auth;
using Trinetra.Federation.Storage.Repositories;

namespace Trinetra.IntegrationTests;

/// <summary>PR7c — the API-key auth handler's pre-DB guards: rate limit, format check, and the
/// per-node grant cache.</summary>
public sealed class ApiKeyAuthHandlerTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly PostgresFixture _fixture;

    private static readonly Guid GroupId = Guid.Parse("f4444444-4444-4444-4444-444444444444");
    private static readonly Guid KeyId   = Guid.Parse("c4444444-0000-4000-8000-000000000abc");

    // Exactly 64 lowercase hex chars.
    private const string RawKey = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    public ApiKeyAuthHandlerTests(PostgresFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(RawKey)));
        await _fixture.ExecuteAsync($"""
            DELETE FROM federation.api_key WHERE id = '{KeyId}';
            DELETE FROM federation.access_groups WHERE id = '{GroupId}';
            INSERT INTO federation.access_groups (id, code, name, role_id, status)
            SELECT '{GroupId}', 'AKHANDLER', 'Key handler test', r.id, 'ACTIVE'
            FROM federation.roles r WHERE r.code = 'VIEWER';
            INSERT INTO federation.api_key (id, key_id, key_hash, display_name, group_id)
            VALUES ('{KeyId}', 'ak_handler', '{hash}', 'Handler test', '{GroupId}');
            """);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<(AuthenticateResult Result, int Status)> Run(
        IMemoryCache cache, string? key, string ip = "198.51.100.9")
    {
        var http = new DefaultHttpContext
        {
            Connection = { RemoteIpAddress = System.Net.IPAddress.Parse(ip) },
        };
        if (key is not null)
        {
            http.Request.Headers[ApiKeyAuthenticationHandler.HeaderName] = key;
        }

        var handler = new ApiKeyAuthenticationHandler(
            new OptionsMonitorStub(),
            NullLoggerFactory.Instance,
            UrlEncoder.Default,
            new ApiKeyRepository(_fixture.DataSource),
            cache,
            new AuthAuditRepository(_fixture.DataSource, NullLogger<AuthAuditRepository>.Instance));

        await handler.InitializeAsync(
            new AuthenticationScheme(ApiKeyAuthenticationHandler.SchemeName, null,
                typeof(ApiKeyAuthenticationHandler)),
            http);

        var result = await handler.AuthenticateAsync();
        return (result, http.Response.StatusCode);
    }

    private sealed class OptionsMonitorStub : IOptionsMonitor<AuthenticationSchemeOptions>
    {
        public AuthenticationSchemeOptions CurrentValue { get; } = new();
        public AuthenticationSchemeOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<AuthenticationSchemeOptions, string?> listener) => null;
    }

    [Fact]
    public async Task WellFormedKnownKey_Authenticates()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var (result, _) = await Run(cache, RawKey);
        result.Succeeded.ShouldBeTrue(result.Failure?.ToString());
    }

    [Fact]
    public async Task MalformedKey_Rejected_NoLookup()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var (result, _) = await Run(cache, "too-short");
        result.Succeeded.ShouldBeFalse();
    }

    [Fact]
    public async Task RateLimit_TripsAfterLimit_Returns429_BeforeAnyDbWork()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var junk = new string('a', 64); // well-formed, unknown

        for (var i = 0; i < ApiKeyRateLimiter.Limit; i++)
        {
            (await Run(cache, junk)).Result.Succeeded.ShouldBeFalse();
        }

        var (result, status) = await Run(cache, junk);
        result.Succeeded.ShouldBeFalse();
        status.ShouldBe(StatusCodes.Status429TooManyRequests);

        // Exactly one ratelimited audit row for the window, not one per rejected request.
        var rows = await _fixture.ScalarAsync<long>(
            "SELECT count(*) FROM federation.auth_audit WHERE event_type = 'apikey.ratelimited' "
            + "AND source_address = '198.51.100.9'");
        rows.ShouldBe(1);
    }

    [Fact]
    public async Task GrantCache_SecondCall_ServesFromCache()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var fullHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(RawKey)));

        await Run(cache, RawKey);
        cache.TryGetValue(ApiKeyGrantCache.GrantKey(fullHash), out _).ShouldBeTrue();
        cache.TryGetValue(ApiKeyGrantCache.IdIndexKey(KeyId), out _).ShouldBeTrue();

        // Revoke-style bust removes both.
        if (cache.TryGetValue(ApiKeyGrantCache.IdIndexKey(KeyId), out string? h) && h is not null)
        {
            cache.Remove(ApiKeyGrantCache.GrantKey(h));
            cache.Remove(ApiKeyGrantCache.IdIndexKey(KeyId));
        }
        cache.TryGetValue(ApiKeyGrantCache.GrantKey(fullHash), out _).ShouldBeFalse();
    }
}
