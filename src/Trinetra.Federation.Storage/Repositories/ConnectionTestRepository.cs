using Dapper;
using Npgsql;

namespace Trinetra.Federation.Storage.Repositories;

/// <summary>A connection test as stored, with its result still as raw JSON.</summary>
// Init properties, not positional records. Dapper binds a positional record by matching the
// reader's column TYPES to constructor parameters exactly, so a timestamptz (which Npgsql
// surfaces as DateTime) will not bind to a DateTimeOffset parameter and the query throws at
// runtime. Property binding converts, which is what lets these keep DateTimeOffset -- required
// everywhere by CLAUDE.md, because a naive timestamp silently corrupts time-window correlation.
public sealed record ConnectionTestRow
{
    public Guid Id { get; init; }
    public Guid TargetId { get; init; }
    public string Status { get; init; } = "";
    public DateTimeOffset RequestedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public string? FailureReason { get; init; }
    public string? ResultJson { get; init; }
}

/// <summary>One line of a target's recent test history.</summary>
public sealed record ConnectionTestSummaryRow
{
    public Guid Id { get; init; }
    public string Status { get; init; } = "";
    public string RequestedBy { get; init; } = "";
    public DateTimeOffset RequestedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public string? FailureReason { get; init; }
}

/// <summary>
/// Asynchronous connection tests against a vendor device.
/// </summary>
/// <remarks>
/// Backed by a table rather than in-memory state because several API instances may run, and a
/// result has to be readable from an instance other than the one that produced it.
/// </remarks>
// The two write methods take their connection from the UnitOfWork, so they touch no instance
// state. They stay instance methods so this type is called one way, not two.
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Performance", "CA1822:Mark members as static",
    Justification = "Write methods take their connection from the UnitOfWork by design.",
    Scope = "type")]
public sealed class ConnectionTestRepository
{
    private readonly NpgsqlDataSource _dataSource;

    public ConnectionTestRepository(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    /// <summary>The test already running for a target, if any.</summary>
    /// <remarks>
    /// One live test per target. Without this a caller can queue hundreds against one device, and
    /// cheap NVRs cap concurrent sessions in single digits — the tests would deny service to the
    /// operators actually watching the cameras.
    /// </remarks>
    public async Task<Guid?> InFlightAsync(Guid targetId, UnitOfWork work, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(work);

        return await work.Connection.ExecuteScalarAsync<Guid?>(new CommandDefinition("""
            SELECT id FROM federation.connection_test
            WHERE target_id = @targetId AND status IN ('pending', 'running')
            ORDER BY requested_at DESC LIMIT 1;
            """, new { targetId }, work.Transaction, cancellationToken: ct));
    }

    /// <summary>Books a test. Shares the caller's transaction with its audit row.</summary>
    public async Task<Guid> ClaimAsync(
        Guid targetId, string actor, UnitOfWork work, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(work);

        return await work.Connection.ExecuteScalarAsync<Guid>(new CommandDefinition("""
            INSERT INTO federation.connection_test (target_id, requested_by)
            VALUES (@targetId, @actor) RETURNING id;
            """, new { targetId, actor }, work.Transaction, cancellationToken: ct));
    }

    public async Task<ConnectionTestRow?> GetAsync(
        Guid targetId, Guid testId, CancellationToken ct)
    {
        await using var c = await _dataSource.OpenConnectionAsync(ct);

        // Both ids in the predicate: a test id alone would let a caller who can reach one target
        // read the result of a test against another.
        return await c.QuerySingleOrDefaultAsync<ConnectionTestRow>(new CommandDefinition("""
            SELECT id, target_id, status, requested_at, completed_at, failure_reason,
                   result::text AS result_json
            FROM federation.connection_test
            WHERE id = @testId AND target_id = @targetId;
            """, new { testId, targetId }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<ConnectionTestSummaryRow>> ListAsync(
        Guid targetId, CancellationToken ct)
    {
        await using var c = await _dataSource.OpenConnectionAsync(ct);

        var rows = await c.QueryAsync<ConnectionTestSummaryRow>(new CommandDefinition("""
            SELECT id, status, requested_by, requested_at, completed_at, failure_reason
            FROM federation.connection_test
            WHERE target_id = @targetId ORDER BY requested_at DESC LIMIT 20;
            """, new { targetId }, cancellationToken: ct));

        return [.. rows];
    }
}
