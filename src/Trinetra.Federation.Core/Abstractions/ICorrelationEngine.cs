using Trinetra.Federation.Core.Model;

namespace Trinetra.Federation.Core.Abstractions;

/// <summary>
/// Joins observations of the same object (a plate, a person reference, a track) across cameras
/// and vendors within a time window. Architecture §8.
/// </summary>
/// <remarks>
/// <para>
/// <b>Day-one implementation is windowed SQL over the hot PostgreSQL tier</b>
/// (<c>SqlCorrelationEngine</c> in <c>Trinetra.Federation.Storage</c>), correct up to roughly 5k
/// events/s. A future stream-processor implementation (Flink, or Kafka Streams via a JVM service)
/// sits behind this same port — nothing that calls <see cref="ICorrelationEngine"/> should need
/// to change when that happens.
/// </para>
/// <para>
/// Two invariants every implementation must hold:
/// </para>
/// <list type="bullet">
/// <item>Cross-camera identity is probabilistic — every group carries a confidence score and is
/// never surfaced as certainty.</item>
/// <item>Correlation rules are configuration, not code — time window, spatial radius, confidence
/// floor and required signal agreement come from <see cref="CorrelationRule"/> rows, not from
/// constants in an implementation.</item>
/// </list>
/// </remarks>
public interface ICorrelationEngine
{
    /// <summary>
    /// Runs every active <see cref="CorrelationRule"/> over <c>[windowStart, windowEnd)</c> and
    /// persists any new groups found.
    /// </summary>
    /// <remarks>
    /// Idempotent: a group is deduplicated on <c>(rule, natural key, window bucket)</c>, so
    /// calling this again for an overlapping or identical window creates nothing new (CLAUDE.md
    /// invariant 5 — every sink is idempotent).
    /// </remarks>
    Task<CorrelationRunSummary> RunAsync(
        DateTimeOffset windowStart, DateTimeOffset windowEnd, CancellationToken cancellationToken);
}
