using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Trinetra.Federation.Storage;

namespace Trinetra.Federation.Worker;

/// <summary>
/// Keeps every standalone ONVIF camera's synthetic <c>connector_target</c> and reconciliation
/// link up to date, so <c>LeaseManager</c>/<c>ConnectorSupervisor</c>/<c>TargetWorker</c> — the
/// existing VMS pipeline — picks it up and pulls its onboard analytics events (crowd, intrusion,
/// motion, ...) into <c>federation_event</c> exactly as it already does for a real VMS. See
/// <see cref="SyntheticCameraTargetStore"/>'s remarks for why this is a sync job rather than a
/// second event-pulling client.
/// </summary>
public sealed partial class SyntheticCameraTargetSyncRunner : BackgroundService
{
    // Cheap relative to an ONVIF handshake (a handful of UPDATE/INSERT statements over an
    // already-small cameras/targets table) and only needs to stay ahead of how fast an operator
    // can add a standalone camera and expect it to start being polled for events — a much looser
    // requirement than CameraHealthCheckRunner's per-camera probe cadence.
    private static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(1);

    private readonly SyntheticCameraTargetStore _store;
    private readonly ILogger<SyntheticCameraTargetSyncRunner> _logger;

    public SyntheticCameraTargetSyncRunner(
        SyntheticCameraTargetStore store, ILogger<SyntheticCameraTargetSyncRunner> logger)
    {
        _store = store;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogStarting(_logger, TickInterval);

        using var timer = new PeriodicTimer(TickInterval);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    var (created, retired) = await _store.SyncTargetsAsync(stoppingToken).ConfigureAwait(false);
                    var reconciled = await _store.AutoReconcileAsync(stoppingToken).ConfigureAwait(false);

                    if (created > 0 || retired > 0 || reconciled > 0)
                    {
                        LogSynced(_logger, created, retired, reconciled);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Same posture as CorrelationRunner/CameraHealthCheckRunner: one bad tick
                    // (a DB blip) must not take down the whole worker process.
                    LogSyncFailed(_logger, ex);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Synthetic camera-target sync runner starting. TickInterval={TickInterval}")]
    private static partial void LogStarting(ILogger logger, TimeSpan tickInterval);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Synthetic camera-target sync: {Created} created, {Retired} retired, {Reconciled} auto-reconciled")]
    private static partial void LogSynced(ILogger logger, int created, int retired, int reconciled);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Synthetic camera-target sync failed; retrying next tick")]
    private static partial void LogSyncFailed(ILogger logger, Exception exception);
}
