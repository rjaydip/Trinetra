namespace Trinetra.Federation.Core.Events;

/// <summary>
/// The normalised event taxonomy. Every vendor event maps onto exactly one of these.
/// </summary>
/// <remarks>
/// <see cref="VendorSpecific"/> is the escape hatch. When a vendor event has no good fit,
/// it is mapped here and the vendor's own label is preserved in
/// <see cref="NormalisedEvent.VendorEventType"/>. That keeps the event searchable and
/// correlatable rather than silently dropped — an adapter that discards what it cannot
/// classify loses evidence, which for an investigation platform is the worst outcome.
/// </remarks>
public enum EventType
{
    // Connectivity and health
    CameraOnline,
    CameraOffline,
    StreamLost,
    StreamRestored,
    RecordingStarted,
    RecordingStopped,
    StorageFailure,

    // Tamper and integrity
    TamperDetected,
    VideoLoss,
    SceneChange,

    // VMS-side analytics
    MotionDetected,
    LineCrossed,
    IntrusionDetected,
    LoiteringDetected,
    ObjectRemoved,
    CrowdDetected,

    // Observations shared with Model 2
    AnprDetection,
    VehicleDetection,
    PersonDetection,
    FaceDetection,

    // Platform-generated
    CorrelationMatch,
    ConnectorHealth,

    VendorSpecific,
}

/// <summary>Operational significance of an event.</summary>
public enum Severity
{
    Info,
    Low,
    Medium,
    High,
    Critical,
}

/// <summary>
/// Distinguishes live traffic from replay.
/// </summary>
/// <remarks>
/// Backfill after a reconnect or lease handover can briefly deliver at many times the live
/// rate. Consumers that alert, page, or rate-limit must be able to tell the difference, or
/// a routine failover pages the whole operations team. See architecture §6.
/// </remarks>
public enum DeliveryMode
{
    Live,
    Backfill,
    Replay,
}
