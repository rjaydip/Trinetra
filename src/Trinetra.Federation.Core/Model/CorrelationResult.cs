namespace Trinetra.Federation.Core.Model;

/// <summary>
/// One cluster of events an <see cref="Trinetra.Federation.Core.Abstractions.ICorrelationEngine"/>
/// judged a possible match under one <see cref="CorrelationRule"/>.
/// </summary>
/// <remarks>
/// <b>Never certainty.</b> <see cref="Confidence"/> is a possible-match score in [0,1], not proof
/// of identity — cross-camera identity is probabilistic (CLAUDE.md production posture) and every
/// surface presenting this data must say "possible match", never "confirmed" or "same person".
/// </remarks>
public sealed record CorrelationGroupOutcome
{
    /// <summary>The shared <see cref="CorrelationCandidate.ObjectReference"/> the group formed on.</summary>
    public required string NaturalKey { get; init; }

    /// <summary>Start of the time bucket this group was computed for.</summary>
    public required DateTimeOffset WindowBucket { get; init; }

    /// <summary>Possible-match confidence in [0,1]. See remarks — never certainty.</summary>
    public required double Confidence { get; init; }

    public required DateTimeOffset FirstOccurredAt { get; init; }

    public required DateTimeOffset LastOccurredAt { get; init; }

    public required IReadOnlyList<CorrelationCandidate> Members { get; init; }
}

/// <summary>Outcome of applying one <see cref="CorrelationRule"/> over one time window.</summary>
public sealed record CorrelationResult
{
    public required CorrelationRule Rule { get; init; }

    public required IReadOnlyList<CorrelationGroupOutcome> Groups { get; init; }
}

/// <summary>Summary of one engine run, across every active rule, for the runner to log.</summary>
public sealed record CorrelationRunSummary
{
    public required int RulesEvaluated { get; init; }

    public required int CandidateGroups { get; init; }

    /// <summary>
    /// Groups actually inserted — always ≤ <see cref="CandidateGroups"/>. The gap is idempotent
    /// re-processing of an overlapping window, not an error.
    /// </summary>
    public required int GroupsCreated { get; init; }
}
