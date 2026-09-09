using Dapper;
using Npgsql;

namespace Trinetra.Federation.Storage.Repositories;

/// <summary>Last-known heartbeat for one AI-worker process.</summary>
public sealed record AiWorkerHealthRow
{
    public string WorkerId { get; init; } = "";
    public string? Hostname { get; init; }
    public DateTimeOffset FirstSeenAt { get; init; }
    public DateTimeOffset LastHeartbeatAt { get; init; }
}

/// <summary>
/// Tracks liveness for Model 2's AI workers (<c>ai-worker/monitoring/heartbeat.py</c>).
/// </summary>
/// <remarks>
/// Deliberately not <c>worker_node</c>: that table is the leased-connector-worker registry.
/// An AI worker owns a static camera partition rather than a lease, so it gets its own table
/// instead of overloading a different lifecycle's schema. Non-sensitive and high-frequency, so
/// this upsert is not audited — the same treatment <c>worker_node</c> already gets.
/// </remarks>
public sealed class AiWorkerHealthRepository
{
    private readonly NpgsqlDataSource _dataSource;

    public AiWorkerHealthRepository(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task UpsertHeartbeatAsync(
        string workerId, string? hostname, DateTimeOffset reportedAt, CancellationToken ct)
    {
        await using var c = await _dataSource.OpenConnectionAsync(ct);

        await c.ExecuteAsync(new CommandDefinition("""
            INSERT INTO federation.ai_worker_health (worker_id, hostname, last_heartbeat_at)
            VALUES (@workerId, @hostname, @reportedAt)
            ON CONFLICT (worker_id) DO UPDATE
            SET hostname = EXCLUDED.hostname, last_heartbeat_at = EXCLUDED.last_heartbeat_at;
            """, new { workerId, hostname, reportedAt }, cancellationToken: ct));
    }

    /// <summary>One page of worker-health rows, with the full count.</summary>
    public async Task<PagedRows<AiWorkerHealthRow>> ListAsync(PageWindow window, CancellationToken ct)
    {
        await using var c = await _dataSource.OpenConnectionAsync(ct);

        var rows = (await c.QueryAsync<AiWorkerHealthRow, long, (AiWorkerHealthRow R, long T)>(
            new CommandDefinition("""
                SELECT worker_id, hostname, first_seen_at, last_heartbeat_at,
                       count(*) OVER() AS total_count
                FROM federation.ai_worker_health
                ORDER BY worker_id
                LIMIT @limit OFFSET @offset;
                """, new { limit = window.Limit, offset = window.Offset }, cancellationToken: ct),
            (r, t) => (r, t), splitOn: "total_count")).ToList();

        return new PagedRows<AiWorkerHealthRow>(
            [.. rows.Select(x => x.R)], rows.Count > 0 ? (int)rows[0].T : 0);
    }
}
