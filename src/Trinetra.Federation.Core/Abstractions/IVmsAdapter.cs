using Trinetra.Federation.Core.Capabilities;
using Trinetra.Federation.Core.Events;
using Trinetra.Federation.Core.Model;

namespace Trinetra.Federation.Core.Abstractions;

/// <summary>
/// The VMS adapter contract — the load-bearing abstraction of Model 3.
/// </summary>
/// <remarks>
/// <para>
/// <b>Vendor-specific logic exists only behind this interface.</b> Core services, correlation,
/// search, alerting and the API must never branch on vendor. If adding a vendor requires a
/// change outside <c>Trinetra.Federation.Adapters</c>, the abstraction has leaked and the
/// change is wrong.
/// </para>
/// <para>Adding a vendor, per the Model 3 spec's connector framework:</para>
/// <list type="number">
/// <item>Implement <see cref="IVmsAdapter"/>.</item>
/// <item>Declare supported capabilities via <see cref="ProbeCapabilitiesAsync"/>.</item>
/// <item>Map vendor events onto <see cref="NormalisedEvent"/>.</item>
/// <item>Register the factory in <c>AdapterRegistry</c>.</item>
/// <item>Pass the shared conformance suite in <c>Trinetra.IntegrationTests</c>.</item>
/// </list>
/// <para><b>Scale rule enforced by these signatures.</b> Inventory and status methods are
/// VMS-scoped and return collections. There is deliberately no <c>GetCameraAsync(cameraId)</c>:
/// per-camera calls against vendor devices work out to roughly 2,670 requests/second at 80k
/// cameras on a 30-second cadence, and no NVR survives that. One call returns a target's whole
/// inventory. See architecture §1.</para>
/// <para><b>Adapters do not implement their own resilience.</b> The runtime wraps every call
/// with per-target rate limiting, retry and circuit breaking, so policy is uniform across
/// vendors and observable in one place. An adapter that retries internally hides failures from
/// the circuit breaker and defeats it.</para>
/// <para>One instance is bound to one <see cref="ConnectorTarget"/> and owned by exactly one
/// worker at a time, enforced by the lease. Instances are not thread-safe.</para>
/// </remarks>
public interface IVmsAdapter : IAsyncDisposable
{
    /// <summary>
    /// The protocol surface this adapter speaks.
    /// </summary>
    /// <remarks>
    /// An instance member rather than <c>static abstract</c>. A static abstract here would be
    /// tidier at the call site, but C# forbids using an interface that declares unimplemented
    /// static members as a generic type argument — which would make <c>Task&lt;IVmsAdapter&gt;</c>
    /// illegal and break the factory that every caller goes through.
    /// </remarks>
    VendorKind Vendor { get; }

    /// <summary>The target this instance is bound to.</summary>
    ConnectorTarget Target { get; }

    /// <summary>
    /// Capabilities discovered by <see cref="ProbeCapabilitiesAsync"/>.
    /// Throws if read before probing.
    /// </summary>
    CapabilitySet Capabilities { get; }

    // ---- Lifecycle ---------------------------------------------------------

    /// <summary>Open sessions and authenticate. Throws <c>AuthException</c> on bad credentials.</summary>
    Task ConnectAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Discover what this VMS actually supports, by asking the device.
    /// </summary>
    /// <remarks>
    /// Never infer capability from vendor name or firmware string: it varies by model, licence
    /// tier and configuration within a single vendor. Called once on connect, then cached.
    /// </remarks>
    Task<CapabilitySet> ProbeCapabilitiesAsync(CancellationToken cancellationToken);

    // ---- Inventory (VMS-scoped, always bulk) -------------------------------

    /// <summary>
    /// The complete camera inventory for this target, in one call.
    /// </summary>
    /// <remarks>
    /// Must return the full set — partial returns are how silent inventory drift happens. If
    /// the vendor paginates, the adapter walks every page here rather than returning page one.
    /// </remarks>
    Task<IReadOnlyList<FederatedCamera>> GetCamerasAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Current health and recording state for all cameras on this target.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="GetCamerasAsync"/> because status changes on a ~30 second
    /// cadence while inventory changes on a roughly daily one; merging them would force the
    /// expensive call onto the fast loop.
    /// </remarks>
    Task<IReadOnlyList<FederatedCamera>> GetCameraStatusAsync(CancellationToken cancellationToken);

    // ---- Optional capabilities ---------------------------------------------

    /// <summary>
    /// Stream <i>references</i> for one camera. Model 3 never proxies video.
    /// </summary>
    /// <remarks>
    /// Per-camera by necessity, but called on demand from the UI rather than on a poll loop,
    /// so it does not violate the bulk rule.
    /// </remarks>
    Task<IReadOnlyList<StreamProfile>> GetStreamsAsync(
        string nativeCameraId, CancellationToken cancellationToken);

    Task<IReadOnlyList<RecordingSegment>> GetRecordingsAsync(
        string nativeCameraId, DateTimeOffset rangeStart, DateTimeOffset rangeEnd,
        CancellationToken cancellationToken);

    /// <summary>
    /// Pull events in a time window, already normalised.
    /// </summary>
    /// <remarks>
    /// <paramref name="since"/> comes from the persisted cursor. Implementations must honour
    /// <paramref name="limit"/> and return events in ascending timestamp order, so the runtime
    /// can safely advance the cursor on a partial batch without skipping the remainder.
    /// </remarks>
    Task<IReadOnlyList<NormalisedEvent>> GetEventsAsync(
        DateTimeOffset since, DateTimeOffset? until, int limit,
        CancellationToken cancellationToken);

    /// <summary>
    /// Long-lived push subscription, already normalised. Preferred over polling.
    /// </summary>
    /// <remarks>
    /// Runs until cancelled. The runtime owns reconnection and gap-fill from the cursor, so
    /// implementations must <b>not</b> swallow disconnects — throw <c>TransientVmsException</c>
    /// and let the runtime decide, otherwise a silently dead subscription looks identical to a
    /// quiet site.
    /// </remarks>
    IAsyncEnumerable<NormalisedEvent> SubscribeEventsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// The VMS's own clock, for skew detection.
    /// </summary>
    /// <remarks>
    /// Clock skew across departments silently corrupts every time-window correlation, so it is
    /// measured rather than assumed. See architecture §8.
    /// </remarks>
    Task<DateTimeOffset> GetVmsTimeAsync(CancellationToken cancellationToken);
}
