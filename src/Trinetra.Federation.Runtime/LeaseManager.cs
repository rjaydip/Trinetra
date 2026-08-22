using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Trinetra.Federation.Core.Model;
using Trinetra.Federation.Storage;

namespace Trinetra.Federation.Runtime;

/// <summary>
/// Owns this worker's set of connector targets: claims new ones, renews the lease on those
/// held, and releases everything on clean shutdown.
/// </summary>
/// <remarks>
/// <para>
/// This is the whole of the platform's work-assignment machinery. There is no coordinator and
/// no leader election — see architecture §4 for why PostgreSQL leases were chosen over
/// ZooKeeper/etcd on bare metal.
/// </para>
/// <para>
/// Losing a lease is treated as a first-class event rather than an error to retry through: it
/// means another worker now owns that target, so this worker must stop polling it immediately.
/// Continuing would double the request rate seen by the vendor and duplicate every event.
/// </para>
/// </remarks>
public sealed partial class LeaseManager : BackgroundService
{
    private readonly LeaseStore _leases;
    private readonly WorkerOptions _options;
    private readonly ILogger<LeaseManager> _logger;
    private readonly Dictionary<Guid, ConnectorTarget> _held = new();
    private readonly Lock _gate = new();

    public LeaseManager(
        LeaseStore leases,
        IOptions<WorkerOptions> options,
        ILogger<LeaseManager> logger)
    {
        _leases = leases;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>Targets currently owned by this worker.</summary>
    public IReadOnlyCollection<ConnectorTarget> HeldTargets
    {
        get
        {
            lock (_gate)
            {
                return _held.Values.ToList();
            }
        }
    }

    /// <summary>Raised when a target is newly claimed, so the runtime can start polling it.</summary>
    public event Action<ConnectorTarget>? TargetAcquired;

    /// <summary>Raised when a lease is lost. Handlers must stop polling that target at once.</summary>
    public event Action<Guid>? TargetReleased;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _options.Validate();
        LogStarting(_logger, _options.WorkerId, _options.MaxTargets, _options.LeaseTtl);

        using var renewTimer = new PeriodicTimer(_options.RenewInterval);
        var nextClaim = DateTimeOffset.UtcNow;

        try
        {
            while (await renewTimer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await RenewHeldAsync(stoppingToken).ConfigureAwait(false);

                if (DateTimeOffset.UtcNow >= nextClaim)
                {
                    await ClaimMoreAsync(stoppingToken).ConfigureAwait(false);
                    nextClaim = DateTimeOffset.UtcNow + _options.ClaimInterval;
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        finally
        {
            // Release rather than let leases expire: this turns a rolling restart from
            // "every target dark for a full TTL" into "dark for the restart itself".
            await ReleaseAllAsync().ConfigureAwait(false);
        }
    }

    private async Task RenewHeldAsync(CancellationToken cancellationToken)
    {
        List<Guid> expected;
        lock (_gate)
        {
            if (_held.Count == 0)
            {
                return;
            }

            expected = [.. _held.Keys];
        }

        IReadOnlyList<Guid> stillHeld;
        try
        {
            stillHeld = await _leases.RenewAsync(_options.WorkerId, _options.LeaseTtl, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A failed renewal is not yet a lost lease — the TTL has not necessarily elapsed.
            // Keep the targets and try again next tick rather than thrashing ownership on a
            // transient database blip.
            LogRenewFailed(_logger, ex);
            return;
        }

        var lost = expected.Except(stillHeld).ToList();
        foreach (var targetId in lost)
        {
            lock (_gate)
            {
                _held.Remove(targetId);
            }

            LogLeaseLost(_logger, targetId);
            TargetReleased?.Invoke(targetId);
        }
    }

    private async Task ClaimMoreAsync(CancellationToken cancellationToken)
    {
        int capacity;
        lock (_gate)
        {
            capacity = _options.MaxTargets - _held.Count;
        }

        if (capacity <= 0)
        {
            return;
        }

        IReadOnlyList<ConnectorTarget> claimed;
        try
        {
            claimed = await _leases.ClaimAsync(
                _options.WorkerId,
                capacity,
                _options.LeaseTtl,
                _options.RuntimeClass,
                _options.VendorFilter.Count > 0 ? _options.VendorFilter.ToList() : null,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogClaimFailed(_logger, ex);
            return;
        }

        foreach (var target in claimed)
        {
            lock (_gate)
            {
                _held[target.Id] = target;
            }

            LogClaimed(_logger, target.Id, target.Vendor, target.DisplayName);
            TargetAcquired?.Invoke(target);
        }
    }

    private async Task ReleaseAllAsync()
    {
        try
        {
            // Deliberately not passing the stopping token: this must run during shutdown,
            // when that token is already cancelled.
            var released = await _leases.ReleaseAllAsync(_options.WorkerId, CancellationToken.None)
                .ConfigureAwait(false);
            LogReleased(_logger, released);
        }
        catch (Exception ex)
        {
            // Best effort. If this fails the leases simply expire on their TTL, which is the
            // same path a crash takes — slower, but correct.
            LogReleaseFailed(_logger, ex);
        }

        lock (_gate)
        {
            _held.Clear();
        }
    }

    // LoggerMessage source generators: this runs on every renew tick across hundreds of
    // workers, so allocation-free logging is worth the ceremony (CA1848).

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Lease manager starting. WorkerId={WorkerId} MaxTargets={MaxTargets} LeaseTtl={LeaseTtl}")]
    private static partial void LogStarting(ILogger logger, string workerId, int maxTargets, TimeSpan leaseTtl);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Claimed target {TargetId} ({Vendor}) - {DisplayName}")]
    private static partial void LogClaimed(ILogger logger, Guid targetId, VendorKind vendor, string displayName);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Lost lease on target {TargetId}; another worker has taken ownership. Polling stopped.")]
    private static partial void LogLeaseLost(ILogger logger, Guid targetId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Lease renewal failed; retrying next tick")]
    private static partial void LogRenewFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Target claim failed; retrying next interval")]
    private static partial void LogClaimFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Released {Count} lease(s) on shutdown")]
    private static partial void LogReleased(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Failed to release leases on shutdown; they will expire on their TTL instead")]
    private static partial void LogReleaseFailed(ILogger logger, Exception exception);
}
