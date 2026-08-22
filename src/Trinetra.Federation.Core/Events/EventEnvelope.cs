namespace Trinetra.Federation.Core.Events;

/// <summary>
/// Wire format. Versioned independently of the payload so schema evolution never requires a
/// coordinated stop across services.
/// </summary>
/// <remarks>
/// Consumers must tolerate unknown fields (additive minor bumps) and reject unknown
/// <i>major</i> versions. With hundreds of connector workers deployed across departmental
/// sites, a rolling upgrade always has mixed versions in flight; the envelope is what makes
/// that safe rather than merely likely to work.
/// </remarks>
public sealed record EventEnvelope
{
    /// <summary>Current envelope version. Bump the major on any breaking payload change.</summary>
    public const string CurrentSchemaVersion = "1.0";

    public string SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>Worker or service instance id, for provenance when tracing a bad event.</summary>
    public required string Producer { get; init; }

    public DateTimeOffset ProducedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Correlates an event across worker, bus, correlation engine and API.</summary>
    public string TraceId { get; init; } = Guid.NewGuid().ToString("N");

    public required NormalisedEvent Payload { get; init; }

    /// <summary>
    /// Whether this consumer can safely read the envelope. Major must match; a higher minor
    /// is readable because minor bumps are additive only.
    /// </summary>
    public bool IsCompatible()
    {
        var major = MajorOf(SchemaVersion);
        return major is not null && major == MajorOf(CurrentSchemaVersion);
    }

    private static string? MajorOf(string version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return null;
        }

        var dot = version.IndexOf('.', StringComparison.Ordinal);
        return dot < 0 ? version : version[..dot];
    }
}
