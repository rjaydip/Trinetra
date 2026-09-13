using Dapper;
using Npgsql;

namespace Trinetra.Federation.Storage.Repositories;

/// <summary>An authenticated camera credential test as stored, with its result still as raw JSON.</summary>
// Init properties, not a positional record -- Dapper's positional-record binding matches the
// reader's column TYPES to constructor parameters exactly, so a timestamptz (surfaced by Npgsql
// as DateTime) will not bind to a DateTimeOffset parameter. Property binding converts, which is
// what lets this keep DateTimeOffset everywhere per CLAUDE.md.
public sealed record CameraCredentialTestRow
{
    public Guid Id { get; init; }
    public Guid CameraId { get; init; }
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
/// Asynchronous authenticated credential tests against an already-registered camera.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately not <see cref="CameraRepository"/>-scoped in its own right: the endpoint reaches
/// the camera through <see cref="CameraRepository"/> first (out-of-scope reads as 404 there), and
/// this repository only ever operates on a <c>camera_id</c> the caller has already been proven to
/// reach. Re-scoping every read here as well would need a join back through <c>cameras</c> for no
/// additional safety.
/// </para>
/// <para>
/// Backed by a table rather than in-memory state, same reason as
/// <see cref="CameraConnectionTestRepository"/> and <see cref="ConnectionTestRepository"/>:
/// several API instances may run, and the poll may land on a different one than the instance that
/// started the test.
/// </para>
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Performance", "CA1822:Mark members as static",
    Justification = "The write method takes its connection from the UnitOfWork by design.",
    Scope = "type")]
public sealed class CameraCredentialTestRepository
{
    private readonly NpgsqlDataSource _dataSource;

    public CameraCredentialTestRepository(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    /// <summary>
    /// Books a test, snapshotting the camera's protocol/ip/port/credential reference at claim
    /// time. Shares the caller's transaction with its audit row.
    /// </summary>
    public async Task<Guid> ClaimAsync(
        Guid cameraId, string protocol, string ipAddress, int port, string credentialReference,
        string actor, UnitOfWork work, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(work);

        return await work.Connection.ExecuteScalarAsync<Guid>(new CommandDefinition("""
            INSERT INTO federation.camera_credential_test
                (camera_id, protocol, ip_address, port, credential_reference, requested_by)
            VALUES (@cameraId, @protocol, @ipAddress::inet, @port, @credentialReference, @actor)
            RETURNING id;
            """, new { cameraId, protocol, ipAddress, port, credentialReference, actor },
            work.Transaction, cancellationToken: ct));
    }

    public async Task<CameraCredentialTestRow?> GetAsync(Guid testId, CancellationToken ct)
    {
        await using var c = await _dataSource.OpenConnectionAsync(ct);

        return await c.QuerySingleOrDefaultAsync<CameraCredentialTestRow>(new CommandDefinition("""
            SELECT id, camera_id, protocol, host(ip_address) AS ip_address, port, status,
                   requested_at, completed_at, failure_reason, result::text AS result_json
            FROM federation.camera_credential_test
            WHERE id = @testId;
            """, new { testId }, cancellationToken: ct));
    }

    /// <summary>Test history for one camera, most recent first.</summary>
    public async Task<IReadOnlyList<CameraCredentialTestRow>> ListAsync(
        Guid cameraId, CancellationToken ct)
    {
        await using var c = await _dataSource.OpenConnectionAsync(ct);

        var rows = await c.QueryAsync<CameraCredentialTestRow>(new CommandDefinition("""
            SELECT id, camera_id, protocol, host(ip_address) AS ip_address, port, status,
                   requested_at, completed_at, failure_reason, result::text AS result_json
            FROM federation.camera_credential_test
            WHERE camera_id = @cameraId
            ORDER BY requested_at DESC;
            """, new { cameraId }, cancellationToken: ct));

        return [.. rows];
    }
}
