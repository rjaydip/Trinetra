using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.CircuitBreaker;
using Polly.Timeout;
using Trinetra.Federation.Adapters;
using Trinetra.Federation.Core.Abstractions;
using Trinetra.Federation.Core.Capabilities;
using Trinetra.Federation.Core.Errors;
using Trinetra.Federation.Core.Events;
using Trinetra.Federation.Core.Model;
using Trinetra.Federation.Storage;

namespace Trinetra.Federation.Runtime;

/// <summary>
/// Drives one connector target: connects, polls inventory and status, and streams events.
/// </summary>
/// <remarks>
/// <para>
/// This is the link between the lease (which decides <i>what</i> this worker owns) and the
/// adapters (which know <i>how</i> to talk to a device). One instance per leased target; a
/// worker runs 50-200 of them concurrently in one process.
/// </para>
/// <para>
/// <b>Three activities run concurrently at different cadences</b>, because their costs and their
/// rates of change differ by orders of magnitude: inventory changes roughly daily, status every
/// 30 seconds, and events continuously. Merging them would force the expensive call onto the
/// fast loop.
/// </para>
/// <para>
/// <b>Losing the lease cancels everything immediately.</b> Continuing to poll a target another
/// worker now owns would double the request rate the vendor sees and duplicate every event —
/// and would be invisible in metrics, since both workers would look healthy.
/// </para>
/// </remarks>
public sealed partial class TargetWorker : IAsyncDisposable
{
    /// <summary>
    /// Events are batched before writing. A busy site produces many events per second, and a
    /// database round trip each would make storage the bottleneck the bus exists to avoid.
    /// </summary>
    private const int EventBatchSize = 200;

    private static readonly TimeSpan EventFlushInterval = TimeSpan.FromSeconds(2);

    private readonly ConnectorTarget _target;
    private readonly IAdapterFactory _adapters;
    private readonly EventStore _events;
    private readonly ConnectorStateStore _state;
    private readonly ILogger<TargetWorker> _logger;
    private readonly ResiliencePipeline _resilience;
    private readonly TokenBucket _rateLimit;
    private readonly CancellationTokenSource _stopping = new();

    private IVmsAdapter? _adapter;
    private Task? _running;
    private long _eventsSinceHealthCheck;
    private int _consecutiveFailures;
    private string? _lastError;
    private int? _lastCameraCount;

    public TargetWorker(
        ConnectorTarget target,
        IAdapterFactory adapters,
        EventStore events,
        ConnectorStateStore state,
        ILogger<TargetWorker> logger)
    {
        _target = target;
        _adapters = adapters;
        _events = events;
        _state = state;
        _logger = logger;
        _resilience = ConnectorResilience.Create(target, logger);

        // Politeness toward the device, applied before every call. Separate from the resilience
        // pipeline because it shapes normal traffic rather than reacting to failure.
        _rateLimit = new TokenBucket(target.RateLimitPerSecond, target.RateLimitBurst);
    }

    public Guid TargetId => _target.Id;

    /// <summary>Starts the worker. Returns immediately; work continues in the background.</summary>
    /// <remarks>
    /// The linked token source is disposed in a <c>finally</c> rather than a <c>ContinueWith</c>.
    /// A continuation would make <c>_running</c> refer to the disposal task, leaving any fault
    /// from <see cref="RunAsync"/> unobserved on a task nobody awaits — so a failure here would
    /// vanish instead of being logged.
    /// </remarks>
    public void Start(CancellationToken workerShutdown)
    {
        _running = RunGuardedAsync(workerShutdown);

        async Task RunGuardedAsync(CancellationToken shutdown)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                _stopping.Token, shutdown);

            await RunAsync(linked.Token).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Stops the worker and waits briefly for it to unwind.
    /// </summary>
    /// <remarks>
    /// The wait is bounded: a device that will not release a connection must not delay the
    /// worker's whole shutdown, and any subscription left behind lapses on its own timeout.
    /// </remarks>
    public async Task StopAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);

        if (_running is not null)
        {
            try
            {
                await _running.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
                LogStopTimedOut(_logger, _target.Id);
            }
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            _adapter = await _adapters.CreateAsync(_target, cancellationToken).ConfigureAwait(false);

            await _adapter.ConnectAsync(cancellationToken).ConfigureAwait(false);

            var capabilities = await _adapter.ProbeCapabilitiesAsync(cancellationToken)
                .ConfigureAwait(false);
            await _state.SaveCapabilitiesAsync(_target.Id, capabilities, cancellationToken)
                .ConfigureAwait(false);

            LogConnected(_logger, _target.Id, _target.Vendor, capabilities.Supported);

            // Independent cadences, one shared cancellation. If any one fails terminally the
            // others are torn down with it, because a target is federated as a whole or not at all.
            await Task.WhenAll(
                InventoryLoopAsync(cancellationToken),
                StatusLoopAsync(cancellationToken),
                EventLoopAsync(capabilities, cancellationToken),
                HealthLoopAsync(cancellationToken))
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal: lease lost or worker shutting down.
        }
        catch (AuthException ex)
        {
            // Permanent until an operator acts. Never retried: retrying rejected credentials
            // across an estate this size is how an integration account gets locked out everywhere.
            _lastError = ex.Message;
            LogAuthFailed(_logger, _target.Id, ex.Message);
            await RecordHealthAsync(HealthStatus.AuthFailed, null, null, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (ConfigurationException ex)
        {
            _lastError = ex.Message;
            LogMisconfigured(_logger, _target.Id, ex.Message);
            await RecordHealthAsync(HealthStatus.Unknown, null, null, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            LogWorkerFailed(_logger, _target.Id, ex);
            await RecordHealthAsync(HealthStatus.Unreachable, null, null, CancellationToken.None)
                .ConfigureAwait(false);
        }
    }

    // ---- Inventory: slow cadence -------------------------------------------

    private async Task InventoryLoopAsync(CancellationToken cancellationToken)
    {
        if (!_adapter!.Capabilities.Has(Capability.Inventory))
        {
            return;
        }

        using var timer = new PeriodicTimer(_target.InventoryPollInterval);

        do
        {
            try
            {
                var cameras = await CallAsync(
                    ct => _adapter!.GetCamerasAsync(ct), cancellationToken).ConfigureAwait(false);

                await _state.UpsertCamerasAsync(cameras, cancellationToken).ConfigureAwait(false);
                _lastCameraCount = cameras.Count;

                // Silent inventory drift is a common vendor failure: a device returns 3 of its
                // 128 channels and every other signal still says healthy.
                if (_target.ExpectedCameraCount is { } expected && cameras.Count < expected)
                {
                    LogInventoryDrift(_logger, _target.Id, cameras.Count, expected);
                }

                Interlocked.Exchange(ref _consecutiveFailures, 0);
            }
            catch (Exception ex) when (IsPollFailure(ex))
            {
                RecordFailure(ex);
            }
        }
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
    }

    // ---- Status: fast cadence ----------------------------------------------

    private async Task StatusLoopAsync(CancellationToken cancellationToken)
    {
        if (!_adapter!.Capabilities.Has(Capability.CameraStatus))
        {
            return;
        }

        using var timer = new PeriodicTimer(_target.StatusPollInterval);

        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                var statuses = await CallAsync(
                    ct => _adapter!.GetCameraStatusAsync(ct), cancellationToken).ConfigureAwait(false);

                await _state.UpsertCamerasAsync(statuses, cancellationToken).ConfigureAwait(false);
                _lastCameraCount = statuses.Count;
                Interlocked.Exchange(ref _consecutiveFailures, 0);
            }
            catch (Exception ex) when (IsPollFailure(ex))
            {
                RecordFailure(ex);
            }
        }
    }

    // ---- Events: continuous ------------------------------------------------

    private async Task EventLoopAsync(CapabilitySet capabilities, CancellationToken cancellationToken)
    {
        if (capabilities.EventDelivery == EventDelivery.None)
        {
            LogNoEventDelivery(_logger, _target.Id);
            return;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (capabilities.EventDelivery == EventDelivery.Subscribe)
                {
                    await SubscribeAsync(cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await PollEventsAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (IsPollFailure(ex))
            {
                RecordFailure(ex);

                // A dropped subscription is expected, not exceptional — devices reboot and
                // networks blip. Reconnect after a pause rather than tearing down the target.
                LogEventStreamReconnecting(_logger, _target.Id, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task SubscribeAsync(CancellationToken cancellationToken)
    {
        var batch = new List<NormalisedEvent>(EventBatchSize);
        var lastFlush = Stopwatch.StartNew();

        await foreach (var normalised in _adapter!.SubscribeEventsAsync(cancellationToken)
            .ConfigureAwait(false))
        {
            try
            {
                normalised.Validate();
            }
            catch (Exception ex) when (ex is ArgumentException or ArgumentOutOfRangeException)
            {
                // One malformed event must never stall a target's whole stream.
                LogEventRejected(_logger, _target.Id, ex.Message);
                continue;
            }

            batch.Add(normalised);

            // Flushed on size or on age: a quiet site would otherwise hold a partial batch
            // indefinitely and appear to be producing nothing.
            if (batch.Count >= EventBatchSize || lastFlush.Elapsed >= EventFlushInterval)
            {
                await FlushAsync(batch, cancellationToken).ConfigureAwait(false);
                lastFlush.Restart();
            }
        }

        await FlushAsync(batch, cancellationToken).ConfigureAwait(false);
    }

    private async Task PollEventsAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_target.EventPollInterval);

        do
        {
            var cursor = await _state.GetCursorAsync(_target.Id, cancellationToken)
                .ConfigureAwait(false);

            // Bounded gap-fill. Without the bound, a target that was down for a day returns and
            // floods the bus with a day of backlog at full rate.
            var since = cursor.Position;
            var earliest = DateTimeOffset.UtcNow - cursor.MaxLookback;
            if (since < earliest)
            {
                LogBackfillTruncated(_logger, _target.Id,
                    (earliest - since).TotalHours, cursor.MaxLookback.TotalHours);
                since = earliest;
            }

            var events = await CallAsync(
                ct => _adapter!.GetEventsAsync(since, null, 1000, ct), cancellationToken)
                .ConfigureAwait(false);

            if (events.Count == 0)
            {
                continue;
            }

            await FlushAsync([.. events], cancellationToken).ConfigureAwait(false);

            // Adapters return events in ascending time order, so the last is the newest and the
            // cursor can advance safely even on a truncated batch.
            var newest = events[^1];
            await _state.AdvanceCursorAsync(
                _target.Id, newest.Timestamp, newest.SourceEventId, cancellationToken)
                .ConfigureAwait(false);
        }
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
    }

    private async Task FlushAsync(List<NormalisedEvent> batch, CancellationToken cancellationToken)
    {
        if (batch.Count == 0)
        {
            return;
        }

        var written = await _events.AppendAsync(batch, cancellationToken).ConfigureAwait(false);
        Interlocked.Add(ref _eventsSinceHealthCheck, written);

        // A gap between received and written is duplicate suppression working, not data loss.
        if (written < batch.Count)
        {
            LogDuplicatesSuppressed(_logger, _target.Id, batch.Count - written);
        }

        batch.Clear();
    }

    // ---- Health ------------------------------------------------------------

    private async Task HealthLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(60));

        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            var started = Stopwatch.StartNew();
            HealthStatus status;
            double? latency = null;

            try
            {
                if (_adapter!.Capabilities.Has(Capability.TimeSyncCheck))
                {
                    var deviceTime = await CallAsync(
                        ct => _adapter!.GetVmsTimeAsync(ct), cancellationToken).ConfigureAwait(false);

                    latency = started.Elapsed.TotalMilliseconds;

                    // The device answered, so it is reachable again: clear the failure count the
                    // poll loops raised while it was down. Without this the breaker can close and
                    // calls succeed, yet ConsecutiveFailures stays elevated forever.
                    Interlocked.Exchange(ref _consecutiveFailures, 0);

                    // Clock skew is measured rather than assumed: it silently corrupts every
                    // time-window correlation and, on ONVIF, breaks authentication outright.
                    var skew = deviceTime - DateTimeOffset.UtcNow;
                    if (Math.Abs(skew.TotalMinutes) > 5)
                    {
                        LogClockSkew(_logger, _target.Id, skew.TotalMinutes);
                        status = HealthStatus.Degraded;
                    }
                    else
                    {
                        status = HealthStatus.Healthy;
                    }
                }
                else
                {
                    status = _consecutiveFailures == 0 ? HealthStatus.Healthy : HealthStatus.Degraded;
                }
            }
            catch (AuthException)
            {
                status = HealthStatus.AuthFailed;
            }
            catch (Exception ex) when (ex is BrokenCircuitException or TimeoutRejectedException)
            {
                // The breaker is open (or an attempt timed out) — the target is unreachable, but
                // this is not a fresh failure to count. The breaker half-opens on its own and
                // this loop keeps running, so a recovered device is picked up automatically.
                _lastError = ex.Message;
                status = HealthStatus.Unreachable;
            }
            catch (Exception ex) when (ex is AdapterException)
            {
                RecordFailure(ex);
                status = HealthStatus.Unreachable;
            }

            await RecordHealthAsync(status, latency, _lastCameraCount, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task RecordHealthAsync(
        HealthStatus status, double? latencyMs, int? cameraCount, CancellationToken cancellationToken)
    {
        var events = Interlocked.Exchange(ref _eventsSinceHealthCheck, 0);

        try
        {
            await _state.RecordHealthAsync(new ConnectorHealth
            {
                TargetId = _target.Id,
                Status = status,
                CheckedAt = DateTimeOffset.UtcNow,
                LatencyMs = latencyMs,
                CameraCount = cameraCount,
                ConsecutiveFailures = _consecutiveFailures,
                LastError = _lastError,
                EventsSinceLastCheck = events,
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Health reporting must never take down the target it reports on.
            LogHealthWriteFailed(_logger, _target.Id, ex.Message);
        }
    }

    // ---- Call plumbing -----------------------------------------------------

    /// <summary>
    /// Runs one adapter call under rate limiting and the resilience pipeline.
    /// </summary>
    /// <remarks>
    /// Every adapter call goes through here. That is what lets adapters contain no retry, rate
    /// limiting or circuit-breaking logic of their own, and keeps the policy uniform across
    /// vendors and observable in one place.
    /// </remarks>
    private async Task<T> CallAsync<T>(
        Func<CancellationToken, Task<T>> call, CancellationToken cancellationToken)
    {
        await _rateLimit.AcquireAsync(1, cancellationToken).ConfigureAwait(false);

        return await _resilience.ExecuteAsync(
            async ct => await call(ct).ConfigureAwait(false), cancellationToken)
            .ConfigureAwait(false);
    }

    private void RecordFailure(Exception ex)
    {
        Interlocked.Increment(ref _consecutiveFailures);
        _lastError = ex.Message;
    }

    /// <summary>
    /// A poll-loop failure the loop should absorb and keep running through, rather than let
    /// escape and tear the whole target down.
    /// </summary>
    /// <remarks>
    /// <see cref="BrokenCircuitException"/> and <see cref="TimeoutRejectedException"/> come from
    /// the resilience pipeline, not the adapter, so they are not <see cref="AdapterException"/>s.
    /// Letting them propagate killed the worker permanently: the breaker would open while a
    /// device was down, the exception would fault <c>Task.WhenAll</c>, and nothing restarts a
    /// <see cref="TargetWorker"/> short of the lease churning. The loops must survive an open
    /// breaker so the half-open probe can recover the target on its own.
    /// </remarks>
    private static bool IsPollFailure(Exception ex) =>
        ex is (AdapterException and not AuthException)
            or BrokenCircuitException
            or TimeoutRejectedException;

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _stopping.Dispose();

        if (_adapter is not null)
        {
            await _adapter.DisposeAsync().ConfigureAwait(false);
        }
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Target {TargetId} ({Vendor}) connected. Capabilities: {Capabilities}")]
    private static partial void LogConnected(
        ILogger logger, Guid targetId, VendorKind vendor, Capability capabilities);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Target {TargetId} rejected our credentials: {Reason}. Not retrying — this "
                + "needs operator action.")]
    private static partial void LogAuthFailed(ILogger logger, Guid targetId, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Target {TargetId} is misconfigured: {Reason}")]
    private static partial void LogMisconfigured(ILogger logger, Guid targetId, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Target {TargetId} worker failed")]
    private static partial void LogWorkerFailed(ILogger logger, Guid targetId, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Target {TargetId} reported {Actual} cameras but {Expected} were expected; "
                + "possible silent inventory drift")]
    private static partial void LogInventoryDrift(
        ILogger logger, Guid targetId, int actual, int expected);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Target {TargetId} supports no event delivery; polling inventory and status only")]
    private static partial void LogNoEventDelivery(ILogger logger, Guid targetId);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Target {TargetId} event stream dropped ({Reason}); reconnecting")]
    private static partial void LogEventStreamReconnecting(
        ILogger logger, Guid targetId, string reason);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Target {TargetId} produced an invalid event, dropped: {Reason}")]
    private static partial void LogEventRejected(ILogger logger, Guid targetId, string reason);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "Target {TargetId} suppressed {Count} duplicate event(s)")]
    private static partial void LogDuplicatesSuppressed(ILogger logger, Guid targetId, int count);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Target {TargetId} was behind by {BehindHours:F1}h; backfill truncated to the "
                + "{LookbackHours:F0}h lookback bound. Events older than that are not recoverable.")]
    private static partial void LogBackfillTruncated(
        ILogger logger, Guid targetId, double behindHours, double lookbackHours);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Target {TargetId} clock differs from platform time by {SkewMinutes:F1} minutes")]
    private static partial void LogClockSkew(ILogger logger, Guid targetId, double skewMinutes);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Target {TargetId} health could not be recorded: {Reason}")]
    private static partial void LogHealthWriteFailed(ILogger logger, Guid targetId, string reason);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Target {TargetId} did not stop within the shutdown budget; abandoning it. "
                + "Any device subscription will lapse on its own timeout.")]
    private static partial void LogStopTimedOut(ILogger logger, Guid targetId);
}
