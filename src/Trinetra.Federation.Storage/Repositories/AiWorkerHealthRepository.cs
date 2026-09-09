using Dapper;
using Npgsql;

namespace Trinetra.Federation.Storage.Repositories;

/// <summary>Last-known heartbeat for one AI-worker process, under the key that reports it.</summary>
public sealed record AiWorkerHealthRow
{
    public Guid Id { get; init; }
    public Guid ApiKeyId { get; init; }
    public string ApiKeyName { get; init; } = "";
    public string WorkerId { get; init; } = "";
    public string Hostname { get; init; } = "";
    public DateTimeOffset FirstSeenAt { get; init; }
    public DateTimeOffset LastHeartbeatAt { get; init; }
    public DateTimeOffset? ReportedAt { get; init; }
}

/// <summary>
/// Tracks liveness for Model 2's AI workers (<c>ai-worker/monitoring/heartbeat.py</c>).
/// </summary>
/// <remarks>
/// <para>
/// Deliberately not <c>worker_node</c>: that table is the leased-connector-worker registry.
/// An AI worker owns a static camera partition rather than a lease, so it gets its own table
/// instead of overloading a different lifecycle's schema.
/// </para>
/// <para>
/// The heartbeat upsert is <b>not audited</b> — it is high-frequency observability telemetry in
/// the same class as <c>worker_node</c> lease renewals and <c>api_key.last_used_at</c>: it
/// records <i>that a process pinged</i>, it changes nothing about what any principal may do or
/// see. It still takes a <see cref="CallerContext"/>, because ownership of the row is bound to
/// the reporting API key (Finding 17-M1) — that is scoping, not audit. The retire path
/// (<see cref="RetireAsync"/>) <i>is</i> a mutation and is audited through a
/// <see cref="UnitOfWork"/>.
/// </para>
/// </remarks>
public sealed class AiWorkerHealthRepository
{
    private readonly NpgsqlDataSource _dataSource;

    public AiWorkerHealthRepository(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    /// <summary>
    /// Records a heartbeat under the caller's own API key. <paramref name="reportedAt"/> is the
    /// worker's claimed send time, kept only for drift diagnosis — <c>last_heartbeat_at</c> is
    /// always the server clock (Finding 17-M2).
    /// </summary>
    /// <remarks>
    /// The caller must be an API-key principal: a heartbeat is a machine liveness signal, and one
    /// a human could assert is no signal at all (Finding 17-M1). The endpoint rejects a non-key
    /// caller with 403 before reaching here; this method throws if that guard is ever bypassed.
    /// </remarks>
    public async Task UpsertHeartbeatAsync(
        CallerContext caller, string workerId, string hostname, DateTimeOffset? reportedAt,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require("worker.heartbeat");

        if (caller.ApiKeyId is not { } apiKeyId)
        {
            // The endpoint rejects a non-key caller with 403 before this method is reached; a
            // heartbeat has no row to own without a key. Reaching here means that guard was
            // bypassed.
            throw new InvalidOperationException(
                "A heartbeat requires an API-key principal; caller has no ApiKeyId.");
        }

        await using var c = await _dataSource.OpenConnectionAsync(ct);

        await c.ExecuteAsync(new CommandDefinition("""
            INSERT INTO federation.ai_worker_health
                (api_key_id, worker_id, hostname, last_heartbeat_at, reported_at)
            VALUES (@apiKeyId, @workerId, @hostname, now(), @reportedAt)
            ON CONFLICT (api_key_id, worker_id, hostname) DO UPDATE
            SET last_heartbeat_at = now(),
                reported_at       = EXCLUDED.reported_at;
            """, new { apiKeyId, workerId, hostname, reportedAt }, cancellationToken: ct));
    }

    /// <summary>
    /// One page of worker-health rows, with the full count. Not organization- or
    /// geography-scoped: an AI worker is not an org/geo entity (Finding 17-L3, same call as the
    /// API-key list). Gated by <c>worker.read</c>.
    /// </summary>
    public async Task<PagedRows<AiWorkerHealthRow>> ListAsync(
        CallerContext caller, PageWindow window, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require("worker.read");

        await using var c = await _dataSource.OpenConnectionAsync(ct);

        var rows = (await c.QueryAsync<AiWorkerHealthRow, long, (AiWorkerHealthRow R, long T)>(
            new CommandDefinition("""
                SELECT h.id AS Id, h.api_key_id AS ApiKeyId, k.display_name AS ApiKeyName,
                       h.worker_id AS WorkerId, h.hostname AS Hostname,
                       h.first_seen_at AS FirstSeenAt, h.last_heartbeat_at AS LastHeartbeatAt,
                       h.reported_at AS ReportedAt,
                       count(*) OVER() AS total_count
                FROM federation.ai_worker_health h
                JOIN federation.api_key k ON k.id = h.api_key_id
                ORDER BY h.api_key_id, h.worker_id, h.hostname
                LIMIT @limit OFFSET @offset;
                """, new { limit = window.Limit, offset = window.Offset }, cancellationToken: ct),
            (r, t) => (r, t), splitOn: "total_count")).ToList();

        return new PagedRows<AiWorkerHealthRow>(
            [.. rows.Select(x => x.R)], rows.Count > 0 ? PagedCount.From(rows[0].T) : 0);
    }

    /// <summary>
    /// Deletes one worker-health record — used to clear a decommissioned worker or the stale
    /// rows a fleet resize leaves behind (Finding 17-L1). Audited. Gated by <c>worker.manage</c>.
    /// </summary>
    // Takes its connection from the UnitOfWork rather than the data source, so CA1822 flags it
    // static-able. It stays an instance method so every operation on this entity is called the
    // same way — matches the other repositories.
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Performance", "CA1822:Mark members as static",
        Justification = "Takes its connection from the UnitOfWork by design.")]
    public async Task<AiWorkerHealthRow?> RetireAsync(
        Guid id, CallerContext caller, UnitOfWork work, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(work);
        caller.Require("worker.manage");

        var c = work.Connection;

        return await c.QuerySingleOrDefaultAsync<AiWorkerHealthRow>(new CommandDefinition("""
            DELETE FROM federation.ai_worker_health h
            USING federation.api_key k
            WHERE k.id = h.api_key_id AND h.id = @id
            RETURNING h.id AS Id, h.api_key_id AS ApiKeyId, k.display_name AS ApiKeyName,
                      h.worker_id AS WorkerId, h.hostname AS Hostname,
                      h.first_seen_at AS FirstSeenAt, h.last_heartbeat_at AS LastHeartbeatAt,
                      h.reported_at AS ReportedAt;
            """, new { id }, work.Transaction, cancellationToken: ct));
    }
}
