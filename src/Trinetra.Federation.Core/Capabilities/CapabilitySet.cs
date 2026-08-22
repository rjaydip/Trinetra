namespace Trinetra.Federation.Core.Capabilities;

/// <summary>How events reach the platform from a given target.</summary>
public enum EventDelivery
{
    None,
    Poll,
    Subscribe,
}

/// <summary>
/// The result of probing one connector target.
/// </summary>
/// <remarks>
/// Probed once on connect and persisted. Downstream services read the stored matrix and must
/// never call a VMS to discover what it supports — at 80k cameras that turns a single UI
/// render into thousands of vendor round-trips.
/// </remarks>
public sealed record CapabilitySet
{
    public Capability Supported { get; init; } = Capability.None;

    public DateTimeOffset ProbedAt { get; init; } = DateTimeOffset.UtcNow;

    public string AdapterVersion { get; init; } = "unknown";

    /// <summary>
    /// Per-capability detail — why one was unavailable, or which vendor API version answered.
    /// Surfaced verbatim to operators diagnosing a target, so it should read as an
    /// explanation rather than a status code.
    /// </summary>
    public IReadOnlyDictionary<string, string> Notes { get; init; }
        = new Dictionary<string, string>(StringComparer.Ordinal);

    public bool Has(Capability caps) => (Supported & caps) == caps;

    public Capability Missing(Capability caps) => caps & ~Supported;

    /// <summary>
    /// How to collect events from this target.
    /// </summary>
    /// <remarks>
    /// Subscription is strongly preferred. Inventory polling scales fine because it is one
    /// bulk call per target, but event polling does not: event queries are time-ranged and
    /// far more expensive on the vendor side.
    /// </remarks>
    public EventDelivery EventDelivery => Has(Capability.EventsSubscribe)
        ? EventDelivery.Subscribe
        : Has(Capability.EventsPull)
            ? EventDelivery.Poll
            : EventDelivery.None;
}
