namespace Trinetra.Federation.Core.Events;

/// <summary>
/// A vendor event mapped onto the common schema of
/// <c>MODEL-3-VMS-FEDERATION-MIDDLEWARE.md</c> and <c>TECHNICAL-DESIGN.md</c> §6.
/// </summary>
/// <remarks>
/// <para>
/// Produced by adapters <b>at the edge</b> — normalisation happens inside the connector
/// worker, never centrally, so vendor-specific knowledge stays behind the adapter boundary.
/// </para>
/// <para>
/// Vendor-native payloads are never carried inline. <see cref="RawReference"/> points at the
/// retained original in object storage, so a normalisation bug is fixed by replaying stored
/// payloads rather than by re-pulling from a VMS that has probably already aged the event out.
/// </para>
/// <para>
/// Timestamps are <see cref="DateTimeOffset"/> throughout. Cameras and VMS hosts across
/// departments run in different timezones and drift; a timezone-naive timestamp silently
/// corrupts every time-window correlation. Using an offset-bearing type makes that class of
/// bug unrepresentable rather than merely validated against.
/// </para>
/// </remarks>
public sealed record NormalisedEvent
{
    /// <summary>Platform-assigned identity, unique per event instance.</summary>
    public string EventId { get; init; } = $"EVT-{Guid.NewGuid():N}";

    /// <summary>Connector target that produced this event.</summary>
    public required Guid SourceVmsId { get; init; }

    /// <summary>
    /// The vendor's own event id. Together with <see cref="SourceVmsId"/> this is the
    /// deduplication key: VMS reconnects, subscription replays and lease handovers all
    /// resend events, so every sink upserts on <see cref="DedupKey"/>.
    /// </summary>
    public string? SourceEventId { get; init; }

    public required string CameraId { get; init; }

    /// <summary>
    /// Owning organization unit, denormalised onto every event.
    /// </summary>
    /// <remarks>
    /// Deliberately duplicated rather than resolved by joining to the camera. Authorization
    /// filters every event query by organization, and a join to establish ownership on a table
    /// taking 100-400M rows/day would make the common case the expensive one.
    /// </remarks>
    public required Guid OrganizationUnitId { get; init; }

    /// <summary>
    /// Geographic area, when known — the event's geo-scope key.
    /// </summary>
    /// <remarks>
    /// Populated from the connector target today. Once Model 1's camera registry exists and
    /// reconciliation runs, it will come from the camera — which is what completes geographic
    /// scoping for events.
    /// </remarks>
    public Guid? GeographicAreaId { get; init; }

    public required EventType EventType { get; init; }

    /// <summary>
    /// The vendor's original label. Required when <see cref="EventType"/> is
    /// <see cref="EventType.VendorSpecific"/>; retained otherwise for auditability.
    /// </summary>
    public string? VendorEventType { get; init; }

    /// <summary>When the event occurred, according to the source VMS.</summary>
    public required DateTimeOffset Timestamp { get; init; }

    public Severity Severity { get; init; } = Severity.Info;

    /// <summary>
    /// Plate number, person reference, or track id — the join key for correlation.
    /// A track id is only valid within a single camera; see <see cref="Confidence"/>.
    /// </summary>
    public string? ObjectReference { get; init; }

    /// <summary>
    /// Required whenever <see cref="ObjectReference"/> came from inference. Cross-camera
    /// identity is probabilistic (<c>MODEL-2</c>) and must never be surfaced as certainty.
    /// </summary>
    public double? Confidence { get; init; }

    /// <summary>
    /// Denormalised from the Model 1 registry at publish time so consumers need no registry
    /// lookup on the hot path. Model 1 remains authoritative for camera geography.
    /// </summary>
    public GeoPoint? Location { get; init; }

    /// <summary>Object-store key of the retained vendor-native payload.</summary>
    public string? RawReference { get; init; }

    public DeliveryMode DeliveryMode { get; init; } = DeliveryMode.Live;

    public DateTimeOffset ReceivedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Kafka partition key. Keyed by camera so one camera's events stay ordered — the only
    /// ordering guarantee correlation actually needs. Cross-camera ordering is resolved by
    /// <see cref="Timestamp"/>, not by partition.
    /// </summary>
    public string PartitionKey => CameraId;

    /// <summary>Idempotency key for every sink. See <see cref="SourceEventId"/>.</summary>
    public string DedupKey => $"{SourceVmsId}:{SourceEventId ?? EventId}";

    /// <summary>
    /// Validates the invariants that cannot be expressed in the type system.
    /// Called by the runtime before publish; a failure dead-letters the single event
    /// rather than stalling the target's whole stream.
    /// </summary>
    public void Validate()
    {
        if (EventType == EventType.VendorSpecific && string.IsNullOrWhiteSpace(VendorEventType))
        {
            throw new ArgumentException(
                $"Event {EventId}: VendorEventType is required when EventType is VendorSpecific.");
        }

        if (Confidence is { } c && (c < 0 || c > 1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(Confidence), c, $"Event {EventId}: confidence must be within [0,1].");
        }
    }
}
