using Dapper;
using Npgsql;

namespace Trinetra.Federation.Storage.Repositories;

/// <summary>
/// Partition creation, retention and stale-job cleanup, as the database exposes them.
/// </summary>
/// <remarks>
/// The SQL lives here rather than in the hosted service so the API project contains none at all.
/// Retention in particular is irreversible, and a DROP written inline in a background loop is a
/// DROP nobody reviews as data-layer code.
/// </remarks>
// Most methods take the caller's connection so one maintenance pass runs on one connection.
// They stay instance methods so the type is called one way.
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Performance", "CA1822:Mark members as static",
    Justification = "Methods take the caller's connection so a pass shares one.",
    Scope = "type")]
public sealed class MaintenanceRepository
{
    private readonly NpgsqlDataSource _dataSource;

    public MaintenanceRepository(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    /// <summary>Creates the partitions the next fortnight of writes will need.</summary>
    /// <remarks>
    /// Ahead of need: a missing partition sends rows to DEFAULT, which is unindexed for range
    /// scans and then blocks the correct partition from being created at all.
    /// </remarks>
    public async Task EnsurePartitionsAsync(NpgsqlConnection c, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);

        await c.ExecuteAsync(new CommandDefinition("""
            SELECT federation.ensure_event_partitions(CURRENT_DATE, 14);
            SELECT federation.ensure_health_partitions(CURRENT_DATE, 14);
            SELECT federation.ensure_camera_status_partitions(CURRENT_DATE, 14);
            SELECT federation.ensure_audit_partitions();
            """, cancellationToken: ct));
    }

    /// <summary>Marks tests abandoned when the instance running them died.</summary>
    public async Task<int> SweepAbandonedTestsAsync(NpgsqlConnection c, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);

        return await c.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT federation.sweep_abandoned_connection_tests();", cancellationToken: ct));
    }

    /// <summary>Marks camera reachability probes abandoned when the instance running them died.</summary>
    public async Task<int> SweepAbandonedCameraTestsAsync(NpgsqlConnection c, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);

        return await c.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT federation.sweep_abandoned_camera_connection_tests();", cancellationToken: ct));
    }

    /// <summary>Marks authenticated camera credential tests abandoned when the instance running
    /// them died.</summary>
    public async Task<int> SweepAbandonedCameraCredentialTestsAsync(NpgsqlConnection c, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);

        return await c.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT federation.sweep_abandoned_camera_credential_tests();", cancellationToken: ct));
    }

    /// <summary>Claims today's run of a daily job for this instance.</summary>
    public async Task<bool> TryClaimDailyJobAsync(
        NpgsqlConnection c, string job, DateOnly today, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);

        return await c.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT federation.try_claim_daily_job(@job, @today);",
            new { job, today }, cancellationToken: ct));
    }

    /// <summary>Drops partitions older than a cutoff, returning what went.</summary>
    public async Task<IReadOnlyList<string>> DropPartitionsAsync(
        NpgsqlConnection c, string function, DateOnly cutoff, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);

        // The function name is chosen from a fixed set by the caller, never from input. The
        // cutoff is parameterised, and the SQL function refuses one that is not in the past.
        var dropped = await c.QueryAsync<string>(new CommandDefinition(
            $"SELECT dropped_partition FROM federation.{function}(@cutoff);",
            new { cutoff }, cancellationToken: ct));

        return [.. dropped];
    }

    public async Task<int> PurgeAsync(
        NpgsqlConnection c, string function, DateTimeOffset cutoff, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);

        return await c.ExecuteScalarAsync<int>(new CommandDefinition(
            $"SELECT federation.{function}(@cutoff);", new { cutoff }, cancellationToken: ct));
    }

    public async Task RecordJobDetailAsync(
        NpgsqlConnection c, string job, string detail, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);

        await c.ExecuteAsync(new CommandDefinition(
            "SELECT federation.record_job_detail(@job, @detail);",
            new { job, detail }, cancellationToken: ct));
    }

    /// <summary>Opens a connection for one maintenance pass.</summary>
    public Task<NpgsqlConnection> OpenAsync(CancellationToken ct) =>
        _dataSource.OpenConnectionAsync(ct).AsTask();
}
