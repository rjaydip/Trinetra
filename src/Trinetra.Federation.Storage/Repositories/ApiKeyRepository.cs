using Dapper;
using Npgsql;

namespace Trinetra.Federation.Storage.Repositories;

/// <summary>An API key that authenticated successfully.</summary>
public sealed record ApiKeyIdentity
{
    public Guid Id { get; init; }
    public string KeyId { get; init; } = "";
    public string DisplayName { get; init; } = "";
}

/// <summary>What an API key may do, resolved through its access group.</summary>
public sealed record ApiKeyGrants(
    IReadOnlyList<string> Permissions,
    IReadOnlySet<string> UnscopedOrganization,
    IReadOnlySet<string> UnscopedGeography);

/// <summary>One row of the API key list. Never carries the hash or any secret material.</summary>
/// <remarks>
/// Properties, not a positional record: Dapper materialises <c>{ get; init; }</c> column by
/// column and coerces PostgreSQL <c>timestamptz</c> (which Npgsql hands back as
/// <see cref="DateTime"/>) into <see cref="DateTimeOffset"/>. The positional-record path uses
/// strict constructor matching and throws on that same value. Every row type here follows this.
/// </remarks>
public sealed record ApiKeySummary
{
    public Guid Id { get; init; }
    public string KeyId { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public Guid GroupId { get; init; }
    public string GroupCode { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public DateTimeOffset? LastUsedAt { get; init; }
    public DateTimeOffset? RevokedAt { get; init; }
}

/// <summary>What happened to a key when revocation was attempted.</summary>
public enum RevokeOutcome
{
    /// <summary>No key with that id.</summary>
    NotFound,

    /// <summary>The key was live and is now revoked.</summary>
    Revoked,

    /// <summary>The key existed but was already revoked; nothing changed.</summary>
    AlreadyRevoked,
}

/// <summary>The outcome of a revoke, plus the pre-revocation values for the audit row.</summary>
public sealed record ApiKeyRevocation(
    RevokeOutcome Outcome, string DisplayName, Guid GroupId, DateTimeOffset? PriorRevokedAt);

/// <summary>
/// Authenticates machine-to-machine callers and resolves what they may do.
/// </summary>
/// <remarks>
/// A key acts through an access group exactly as a user does, so both paths resolve their grants
/// from the same <c>principal_*</c> functions. Two parallel authorization models would inevitably
/// drift, and the weaker one would quietly become the real policy.
/// </remarks>
public sealed class ApiKeyRepository
{
    private readonly NpgsqlDataSource _dataSource;

    public ApiKeyRepository(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    /// <summary>Finds a live key by the hash of the presented value.</summary>
    /// <remarks>
    /// The key itself is never stored, only its SHA-256. A database dump must not hand someone
    /// the ability to rewrite camera credentials.
    /// </remarks>
    public async Task<ApiKeyIdentity?> FindAsync(string keyHash, CancellationToken ct)
    {
        await using var c = await _dataSource.OpenConnectionAsync(ct);

        return await c.QuerySingleOrDefaultAsync<ApiKeyIdentity>(new CommandDefinition("""
            SELECT k.id, k.key_id, k.display_name
            FROM federation.api_key k
            WHERE k.key_hash = @keyHash
              AND k.revoked_at IS NULL
              AND (k.expires_at IS NULL OR k.expires_at > now());
            """, new { keyHash }, cancellationToken: ct));
    }

    /// <summary>Permissions and per-dimension unscoped sets for a key.</summary>
    /// <remarks>
    /// Resolved from the key id rather than its group id on purpose: the principal functions
    /// re-check revocation and expiry, so a key revoked between authentication and this call
    /// resolves to no groups and therefore to no permissions.
    /// </remarks>
    public async Task<ApiKeyGrants> GrantsAsync(Guid keyId, CancellationToken ct)
    {
        await using var c = await _dataSource.OpenConnectionAsync(ct);

        await using var multi = await c.QueryMultipleAsync(new CommandDefinition("""
            SELECT permission_code FROM federation.principal_permissions(NULL, @keyId);
            SELECT permission_code FROM federation.unscoped_permissions(NULL, @keyId);

            SELECT rp.permission_code
            FROM federation.principal_groups(NULL, @keyId) pg
            JOIN federation.access_groups ag    ON ag.id = pg.group_id
            JOIN federation.roles r             ON r.id = ag.role_id AND r.status = 'ACTIVE'
            JOIN federation.role_permissions rp ON rp.role_id = r.id
            WHERE NOT EXISTS (
                SELECT 1 FROM federation.group_scopes gs
                JOIN federation.scopes s ON s.id = gs.scope_id
                WHERE gs.group_id = ag.id AND s.scope_type = 'GEOGRAPHY');
            """, new { keyId }, cancellationToken: ct));

        // One round trip. This runs on every machine-to-machine request, and three separate
        // queries would triple the per-request latency of the auth handler.
        var permissions = (await multi.ReadAsync<string>()).ToList();
        var unscopedOrg = (await multi.ReadAsync<string>()).ToHashSet(StringComparer.Ordinal);
        var unscopedGeo = (await multi.ReadAsync<string>()).ToHashSet(StringComparer.Ordinal);

        return new ApiKeyGrants(permissions, unscopedOrg, unscopedGeo);
    }

    /// <summary>
    /// Provisions a new key. The raw value is generated by the caller and hashed here; this is
    /// the only moment it exists anywhere but the client's hands.
    /// </summary>
    public static async Task<Guid> CreateAsync(
        string keyId, string keyHash, string displayName, Guid groupId, DateTimeOffset? expiresAt,
        CallerContext caller, UnitOfWork work, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(work);

        return await work.Connection.ExecuteScalarAsync<Guid>(new CommandDefinition("""
            INSERT INTO federation.api_key
                (key_id, key_hash, display_name, group_id, created_by, expires_at)
            VALUES (@keyId, @keyHash, @displayName, @groupId, @ActorId, @expiresAt)
            RETURNING id;
            """, new { keyId, keyHash, displayName, groupId, ActorId = caller.UserId, expiresAt },
            work.Transaction, cancellationToken: ct));
    }

    /// <summary>
    /// Provisioned keys the caller may see, newest first, with no secret material.
    /// </summary>
    /// <remarks>
    /// A key has no scope of its own, but it acts through an access group that does. So a key is
    /// visible to exactly the callers who could have provisioned it: the group passes the same
    /// reachability test the escalation guard applies (<c>AccessGroupRepository.GroupVisiblePredicate</c>),
    /// keyed on <c>apikey.read</c>. A scoped administrator therefore never sees a key bound to
    /// PLATFORM-ADMINS or to another department's group — which is what made the unfiltered list
    /// a target map for cross-department key revocation. An unscoped caller sees every key. The
    /// column list is explicit and never includes <c>key_hash</c>.
    /// </remarks>
    /// <summary>Every API key the caller may see. Unbounded — test / diagnostic path.</summary>
    public async Task<IReadOnlyList<ApiKeySummary>> ListAsync(CallerContext caller, CancellationToken ct) =>
        (await ListAsync(caller, PageWindow.UpTo(int.MaxValue), ct)).Items;

    /// <summary>One page of API keys the caller may see, with the full match count.</summary>
    public async Task<PagedRows<ApiKeySummary>> ListAsync(
        CallerContext caller, PageWindow window, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);

        var p = AccessGroupRepository.GroupVisibilityParams(caller, "apikey.read");
        p.Add("limit", window.Limit);
        p.Add("offset", window.Offset);

        await using var c = await _dataSource.OpenConnectionAsync(ct);
        var rows = (await c.QueryAsync<ApiKeySummary, long, (ApiKeySummary K, long T)>(
            new CommandDefinition($"""
                SELECT k.id, k.key_id, k.display_name, k.group_id, ag.code AS group_code,
                       k.created_at, k.expires_at, k.last_used_at, k.revoked_at,
                       count(*) OVER() AS total_count
                FROM federation.api_key k
                JOIN federation.access_groups ag ON ag.id = k.group_id
                WHERE {AccessGroupRepository.GroupVisiblePredicate}
                ORDER BY k.created_at DESC, k.id
                LIMIT @limit OFFSET @offset;
                """, p, cancellationToken: ct),
            (k, t) => (k, t), splitOn: "total_count")).ToList();

        return new PagedRows<ApiKeySummary>(
            [.. rows.Select(r => r.K)], rows.Count > 0 ? (int)rows[0].T : 0);
    }

    /// <summary>
    /// Revokes a key. Idempotent: revoking an already-revoked key reports
    /// <see cref="RevokeOutcome.AlreadyRevoked"/> and changes nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One statement: the existence check, the conditional update and the pre-revocation values
    /// for the audit row come back together. Takes the <see cref="UnitOfWork"/> because the
    /// caller pairs this with an audit write in the same transaction.
    /// </para>
    /// <para>
    /// Authority-scoped: the <c>target</c> CTE carries the same visibility predicate as
    /// <c>ListAsync</c> (keyed on <c>apikey.manage</c>, the permission being exercised),
    /// and the <c>upd</c> CTE only fires when <c>target</c> is non-empty. A key whose group is
    /// outside the caller's reach comes back as <see cref="RevokeOutcome.NotFound"/> — identical
    /// to an unknown id, and left untouched — so <c>apikey.manage</c> can no longer revoke a key
    /// in another department or the platform-admin group.
    /// </para>
    /// </remarks>
    public static async Task<ApiKeyRevocation> RevokeAsync(
        Guid id, CallerContext caller, UnitOfWork work, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(work);

        var p = AccessGroupRepository.GroupVisibilityParams(caller, "apikey.manage");
        p.Add("id", id);
        p.Add("ActorId", caller.UserId);

        var row = await work.Connection.QuerySingleOrDefaultAsync<RevokeRow>(new CommandDefinition($"""
            WITH target AS (
                SELECT k.id, k.revoked_at, k.display_name, k.group_id
                FROM federation.api_key k
                JOIN federation.access_groups ag ON ag.id = k.group_id
                WHERE k.id = @id AND ({AccessGroupRepository.GroupVisiblePredicate})
            ),
            upd AS (
                UPDATE federation.api_key
                SET revoked_at = now(), revoked_by = @ActorId
                WHERE id = @id AND revoked_at IS NULL AND EXISTS (SELECT 1 FROM target)
                RETURNING id
            )
            SELECT t.display_name              AS display_name,
                   t.group_id                  AS group_id,
                   t.revoked_at                AS prior_revoked_at,
                   EXISTS (SELECT 1 FROM upd)  AS transitioned
            FROM target t;
            """, p, work.Transaction, cancellationToken: ct));

        if (row is null)
        {
            return new ApiKeyRevocation(RevokeOutcome.NotFound, "", Guid.Empty, null);
        }

        return new ApiKeyRevocation(
            row.Transitioned ? RevokeOutcome.Revoked : RevokeOutcome.AlreadyRevoked,
            row.DisplayName, row.GroupId, row.PriorRevokedAt);
    }

    private sealed record RevokeRow
    {
        public string DisplayName { get; init; } = "";
        public Guid GroupId { get; init; }
        public DateTimeOffset? PriorRevokedAt { get; init; }
        public bool Transitioned { get; init; }
    }

    /// <summary>
    /// Stamps last use, but only when it is stale — <c>last_used_at IS NULL</c> or older than
    /// five minutes. Coarse on purpose (finding 8-NEW-H): a precise per-request timestamp is a
    /// write on every machine-to-machine request for a field nobody reads at second precision.
    /// Returns <see langword="true"/> when it actually wrote, which the auth handler uses to
    /// sample the auth-audit success row. Best effort: failing this must not deny a valid caller.
    /// </summary>
    public async Task<bool> TouchAsync(Guid keyId, CancellationToken ct)
    {
        await using var c = await _dataSource.OpenConnectionAsync(ct);

        return await c.ExecuteScalarAsync<bool?>(new CommandDefinition("""
            UPDATE federation.api_key
            SET last_used_at = now()
            WHERE id = @keyId
              AND (last_used_at IS NULL OR last_used_at < now() - @Interval)
            RETURNING TRUE;
            """, new { keyId, Interval = TimeSpan.FromMinutes(5) },
            cancellationToken: ct)) ?? false;
    }
}
