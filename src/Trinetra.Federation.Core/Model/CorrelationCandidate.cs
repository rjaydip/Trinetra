namespace Trinetra.Federation.Core.Model;

/// <summary>
/// One normalised event, as the correlation engine considers it for grouping.
/// </summary>
/// <remarks>
/// A read-only projection of <c>federation_event</c> — see
/// <c>Trinetra.Federation.Core.Events.NormalisedEvent</c> for the full envelope. Kept separate and
/// narrow because the engine only ever needs the join key, the scope columns and the location.
/// </remarks>
public sealed record CorrelationCandidate
{
    public required string EventId { get; init; }

    public required Guid SourceVmsId { get; init; }

    public required string CameraId { get; init; }

    /// <summary>Denormalised scope columns, carried onto the group member so a scoped list query
    /// never has to join back to the (partitioned, high-volume) event table.</summary>
    public required Guid OrganizationUnitId { get; init; }

    public Guid? GeographicAreaId { get; init; }

    /// <summary>The join key: a plate, a person reference, or a track id.</summary>
    public required string ObjectReference { get; init; }

    /// <summary>
    /// Required whenever the reference came from inference. Never surfaced as certainty — see
    /// <see cref="CorrelationRule"/>.
    /// </summary>
    public double? Confidence { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    public double? Latitude { get; init; }

    public double? Longitude { get; init; }
}
