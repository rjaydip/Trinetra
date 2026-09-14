namespace Trinetra.Federation.Core.Model;

/// <summary>
/// Configuration for one correlation rule: the time window, spatial radius, confidence floor and
/// signal-agreement threshold a set of events must clear to be grouped as a possible match.
/// </summary>
/// <remarks>
/// <para>
/// Architecture §8: "Correlation rules are configuration, not code" — time window, spatial
/// radius, confidence floor and required signal agreement are per-rule config, so operators tune
/// without a deployment. Rows live in <c>correlation_rule</c>, seeded by the schema; there is no
/// write API in this pass (system-seeded only).
/// </para>
/// <para>
/// A rule never produces certainty. <see cref="CorrelationGroupOutcome.Confidence"/> on
/// anything this rule matches is always a possible-match score, never proof of identity —
/// cross-camera identity is probabilistic (CLAUDE.md production posture).
/// </para>
/// </remarks>
public sealed record CorrelationRule
{
    public required Guid Id { get; init; }

    /// <summary>Stable machine name, e.g. <c>ANPR_CROSS_CAMERA</c>.</summary>
    public required string Code { get; init; }

    public required string Name { get; init; }

    public string? Description { get; init; }

    /// <summary>
    /// Events are bucketed into windows of this width; only events sharing an
    /// <see cref="CorrelationCandidate.ObjectReference"/> within the same bucket can join
    /// one group.
    /// </summary>
    public required TimeSpan TimeWindow { get; init; }

    /// <summary>
    /// Maximum distance, in meters, a candidate's location may sit from the group's centroid to
    /// stay a member. Null means no spatial constraint is applied (an event with no
    /// <see cref="CorrelationCandidate.Latitude"/> or <see cref="CorrelationCandidate.Longitude"/>
    /// is never excluded by this check).
    /// </summary>
    /// <remarks>
    /// Computed with plain-trig haversine, the same no-PostGIS approach
    /// <see cref="Trinetra.Federation.Core.Geo.CoverageSector"/> uses (CLAUDE.md: no PostgreSQL
    /// extensions).
    /// </remarks>
    public double? SpatialRadiusMeters { get; init; }

    /// <summary>
    /// Minimum average member confidence, in [0,1], for a candidate group to be recorded at all.
    /// </summary>
    public required double ConfidenceFloor { get; init; }

    /// <summary>
    /// Minimum number of distinct cameras that must agree (report the same
    /// <see cref="CorrelationCandidate.ObjectReference"/> in the same window) before a
    /// group is formed. A single camera's own repeated observations never form a group on their
    /// own — cross-camera identity is probabilistic and needs corroboration.
    /// </summary>
    public required int RequiredSignalAgreement { get; init; }

    public bool IsActive { get; init; } = true;
}
