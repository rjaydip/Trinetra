using Dapper;
using Npgsql;

namespace Trinetra.Federation.Storage.Repositories;

/// <summary>A standalone-camera reachability test as stored, with its result still as raw JSON.</summary>
// Init properties, not a positional record -- Dapper's positional-record binding matches the
// reader's column TYPES to constructor parameters exactly, so a timestamptz (surfaced by Npgsql
// as DateTime) will not bind to a DateTimeOffset parameter. Property binding converts, which is
// what lets this keep DateTimeOffset everywhere per CLAUDE.md.
public sealed record CameraConnectionTestRow
{
    public Guid Id { get; init; }
    public string Protocol { get; init; } = "";
    public string IpAddress { get; init; } = "";
    public int Port { get; init; }
    public string Status { get; init; } = "";
    public DateTimeOffset RequestedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public string? FailureReason { get; init; }
    public string? ResultJson { get; init; }
}

/// <summary>
/// Asynchronous reachability tests for a standalone camera that does not exist as a registry row
/// yet -- the operator is testing host:port before deciding whether to save it.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately not <see cref="CameraRepository"/>-scoped: there is no camera row to scope
/// against, so this does not take a <see cref="CallerContext"/>. Authorization is the endpoint's
/// <c>camera.create</c> permission gate, same as registering the camera this test precedes.
/// </para>
/// <para>
/// Backed by a table rather than in-memory state for the same reason as
/// <see cref="ConnectionTestRepository"/>: several API instances may run, and the poll may land
/// on a different one than the instance that started the test.
/// </para>
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Performance", "CA1822:Mark members as static",
    Justification = "The write method takes its connection from the UnitOfWork by design.",
    Scope = "type")]
public sealed class CameraConnectionTestRepository
{
    private readonly NpgsqlDataSource _dataSource;

    public CameraConnectionTestRepository(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    /// <summary>Books a test. Shares the caller's transaction with its audit row.</summary>
    public async Task<Guid> ClaimAsync(
        string protocol, string ipAddress, int port, string actor, UnitOfWork work, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(work);

        return await work.Connection.ExecuteScalarAsync<Guid>(new CommandDefinition("""
            INSERT INTO federation.camera_connection_test
                (protocol, ip_address, port, requested_by)
            VALUES (@protocol, @ipAddress::inet, @port, @actor) RETURNING id;
            """, new { protocol, ipAddress, port, actor }, work.Transaction, cancellationToken: ct));
    }

    public async Task<CameraConnectionTestRow?> GetAsync(Guid testId, CancellationToken ct)
    {
        await using var c = await _dataSource.OpenConnectionAsync(ct);

        return await c.QuerySingleOrDefaultAsync<CameraConnectionTestRow>(new CommandDefinition("""
            SELECT id, protocol, host(ip_address) AS ip_address, port, status,
                   requested_at, completed_at, failure_reason, result::text AS result_json
            FROM federation.camera_connection_test
            WHERE id = @testId;
            """, new { testId }, cancellationToken: ct));
    }
}
