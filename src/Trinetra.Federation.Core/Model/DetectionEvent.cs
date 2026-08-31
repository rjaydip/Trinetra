namespace Trinetra.Federation.Core.Model;

/// <summary>
/// A vehicle/plate/OCR detection submitted by Model 2's AI worker.
/// </summary>
/// <remarks>
/// Mirrors the exact shape <c>ai-worker/pipeline.py</c>'s <c>DetectionEvent.to_json()</c> already
/// produces — <c>CameraId</c> arrives as <c>"{targetId}:{nativeCameraId}"</c>, matching how
/// <c>ai-worker/camera_source.py</c> builds it from <c>GET /api/v1/vms/{id}/cameras</c>.
/// </remarks>
public sealed record DetectionEvent
{
    /// <summary>Worker-generated id, e.g. <c>"evt-&lt;uuid4hex&gt;"</c>. The idempotency key.</summary>
    public required string Id { get; init; }

    /// <summary><c>"{targetId}:{nativeCameraId}"</c>, as emitted by the worker.</summary>
    public required string CameraId { get; init; }

    /// <summary><c>"VEHICLE_DETECTED"</c> or <c>"ANPR_DETECTED"</c>.</summary>
    public required string EventType { get; init; }

    public required DateTimeOffset Timestamp { get; init; }
    public required double Confidence { get; init; }

    public string? VehicleType { get; init; }
    public string? PlateNumberRaw { get; init; }

    /// <summary>Bare local path today; whatever the evidence root writes to. Never image bytes.</summary>
    public string? SnapshotReference { get; init; }
}
