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

    /// <summary>Stamps last use. Best effort: failing this must not deny a valid caller.</summary>
    public async Task TouchAsync(Guid keyId, CancellationToken ct)
    {
        await using var c = await _dataSource.OpenConnectionAsync(ct);

        await c.ExecuteAsync(new CommandDefinition(
            "UPDATE federation.api_key SET last_used_at = now() WHERE id = @keyId;",
            new { keyId }, cancellationToken: ct));
    }
}
