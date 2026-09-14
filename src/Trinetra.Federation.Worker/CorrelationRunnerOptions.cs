namespace Trinetra.Federation.Worker;

/// <summary>Configuration for <see cref="CorrelationRunner"/>.</summary>
public sealed class CorrelationRunnerOptions
{
    public const string SectionName = "Correlation";

    /// <summary>How often to run a correlation pass.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How far behind "now" the window's trailing edge sits.
    /// </summary>
    /// <remarks>
    /// Correlation needs both sides of a possible match already landed. Running right up to
    /// "now" would routinely split a genuine pair across two runs — the second camera's event
    /// arrives a few hundred milliseconds after the first pass already closed the window.
    /// </remarks>
    public TimeSpan SettleDelay { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Extra lookback added behind the last processed point, so a run always re-examines a slice
    /// of the previous window.
    /// </summary>
    /// <remarks>
    /// Deliberately overlapping. A late-arriving event that missed the previous run's window
    /// still gets a chance to correlate — and re-processing the overlap is safe because
    /// <c>correlation_group</c> is deduplicated on <c>(rule, natural key, window bucket)</c>
    /// (CLAUDE.md invariant 5), not on this runner never repeating a range.
    /// </remarks>
    public TimeSpan Overlap { get; set; } = TimeSpan.FromSeconds(60);
}
