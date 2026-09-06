using Dapper;
using Npgsql;

namespace Trinetra.Federation.Storage.Repositories;

/// <summary>
/// A refresh-token row as the refresh path needs to evaluate it. No secret material.
/// </summary>
/// <remarks>
/// <see cref="IsExpired"/> and <see cref="IsReplayPastGrace"/> are computed against the
/// <b>database</b> clock (<c>now()</c>) in the query, not re-derived in C# — the API host and the
/// DB host can drift, and near an 8h boundary "which clock" must not change the answer.
/// </remarks>
public sealed record RefreshTokenRow
{
    public Guid Id { get; init; }
    public Guid UserId { get; init; }
    public DateTimeOffset? UsedAt { get; init; }
    public DateTimeOffset? RevokedAt { get; init; }
    public bool IsExpired { get; init; }

    /// <summary>The token has been rotated, and long enough ago that a re-presentation is not a
    /// two-tab race but a replay to be treated as theft.</summary>
    public bool IsReplayPastGrace { get; init; }
}

/// <summary>
/// Opaque sliding refresh tokens.
/// </summary>
/// <remarks>
/// <para>
/// Only the SHA-256 of the value is stored — a database dump must not yield working sessions.
/// The raw value exists only in the <c>/auth/login</c> and <c>/auth/refresh</c> response bodies.
/// </para>
/// <para>
/// Lifecycle mirrors <see cref="ApiKeyRepository"/>: instance reads off the data source, static
/// writes through a <see cref="UnitOfWork"/> so a rotation and its audit row commit together.
/// </para>
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Performance", "CA1822:Mark members as static",
    Justification = "Reads take the connection from the data source; kept instance for symmetry.",
    Scope = "type")]
public sealed class RefreshTokenRepository
{
    private readonly NpgsqlDataSource _dataSource;

    public RefreshTokenRepository(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    /// <summary>
    /// Looks a token up by hash, in whatever state, with its expiry and replay judgement computed
    /// against the database clock. The caller still decides what to do with a revoked / expired /
    /// replayed row — this keeps every "now" comparison on one clock.
    /// </summary>
    public async Task<RefreshTokenRow?> FindAsync(
        string tokenHash, TimeSpan reuseGrace, CancellationToken ct)
    {
        await using var c = await _dataSource.OpenConnectionAsync(ct);
        return await c.QuerySingleOrDefaultAsync<RefreshTokenRow>(new CommandDefinition("""
            SELECT id, user_id, used_at, revoked_at,
                   (expires_at <= now()) AS is_expired,
                   (used_at IS NOT NULL AND used_at <= now() - @reuseGrace) AS is_replay_past_grace
            FROM federation.refresh_token
            WHERE token_hash = @tokenHash;
            """, new { tokenHash, reuseGrace }, cancellationToken: ct));
    }

    /// <summary>
    /// Persists a freshly minted refresh token. <paramref name="expiresAt"/> is computed by the
    /// caller as <c>now + refresh lifetime</c>. Returns the new row id.
    /// </summary>
    public async Task<Guid> IssueAsync(
        Guid userId, string tokenHash, DateTimeOffset expiresAt,
        UnitOfWork work, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(work);
        return await work.Connection.ExecuteScalarAsync<Guid>(new CommandDefinition("""
            INSERT INTO federation.refresh_token (user_id, token_hash, expires_at)
            VALUES (@userId, @tokenHash, @expiresAt)
            RETURNING id;
            """, new { userId, tokenHash, expiresAt }, work.Transaction, cancellationToken: ct));
    }

    /// <summary>
    /// Marks a token rotated: <c>used_at</c> and <c>replaced_by</c> are set. Presenting it again
    /// is a replay outside the grace window. No-op if it was already used.
    /// </summary>
    public async Task MarkRotatedAsync(
        Guid oldId, Guid newId, UnitOfWork work, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(work);
        await work.Connection.ExecuteAsync(new CommandDefinition("""
            UPDATE federation.refresh_token
            SET used_at = now(), replaced_by = @newId
            WHERE id = @oldId AND used_at IS NULL;
            """, new { oldId, newId }, work.Transaction, cancellationToken: ct));
    }

    /// <summary>
    /// Revokes every not-yet-revoked refresh token for a user. Pairs with
    /// <c>UserRepository.BumpTokenVersionAsync</c> in one transaction to end every session.
    /// </summary>
    public async Task RevokeAllForUserAsync(Guid userId, UnitOfWork work, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(work);
        await work.Connection.ExecuteAsync(new CommandDefinition("""
            UPDATE federation.refresh_token
            SET revoked_at = now()
            WHERE user_id = @userId AND revoked_at IS NULL;
            """, new { userId }, work.Transaction, cancellationToken: ct));
    }
}
