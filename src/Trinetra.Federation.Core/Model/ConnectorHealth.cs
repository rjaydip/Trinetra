namespace Trinetra.Federation.Core.Model;

/// <summary>
/// Per-target health, surfaced to operators and used to drive quarantine decisions.
/// </summary>
public sealed record ConnectorHealth
{
    public required Guid TargetId { get; init; }
    public required HealthStatus Status { get; init; }
    public required DateTimeOffset CheckedAt { get; init; }

    public double? LatencyMs { get; init; }
    public int? CameraCount { get; init; }
    public int ConsecutiveFailures { get; init; }
    public bool CircuitOpen { get; init; }
    public string? LastError { get; init; }
    public long EventsSinceLastCheck { get; init; }

    /// <summary>
    /// How far behind live the event cursor is.
    /// </summary>
    /// <remarks>
    /// The single most useful signal in the system: it catches a target that is silently falling
    /// behind rather than failing loudly. A connector that returns HTTP 200 while drifting hours
    /// behind looks perfectly healthy by every other measure here.
    /// </remarks>
    public TimeSpan? CursorLag { get; init; }
}
