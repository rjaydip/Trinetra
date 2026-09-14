using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Trinetra.Federation.Core.Abstractions;

namespace Trinetra.Federation.Worker;

/// <summary>
/// Periodically runs <see cref="ICorrelationEngine"/> over the trailing slice of
/// <c>federation_event</c>. Architecture §8's day-one correlation runner: windowed SQL polling,
/// not a bus consumer — <c>Trinetra.Federation.Bus</c> is untouched.
/// </summary>
/// <remarks>
/// <para>
/// Mirrors <c>LeaseManager</c>'s shape (a single <see cref="PeriodicTimer"/> loop, a rolling
/// cursor, best-effort on transient failure) rather than <c>ConnectorSupervisor</c>'s per-target
/// fan-out — there is exactly one thing to run here, not one per lease.
/// </para>
/// <para>
/// The window advances by <see cref="CorrelationRunnerOptions.PollInterval"/> each tick, stays
/// <see cref="CorrelationRunnerOptions.SettleDelay"/> behind "now" so both sides of a possible
/// match have had time to land, and re-examines
/// <see cref="CorrelationRunnerOptions.Overlap"/> of the previous window on every tick. Re-running
/// an overlapping range is safe: <c>correlation_group</c> is deduplicated on
/// <c>(rule, natural key, window bucket)</c>, never on this runner tracking an exact boundary
/// (CLAUDE.md invariant 5).
/// </para>
/// </remarks>
public sealed partial class CorrelationRunner : BackgroundService
{
    private readonly ICorrelationEngine _engine;
    private readonly CorrelationRunnerOptions _options;
    private readonly ILogger<CorrelationRunner> _logger;

    public CorrelationRunner(
        ICorrelationEngine engine, IOptions<CorrelationRunnerOptions> options,
        ILogger<CorrelationRunner> logger)
    {
        _engine = engine;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogStarting(_logger, _options.PollInterval, _options.SettleDelay, _options.Overlap);

        using var timer = new PeriodicTimer(_options.PollInterval);

        // Seeded so the very first tick still has a bounded, non-empty starting window rather
        // than running from epoch.
        var cursor = DateTimeOffset.UtcNow - _options.SettleDelay - _options.PollInterval;

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                var windowEnd = DateTimeOffset.UtcNow - _options.SettleDelay;
                var windowStart = cursor - _options.Overlap;

                if (windowEnd <= windowStart)
                {
                    continue;
                }

                try
                {
                    var summary = await _engine.RunAsync(windowStart, windowEnd, stoppingToken)
                        .ConfigureAwait(false);
                    LogRunCompleted(
                        _logger, windowStart, windowEnd, summary.RulesEvaluated,
                        summary.CandidateGroups, summary.GroupsCreated);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Best effort, same posture as LeaseManager's renew/claim failures: keep the
                    // cursor where it was and retry the (now-wider) window next tick rather than
                    // crashing the whole worker process over one bad pass.
                    LogRunFailed(_logger, ex);
                    continue;
                }

                cursor = windowEnd;
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Correlation runner starting. PollInterval={PollInterval} SettleDelay={SettleDelay} Overlap={Overlap}")]
    private static partial void LogStarting(
        ILogger logger, TimeSpan pollInterval, TimeSpan settleDelay, TimeSpan overlap);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Correlation run [{WindowStart:O}, {WindowEnd:O}): {RulesEvaluated} rule(s), "
                + "{CandidateGroups} candidate group(s), {GroupsCreated} created")]
    private static partial void LogRunCompleted(
        ILogger logger, DateTimeOffset windowStart, DateTimeOffset windowEnd,
        int rulesEvaluated, int candidateGroups, int groupsCreated);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Correlation run failed; retrying next tick")]
    private static partial void LogRunFailed(ILogger logger, Exception exception);
}
