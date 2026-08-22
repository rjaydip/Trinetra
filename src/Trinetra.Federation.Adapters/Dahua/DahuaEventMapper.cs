using System.Text.Json;
using Trinetra.Federation.Core.Events;

namespace Trinetra.Federation.Adapters.Dahua;

/// <summary>Maps Dahua event codes onto the common taxonomy.</summary>
internal static class DahuaEventMapper
{
    private static readonly Dictionary<string, EventType> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["VideoMotion"] = EventType.MotionDetected,
        ["VideoMotionInfo"] = EventType.MotionDetected,
        ["SmartMotionHuman"] = EventType.PersonDetection,
        ["SmartMotionVehicle"] = EventType.VehicleDetection,
        ["CrossLineDetection"] = EventType.LineCrossed,
        ["CrossRegionDetection"] = EventType.IntrusionDetected,
        ["LeftDetection"] = EventType.ObjectRemoved,
        ["TakenAwayDetection"] = EventType.ObjectRemoved,
        ["LoiteringDetection"] = EventType.LoiteringDetected,
        ["CrowdDetection"] = EventType.CrowdDetected,
        ["VideoBlind"] = EventType.TamperDetected,
        ["VideoAbnormalDetection"] = EventType.TamperDetected,
        ["SceneChange"] = EventType.SceneChange,
        ["VideoLoss"] = EventType.VideoLoss,
        ["TrafficJunction"] = EventType.AnprDetection,
        ["TrafficControl"] = EventType.AnprDetection,
        ["ANPR"] = EventType.AnprDetection,
        ["FaceDetection"] = EventType.FaceDetection,
        ["FaceRecognition"] = EventType.FaceDetection,
        ["StorageNotExist"] = EventType.StorageFailure,
        ["StorageFailure"] = EventType.StorageFailure,
        ["StorageLowSpace"] = EventType.StorageFailure,
        ["NoDisk"] = EventType.StorageFailure,
    };

    internal static EventType Map(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return EventType.VendorSpecific;
        }

        if (Known.TryGetValue(code.Trim(), out var mapped))
        {
            return mapped;
        }

        var value = code;

        if (Has("Traffic") || Has("Plate")) return EventType.AnprDetection;
        if (Has("Face")) return EventType.FaceDetection;
        if (Has("Human")) return EventType.PersonDetection;
        if (Has("Vehicle")) return EventType.VehicleDetection;
        if (Has("Blind") || Has("Tamper")) return EventType.TamperDetected;
        if (Has("VideoLoss")) return EventType.VideoLoss;
        if (Has("Motion")) return EventType.MotionDetected;
        if (Has("Storage") || Has("Disk")) return EventType.StorageFailure;

        return EventType.VendorSpecific;

        bool Has(string needle) => value.Contains(needle, StringComparison.OrdinalIgnoreCase);
    }

    internal static Severity MapSeverity(EventType eventType) => eventType switch
    {
        EventType.TamperDetected or EventType.VideoLoss or EventType.StorageFailure => Severity.High,
        EventType.IntrusionDetected or EventType.LineCrossed => Severity.Medium,
        EventType.AnprDetection or EventType.FaceDetection or EventType.PersonDetection
            or EventType.VehicleDetection => Severity.Low,
        _ => Severity.Info,
    };

    /// <summary>
    /// Pulls a plate number out of the JSON <c>data</c> blob on traffic events.
    /// </summary>
    /// <remarks>
    /// Returns null rather than throwing on malformed JSON: a plate we cannot read still leaves
    /// a usable event with a camera, a time and a type, which is far better than discarding it.
    /// </remarks>
    internal static string? ExtractPlate(IReadOnlyDictionary<string, string> fields)
    {
        if (!fields.TryGetValue("data", out var json) || string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return FindPlate(document.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? FindPlate(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (property.NameEquals("PlateNumber") || property.NameEquals("Plate"))
                    {
                        var value = property.Value.ValueKind == JsonValueKind.String
                            ? property.Value.GetString()
                            : null;

                        if (!string.IsNullOrWhiteSpace(value))
                        {
                            return value;
                        }
                    }

                    var nested = FindPlate(property.Value);
                    if (nested is not null)
                    {
                        return nested;
                    }
                }

                return null;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    var nested = FindPlate(item);
                    if (nested is not null)
                    {
                        return nested;
                    }
                }

                return null;

            default:
                return null;
        }
    }
}
