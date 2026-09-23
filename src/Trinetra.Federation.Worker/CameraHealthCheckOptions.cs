namespace Trinetra.Federation.Worker;

/// <summary>Configuration for <see cref="CameraHealthCheckRunner"/>.</summary>
public sealed class CameraHealthCheckOptions
{
    public const string SectionName = "CameraHealthCheck";

    /// <summary>How often a single camera is re-probed once checked. Not the runner's own tick
    /// interval — see <see cref="TickInterval"/> — this is the per-camera cadence the batching is
    /// tuned to approximate across the whole fleet.</summary>
    public TimeSpan CheckInterval { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>How often the runner wakes up to probe one more batch. Kept short relative to
    /// <see cref="CheckInterval"/> so a small, steady trickle of checks — not one big burst every
    /// 15 minutes — is what actually produces the target cadence across the fleet.</summary>
    public TimeSpan TickInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Cameras probed per tick. Tune this against fleet size: at the defaults
    /// (30s tick, batch 50) the runner can probe ~6,000 cameras within one 15-minute
    /// <see cref="CheckInterval"/> window — for the RFP's 80,000-camera target, raise this (and/or
    /// <see cref="MaxConcurrentProbes"/>) accordingly rather than shortening the tick, which would
    /// just create more, smaller database round-trips for the same total throughput.</summary>
    public int BatchSize { get; set; } = 50;

    /// <summary>Probes within a batch run concurrently, bounded by this — never one connection per
    /// camera in the batch at once, and never fully sequential either.</summary>
    public int MaxConcurrentProbes { get; set; } = 20;
}
