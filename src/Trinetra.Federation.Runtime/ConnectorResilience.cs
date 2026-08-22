using Microsoft.Extensions.Logging;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Polly.Timeout;
using Trinetra.Federation.Core.Errors;
using Trinetra.Federation.Core.Model;

namespace Trinetra.Federation.Runtime;

/// <summary>
/// Builds the resilience pipeline wrapped around every adapter call.
/// </summary>
/// <remarks>
/// <para>
/// This is the single place retry, circuit breaking and timeout policy lives. Adapters
/// implement none of it — an adapter that retried internally would hide failures from the
/// breaker and defeat it, which is why <see cref="Core.Abstractions.IVmsAdapter"/> forbids it.
/// </para>
/// <para>
/// Built on Polly rather than hand-rolled. The semantics below are specific enough that they
/// were originally designed by hand, but Polly implements the hard parts — half-open probe
/// coordination, sampling windows, thread-safe state transitions — correctly and under test,
/// and it is already a dependency.
/// </para>
/// <para>
/// <b>Why one pipeline per target rather than one shared.</b> A shared breaker would open on
/// aggregate failures, so one dead site could stop a worker from polling 199 healthy ones. State
/// is per target because failure is per target.
/// </para>
/// </remarks>
public static class ConnectorResilience
{
    /// <summary>
    /// Creates the pipeline for one connector target.
    /// </summary>
    /// <remarks>
    /// Order matters, outermost first:
    /// <list type="number">
    /// <item><b>Circuit breaker</b> — rejects immediately while open, so a dead site costs
    /// nothing rather than burning the worker's time budget on timeouts.</item>
    /// <item><b>Retry</b> — a small number of attempts for genuinely transient faults.</item>
    /// <item><b>Timeout</b> — per attempt, since <c>HttpClient.Timeout</c> is disabled platform
    /// wide so that the runtime owns every deadline in one observable place.</item>
    /// </list>
    /// </remarks>
    public static ResiliencePipeline Create(ConnectorTarget target, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(target);

        return new ResiliencePipelineBuilder()
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                // Only genuinely transient faults count. AuthException, CapabilityException and
                // NormalisationException are permanent facts about the target that no amount of
                // waiting changes; counting them would open the breaker on a device that is
                // working perfectly and simply lacks an optional service.
                ShouldHandle = new PredicateBuilder().Handle<TransientVmsException>(),

                FailureRatio = 0.5,
                MinimumThroughput = 4,
                SamplingDuration = TimeSpan.FromSeconds(60),

                // Grows with consecutive open cycles. Without growth, a site that is down for
                // hours gets probed every 30s by every worker that ever holds it — a small
                // denial-of-service against a device that is already struggling.
                BreakDurationGenerator = static args => ValueTask.FromResult(
                    TimeSpan.FromSeconds(Math.Min(30 * Math.Pow(2, Math.Min(args.FailureCount, 8)), 600))),

                OnOpened = args =>
                {
                    LogCircuitOpened(logger, target.Id, args.BreakDuration.TotalSeconds);
                    return ValueTask.CompletedTask;
                },
                OnClosed = args =>
                {
                    LogCircuitClosed(logger, target.Id);
                    return ValueTask.CompletedTask;
                },
            })
            .AddRetry(new RetryStrategyOptions
            {
                ShouldHandle = new PredicateBuilder().Handle<TransientVmsException>(),

                // Deliberately few. The poll loop comes round again shortly, so exhaustive
                // retrying here only delays discovering that a site is genuinely down.
                MaxRetryAttempts = 2,
                BackoffType = DelayBackoffType.Exponential,
                Delay = TimeSpan.FromSeconds(1),

                // Spreads reconnection across the fleet. Without it, a shared upstream blip
                // makes hundreds of workers retry in lockstep and hit the network together.
                UseJitter = true,

                DelayGenerator = static args =>
                {
                    // Honour the vendor's own hint when it gave one — it knows its limits
                    // better than our backoff curve does.
                    if (args.Outcome.Exception is RateLimitedException { RetryAfter: { } retryAfter })
                    {
                        return ValueTask.FromResult<TimeSpan?>(retryAfter);
                    }

                    return ValueTask.FromResult<TimeSpan?>(null);
                },
            })
            .AddTimeout(new TimeoutStrategyOptions
            {
                Timeout = TimeSpan.FromSeconds(30),
                OnTimeout = args =>
                {
                    LogAttemptTimedOut(logger, target.Id, args.Timeout.TotalSeconds);
                    return ValueTask.CompletedTask;
                },
            })
            .Build();
    }

    private static readonly Action<ILogger, Guid, double, Exception?> LogCircuitOpenedAction =
        LoggerMessage.Define<Guid, double>(LogLevel.Warning, new EventId(1, "CircuitOpened"),
            "Circuit opened for target {TargetId}; suspending calls for {BreakSeconds:F0}s");

    private static readonly Action<ILogger, Guid, Exception?> LogCircuitClosedAction =
        LoggerMessage.Define<Guid>(LogLevel.Information, new EventId(2, "CircuitClosed"),
            "Circuit closed for target {TargetId}; calls resumed");

    private static readonly Action<ILogger, Guid, double, Exception?> LogAttemptTimedOutAction =
        LoggerMessage.Define<Guid, double>(LogLevel.Warning, new EventId(3, "AttemptTimedOut"),
            "Call to target {TargetId} exceeded {TimeoutSeconds:F0}s and was cancelled");

    private static void LogCircuitOpened(ILogger logger, Guid targetId, double breakSeconds) =>
        LogCircuitOpenedAction(logger, targetId, breakSeconds, null);

    private static void LogCircuitClosed(ILogger logger, Guid targetId) =>
        LogCircuitClosedAction(logger, targetId, null);

    private static void LogAttemptTimedOut(ILogger logger, Guid targetId, double timeoutSeconds) =>
        LogAttemptTimedOutAction(logger, targetId, timeoutSeconds, null);
}
