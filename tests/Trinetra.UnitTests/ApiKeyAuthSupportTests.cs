using System.Security.Cryptography;
using Microsoft.Extensions.Caching.Memory;
using Shouldly;

namespace Trinetra.UnitTests;

// ApiKeyFormat and ApiKeyRateLimiter are internal to Trinetra.Federation.Api; the test project
// already has InternalsVisibleTo via the existing api-facing tests.
using Trinetra.Federation.Api.Auth;

public sealed class ApiKeyAuthSupportTests
{
    [Fact]
    public void Format_Accepts64LowerHex()
    {
        ApiKeyFormat.IsWellFormed(new string('a', 64)).ShouldBeTrue();
        ApiKeyFormat.IsWellFormed(Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)))
            .ShouldBeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    public void Format_RejectsWrongLength(string key) =>
        ApiKeyFormat.IsWellFormed(key).ShouldBeFalse();

    [Fact]
    public void Format_RejectsUppercaseAndNonHex()
    {
        ApiKeyFormat.IsWellFormed(new string('A', 64)).ShouldBeFalse();
        ApiKeyFormat.IsWellFormed(new string('g', 64)).ShouldBeFalse();
        ApiKeyFormat.IsWellFormed(new string('a', 63) + "!").ShouldBeFalse();
    }

    [Fact]
    public void RateLimiter_AllowsUpToLimit_ThenJustLimited_ThenAlreadyLimited()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());

        for (var i = 0; i < ApiKeyRateLimiter.Limit; i++)
        {
            ApiKeyRateLimiter.Register(cache, "k|ip").ShouldBe(ApiKeyRateLimiter.Result.Allowed);
        }

        // The transition — and only this one — is worth an audit row.
        ApiKeyRateLimiter.Register(cache, "k|ip").ShouldBe(ApiKeyRateLimiter.Result.JustLimited);
        ApiKeyRateLimiter.Register(cache, "k|ip").ShouldBe(ApiKeyRateLimiter.Result.AlreadyLimited);
        ApiKeyRateLimiter.Register(cache, "k|ip").ShouldBe(ApiKeyRateLimiter.Result.AlreadyLimited);
    }

    [Fact]
    public void RateLimiter_PartitionsAreIndependent()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());

        for (var i = 0; i <= ApiKeyRateLimiter.Limit; i++)
        {
            ApiKeyRateLimiter.Register(cache, "k1|ip");
        }

        ApiKeyRateLimiter.Register(cache, "k2|ip").ShouldBe(ApiKeyRateLimiter.Result.Allowed);
        ApiKeyRateLimiter.Register(cache, "k1|ip2").ShouldBe(ApiKeyRateLimiter.Result.Allowed);
    }

    [Fact]
    public void GrantCache_KeysAreStableAndDistinct()
    {
        ApiKeyGrantCache.GrantKey("abc").ShouldBe(ApiKeyGrantCache.GrantKey("abc"));
        ApiKeyGrantCache.GrantKey("abc").ShouldNotBe(ApiKeyGrantCache.GrantKey("abd"));
        var id = Guid.NewGuid();
        ApiKeyGrantCache.IdIndexKey(id).ShouldBe(ApiKeyGrantCache.IdIndexKey(id));
        ApiKeyGrantCache.GrantKey("abc").ShouldNotBe(ApiKeyGrantCache.IdIndexKey(id));
    }
}
