using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Trinetra.Federation.Adapters;
using Trinetra.Federation.Core.Model;
using Trinetra.Federation.Storage;

namespace Trinetra.Federation.Runtime;

/// <summary>
/// Starts and stops a <see cref="TargetWorker"/> as leases are acquired and lost.
/// </summary>
/// <remarks>
/// <para>
/// The bridge between ownership and work: <see cref="LeaseManager"/> decides which targets this
/// process owns, and this class makes that decision real.
/// </para>
/// <para>
/// <b>Releasing is fire-and-forget by design.</b> A lost lease means another worker already owns
/// the target, so this one must stop <i>now</i>. Blocking the lease loop while a dead device
/// times out would delay every other renewal on this worker and risk losing further leases —
/// turning one unreachable site into a cascade.
/// </para>
/// </remarks>
public sealed partial class ConnectorSupervisor : BackgroundService
{
    private readonly LeaseManager _leases;
    private readonly IAdapterFactory _adapters;
    private readonly EventStore _events;
    private readonly ConnectorStateStore _state;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<ConnectorSupervisor> _logger;

    private readonly ConcurrentDictionary<Guid, TargetWorker> _workers =
        new();

    private CancellationToken _shutdown = CancellationToken.None;

    public ConnectorSupervisor(
        LeaseManager leases,
        IAdapterFactory adapters,
        EventStore events,
        ConnectorStateStore state,
        ILoggerFactory loggerFactory,
        ILogger<ConnectorSupervisor> logger)
    {
        _leases = leases;
        _adapters = adapters;
        _events = events;
        _state = state;
        _loggerFactory = loggerFactory;
        _logger = logger;
    }

    /// <summary>Targets currently being polled by this process.</summary>
    public int ActiveWorkerCount => _workers.Count;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _shutdown = stoppingToken;

        _leases.TargetAcquired += OnTargetAcquired;
        _leases.TargetReleased += OnTargetReleased;

        try
        {
            // Partitions are created ahead of need at startup. If one is missing at insert time
            // events land in the DEFAULT partition, which is unindexed for range scans and then
            // blocks the correct partition from ever being created.
            var created = await _events.EnsurePartitionsAsync(daysAhead: 14, stoppingToken)
                .ConfigureAwait(false);
            LogPartitionsEnsured(_logger, created);

            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        finally
        {
            _leases.TargetAcquired -= OnTargetAcquired;
            _leases.TargetReleased -= OnTargetReleased;

            await StopAllAsync().ConfigureAwait(false);
        }
    }

    private void OnTargetAcquired(ConnectorTarget target)
    {
        if (!_adapters.Supports(target.Vendor))
        {
            // No adapter exists for this vendor. Logged once here rather than failing repeatedly
            // inside a worker that could never have succeeded.
            LogUnsupportedVendor(_logger, target.Id, target.Vendor);
            return;
        }

        var worker = new TargetWorker(
            target, _adapters, _events, _state, _loggerFactory.CreateLogger<TargetWorker>());

        if (!_workers.TryAdd(target.Id, worker))
        {
            // Already running: a duplicate acquire, which is harmless but worth not doubling up on.
            return;
        }

        worker.Start(_shutdown);
        LogWorkerStarted(_logger, target.Id, _workers.Count);
    }

    private void OnTargetReleased(Guid targetId)
    {
        if (!_workers.TryRemove(targetId, out var worker))
        {
            return;
        }

        LogWorkerStopping(_logger, targetId);

        // Deliberately not awaited — see the class remarks. Stopping is bounded internally.
        _ = Task.Run(async () =>
        {
            try
            {
                await worker.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogWorkerStopFailed(_logger, targetId, ex);
            }
        });
    }

    private async Task StopAllAsync()
    {
        var workers = _workers.Values.ToList();
        _workers.Clear();

        // Stopped in parallel: sequentially unwinding 200 targets, each with its own bounded
        // shutdown budget, would exceed any sane service stop timeout.
        await Task.WhenAll(workers.Select(async w =>
        {
            try
            {
                await w.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogWorkerStopFailed(_logger, w.TargetId, ex);
            }
        })).ConfigureAwait(false);

        LogAllWorkersStopped(_logger, workers.Count);
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Ensured event partitions ({Created} created)")]
    private static partial void LogPartitionsEnsured(ILogger logger, int created);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Started worker for target {TargetId} ({ActiveCount} active on this process)")]
    private static partial void LogWorkerStarted(ILogger logger, Guid targetId, int activeCount);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Stopping worker for target {TargetId}; lease no longer held")]
    private static partial void LogWorkerStopping(ILogger logger, Guid targetId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Worker for target {TargetId} failed to stop cleanly")]
    private static partial void LogWorkerStopFailed(ILogger logger, Guid targetId, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "All {Count} worker(s) stopped")]
    private static partial void LogAllWorkersStopped(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Target {TargetId} uses vendor {Vendor}, which has no registered adapter. "
                + "It will not be federated until one is implemented or the target is reconfigured.")]
    private static partial void LogUnsupportedVendor(ILogger logger, Guid targetId, VendorKind vendor);
}
