using Microsoft.Extensions.Caching.Memory;
using Trinetra.Federation.Storage.Repositories;

namespace Trinetra.Federation.Api.Auth;

/// <summary>Shape check for a presented API key, before it is used for a lookup.</summary>
internal static class ApiKeyFormat
{
    /// <summary>
    /// A provisioned key is 64 lowercase hex chars —
    /// <c>Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32))</c>, see
    /// <c>ApiKeyEndpoints</c>. Allocation-free, no regex.
    /// </summary>
    public static bool IsWellFormed(ReadOnlySpan<char> key)
    {
        if (key.Length != 64)
        {
            return false;
        }

        foreach (var c in key)
        {
            if (c is not ((>= '0' and <= '9') or (>= 'a' and <= 'f')))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// A per-node fixed-window limiter for the API-key auth path — applied inside the auth handler,
/// before any database work, so a flood of junk keys cannot amplify into a flood of lookups
/// (finding 4-H5). Keyed on the presented-key hash plus source IP: key-hash primary, so a third
/// party spamming a victim's real key from another address cannot throttle the victim.
/// </summary>
internal static class ApiKeyRateLimiter
{
    public const int Limit = 20;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private sealed class Counter
    {
        public int Hits;
    }

    /// <summary>The outcome of registering one attempt against a partition.</summary>
    public enum Result
    {
        /// <summary>Under the limit — allow.</summary>
        Allowed,

        /// <summary>This attempt is the one that crossed the limit — reject, and it is worth one
        /// audit row.</summary>
        JustLimited,

        /// <summary>Already over the limit in this window — reject silently, no audit row (a
        /// sustained flood must not turn into a sustained stream of INSERTs).</summary>
        AlreadyLimited,
    }

    /// <summary>Registers one attempt for the partition.</summary>
    public static Result Register(IMemoryCache cache, string partitionKey)
    {
        var counter = cache.GetOrCreate("apikeyrl:" + partitionKey, e =>
        {
            // Absolute, set once — a true fixed window, not one a steady trickle holds open.
            e.AbsoluteExpirationRelativeToNow = Window;
            return new Counter();
        });

        // A create/evict race can hand back null; treat that as allowed rather than 500 a
        // machine-to-machine request over a rate-limit bookkeeping miss.
        if (counter is null)
        {
            return Result.Allowed;
        }

        var hits = Interlocked.Increment(ref counter.Hits);
        return hits <= Limit ? Result.Allowed
             : hits == Limit + 1 ? Result.JustLimited
             : Result.AlreadyLimited;
    }
}

/// <summary>A resolved API key held briefly in the per-node grant cache.</summary>
internal sealed record ApiKeyCacheEntry(ApiKeyIdentity Key, ApiKeyGrants Grants);

/// <summary>
/// Per-node grant cache for API-key authentication (finding 8-NEW-H). Holds a resolved key +
/// grants for a short TTL so the common case is not three database round trips. Revoking a key
/// evicts the entry on the node serving the revoke; every other node clears within the TTL.
/// </summary>
internal static class ApiKeyGrantCache
{
    /// <summary>Chosen inside the 30–60s band: short enough that revocation lag is small.</summary>
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(45);

    public static string GrantKey(string fullHash) => "apikeygrant:" + fullHash;

    public static string IdIndexKey(Guid keyId) => "apikeygrant-id:" + keyId.ToString("N");
}
