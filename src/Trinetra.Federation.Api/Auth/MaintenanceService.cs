using Trinetra.Federation.Storage.Repositories;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Trinetra.Federation.Storage;

namespace Trinetra.Federation.Api.Auth;

/// <summary>
/// Periodic housekeeping the API owns: partition creation, retention, and stale job cleanup.
/// </summary>
/// <remarks>
/// <para>
/// Partition creation and the stale-job sweep are cheap and idempotent, so every instance runs
/// them and no cron job has to be remembered.
/// </para>
/// <para>
/// Retention is different: it deletes irreversibly, so it runs <b>once per day for the whole
/// fleet</b>, claimed through <c>try_claim_daily_job</c>. Every instance still attempts it; the
/// database decides which one wins.
/// </para>
/// </remarks>
public sealed partial class MaintenanceService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);
    private const string RetentionJob = "retention";

    private readonly MaintenanceRepository _maintenance;
    // Only for UnitOfWork.BeginAsync; this project can execute no SQL of its own.
    private readonly NpgsqlDataSource _dataSource;
    private readonly RetentionOptions _retention;
    private readonly ILogger<MaintenanceService> _logger;

    public MaintenanceService(
        MaintenanceRepository maintenance,
        NpgsqlDataSource dataSource,
        IOptions<RetentionOptions> retention,
        ILogger<MaintenanceService> logger)
    {
        ArgumentNullException.ThrowIfNull(retention);

        _maintenance = maintenance;
        _dataSource = dataSource;
        _retention = retention.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);

        do
        {
            try
            {
                await using var connection = await _maintenance.OpenAsync(stoppingToken);

                // Ahead of need: a missing partition sends rows to DEFAULT, which is unindexed
                // for range scans and then blocks the correct partition from being created.
                await _maintenance.EnsurePartitionsAsync(connection, stoppingToken);

                var swept = await _maintenance.SweepAbandonedTestsAsync(connection, stoppingToken);

                // Non-zero means an API instance died mid-test. Worth knowing about.
                if (swept > 0)
                {
                    LogSwept(_logger, swept);
                }

                var sweptCameraTests =
                    await _maintenance.SweepAbandonedCameraTestsAsync(connection, stoppingToken);

                if (sweptCameraTests > 0)
                {
                    LogSweptCameraTests(_logger, sweptCameraTests);
                }

                var sweptCameraCredentialTests =
                    await _maintenance.SweepAbandonedCameraCredentialTestsAsync(connection, stoppingToken);

                if (sweptCameraCredentialTests > 0)
                {
                    LogSweptCameraCredentialTests(_logger, sweptCameraCredentialTests);
                }

                await RunRetentionIfDueAsync(connection, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Maintenance failing must never stop the API serving requests.
                LogMaintenanceFailed(_logger, ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>Drops expired partitions, at most once a day across the whole fleet.</summary>
    /// <remarks>
    /// Ordered deliberately: the claim is taken <b>before</b> anything is dropped, so a crash
    /// part-way through does not leave the job unclaimed and let the next instance repeat the
    /// drops that already succeeded. Repeating them would be harmless today — dropping an
    /// already-dropped partition finds nothing — but the ordering is what keeps it harmless if a
    /// destructive step is ever added here.
    /// </remarks>
    private async Task RunRetentionIfDueAsync(
        NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;

        if (!_retention.Enabled || now.Hour < _retention.RunAtUtcHour)
        {
            return;
        }

        var claimed = await _maintenance.TryClaimDailyJobAsync(
            connection, RetentionJob, DateOnly.FromDateTime(now.UtcDateTime), cancellationToken);

        if (!claimed)
        {
            // Another instance has today's pass, or this one already ran it.
            return;
        }

        var cutoffs = _retention.CutoffsFrom(now);

        var events = await _maintenance.DropPartitionsAsync(
            connection, "drop_event_partitions_before", cutoffs.Events, cancellationToken);
        var health = await _maintenance.DropPartitionsAsync(
            connection, "drop_health_partitions_before", cutoffs.Health, cancellationToken);
        var cameraStatus = await _maintenance.DropPartitionsAsync(
            connection, "drop_camera_status_partitions_before", cutoffs.CameraStatus,
            cancellationToken);
        var auditParts = await _maintenance.DropPartitionsAsync(
            connection, "drop_audit_partitions_before", cutoffs.Audit, cancellationToken);
        var authAuditParts = await _maintenance.DropPartitionsAsync(
            connection, "drop_auth_audit_partitions_before", cutoffs.AuthAudit, cancellationToken);

        var tests = await _maintenance.PurgeAsync(
            connection, "purge_connection_tests_before", cutoffs.ConnectionTests, cancellationToken);

        // Same cutoff category as VMS connection tests -- both are ephemeral job rows, not worth
        // a second retention knob for.
        var cameraTests = await _maintenance.PurgeAsync(
            connection, "purge_camera_connection_tests_before", cutoffs.ConnectionTests,
            cancellationToken);

        // Same cutoff category again -- another ephemeral job row, not worth a third knob for.
        var cameraCredentialTests = await _maintenance.PurgeAsync(
            connection, "purge_camera_credential_tests_before", cutoffs.ConnectionTests,
            cancellationToken);

        var deadLetter = await _maintenance.PurgeAsync(
            connection, "purge_deadletter_before", cutoffs.DeadLetter, cancellationToken);

        var summary =
            $"events={events.Count} health={health.Count} "
            + $"cameraStatus={cameraStatus.Count} audit={auditParts.Count} "
            + $"authAudit={authAuditParts.Count} "
            + $"connectionTests={tests} cameraConnectionTests={cameraTests} "
            + $"cameraCredentialTests={cameraCredentialTests} deadLetter={deadLetter}";

        LogRetention(_logger, events.Count, health.Count, cameraStatus.Count,
            auditParts.Count, authAuditParts.Count, tests, cameraTests, deadLetter);

        // Named individually at Information: after an incident, "which day did we lose?" must be
        // answerable from the logs rather than inferred from what is missing.
        foreach (var partition in events.Concat(health).Concat(cameraStatus)
                     .Concat(auditParts).Concat(authAuditParts))
        {
            LogDropped(_logger, partition);
        }

        await _maintenance.RecordJobDetailAsync(connection, RetentionJob, summary, cancellationToken);

        // Irreversible deletion belongs in the audit trail, not only in a log file that rotates.
        // Its own unit: the partitions are already gone by the time this runs, so there is
        // nothing to roll back -- but audit rows are only writable through a UnitOfWork, which
        // is what stops any other path from recording a change outside its transaction.
        await using var work = await UnitOfWork.BeginAsync(_dataSource, cancellationToken);

        await work.AuditAsync(
            CallerContext.System("maintenance"), "delete", "retention",
            cutoffs.Events.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            before: null,
            after: new
            {
                eventPartitions = events,
                healthPartitions = health,
                cameraStatusPartitions = cameraStatus,
                auditPartitions = auditParts,
                authAuditPartitions = authAuditParts,
                connectionTestsPurged = tests,
                cameraConnectionTestsPurged = cameraTests,
                cameraCredentialTestsPurged = cameraCredentialTests,
                deadLetterPurged = deadLetter,
            },
            organizationUnitId: null,
            cancellationToken);

        await work.CommitAsync(cancellationToken);
    }


    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Marked {Count} connection test(s) abandoned. An API instance most likely "
                + "stopped while running them.")]
    private static partial void LogSwept(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Marked {Count} camera reachability test(s) abandoned. An API instance most "
                + "likely stopped while running them.")]
    private static partial void LogSweptCameraTests(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Marked {Count} camera credential test(s) abandoned. An API instance most "
                + "likely stopped while running them.")]
    private static partial void LogSweptCameraCredentialTests(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Retention pass complete. Dropped {EventPartitions} event, {HealthPartitions} "
                + "health and {CameraStatusPartitions} camera-status partition(s), "
                + "{AuditPartitions} audit and {AuthAuditPartitions} auth-audit partition(s); "
                + "purged {Tests} connection test(s), {CameraTests} camera connection test(s) "
                + "and {DeadLetter} dead-letter row(s).")]
    private static partial void LogRetention(
        ILogger logger, int eventPartitions, int healthPartitions, int cameraStatusPartitions,
        int auditPartitions, int authAuditPartitions, int tests, int cameraTests, int deadLetter);

    [LoggerMessage(Level = LogLevel.Information, Message = "Dropped partition {Partition}")]
    private static partial void LogDropped(ILogger logger, string partition);

    [LoggerMessage(Level = LogLevel.Error, Message = "Scheduled maintenance pass failed")]
    private static partial void LogMaintenanceFailed(ILogger logger, Exception exception);
}
