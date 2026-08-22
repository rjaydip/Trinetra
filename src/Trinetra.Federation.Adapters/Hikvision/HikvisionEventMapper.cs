using Trinetra.Federation.Core.Events;

namespace Trinetra.Federation.Adapters.Hikvision;

/// <summary>
/// Maps Hikvision <c>eventType</c> strings onto the common taxonomy.
/// </summary>
/// <remarks>
/// Hikvision's vocabulary is a flat set of camelCase tokens rather than ONVIF's topic tree, so
/// matching is exact-first with a keyword fallback. Anything unknown becomes
/// <see cref="EventType.VendorSpecific"/> with the original token retained — never dropped.
/// </remarks>
internal static class HikvisionEventMapper
{
    private static readonly Dictionary<string, EventType> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["VMD"] = EventType.MotionDetected,
        ["motionDetection"] = EventType.MotionDetected,
        ["linedetection"] = EventType.LineCrossed,
        ["fielddetection"] = EventType.IntrusionDetected,
        ["regionEntrance"] = EventType.IntrusionDetected,
        ["regionExiting"] = EventType.IntrusionDetected,
        ["loitering"] = EventType.LoiteringDetected,
        ["group"] = EventType.CrowdDetected,
        ["attendedBaggage"] = EventType.ObjectRemoved,
        ["unattendedBaggage"] = EventType.ObjectRemoved,
        ["shelteralarm"] = EventType.TamperDetected,
        ["tamperdetection"] = EventType.TamperDetected,
        ["scenechangedetection"] = EventType.SceneChange,
        ["videoloss"] = EventType.VideoLoss,
        ["videomismatch"] = EventType.VideoLoss,
        ["ANPR"] = EventType.AnprDetection,
        ["vehicledetection"] = EventType.VehicleDetection,
        ["facedetection"] = EventType.FaceDetection,
        ["facesnap"] = EventType.FaceDetection,
        ["fielddetectionHuman"] = EventType.PersonDetection,
        ["diskfull"] = EventType.StorageFailure,
        ["diskerror"] = EventType.StorageFailure,
        ["storageDetection"] = EventType.StorageFailure,
        ["recordingFailure"] = EventType.RecordingStopped,
        ["nicbroken"] = EventType.CameraOffline,
        ["ipconflict"] = EventType.CameraOffline,
        ["illaccess"] = EventType.VendorSpecific,
    };

    internal static EventType Map(string eventType)
    {
        if (string.IsNullOrWhiteSpace(eventType))
        {
            return EventType.VendorSpecific;
        }

        if (Known.TryGetValue(eventType.Trim(), out var mapped))
        {
            return mapped;
        }

        // Firmware variants append qualifiers to the base token, so fall back to substring
        // matching before giving up.
        var value = eventType;

        if (Has("plate") || Has("anpr")) return EventType.AnprDetection;
        if (Has("face")) return EventType.FaceDetection;
        if (Has("human") || Has("person")) return EventType.PersonDetection;
        if (Has("vehicle")) return EventType.VehicleDetection;
        if (Has("tamper") || Has("shelter")) return EventType.TamperDetected;
        if (Has("videoloss")) return EventType.VideoLoss;
        if (Has("line")) return EventType.LineCrossed;
        if (Has("motion") || Has("vmd")) return EventType.MotionDetected;
        if (Has("disk") || Has("storage") || Has("hdd")) return EventType.StorageFailure;

        return EventType.VendorSpecific;

        bool Has(string needle) => value.Contains(needle, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Conservative severity. Across 80,000 cameras, over-reporting produces an alert stream
    /// nobody reads — which is operationally the same as having no alerting.
    /// </summary>
    internal static Severity MapSeverity(EventType eventType) => eventType switch
    {
        EventType.TamperDetected or EventType.VideoLoss or EventType.StorageFailure
            or EventType.CameraOffline => Severity.High,
        EventType.IntrusionDetected or EventType.LineCrossed => Severity.Medium,
        EventType.AnprDetection or EventType.FaceDetection or EventType.PersonDetection
            or EventType.VehicleDetection => Severity.Low,
        _ => Severity.Info,
    };
}
