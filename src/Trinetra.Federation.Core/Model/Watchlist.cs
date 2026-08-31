namespace Trinetra.Federation.Core.Model;

/// <summary>A plate number an operator wants flagged when detected.</summary>
public sealed record WatchlistEntry
{
    public Guid Id { get; init; }
    public required Guid OrganizationUnitId { get; init; }

    /// <summary>Always normalized — see <see cref="PlateNormalizer"/>.</summary>
    public required string PlateNumberNormalized { get; init; }

    public string? Reason { get; init; }
    public string Severity { get; init; } = "Medium";
    public bool IsActive { get; init; } = true;
    public DateTimeOffset CreatedAt { get; init; }
    public Guid? CreatedBy { get; init; }
}

/// <summary>A detection that matched an active watchlist entry.</summary>
public sealed record WatchlistAlert
{
    public Guid Id { get; init; }
    public required Guid WatchlistEntryId { get; init; }
    public required string DetectionEventId { get; init; }
    public required DateTimeOffset DetectionOccurredAt { get; init; }
    public DateTimeOffset RaisedAt { get; init; }
    public DateTimeOffset? AcknowledgedAt { get; init; }
    public Guid? AcknowledgedBy { get; init; }
}
