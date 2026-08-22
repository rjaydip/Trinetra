using System.Globalization;
using System.Xml.Linq;
using Trinetra.Federation.Core.Events;

namespace Trinetra.Federation.Adapters.Onvif;

/// <summary>
/// Maps ONVIF notification topics onto the common <see cref="EventType"/> taxonomy.
/// </summary>
/// <remarks>
/// <para>
/// ONVIF topics are dotted paths such as <c>tns1:RuleEngine/CellMotionDetector/Motion</c>.
/// Vendors extend the tree freely, so matching is by suffix and keyword rather than exact
/// equality — an exact-match table would silently drop the majority of real device traffic.
/// </para>
/// <para>
/// Anything unrecognised becomes <see cref="EventType.VendorSpecific"/> with the original topic
/// preserved, never discarded. For an investigation platform, an event that was seen but not
/// understood is far more valuable than no event at all.
/// </para>
/// </remarks>
internal static class OnvifEventMapper
{
    /// <summary>
    /// Classifies a topic. Ordered most-specific first, since ONVIF topic paths nest and a
    /// broad keyword would otherwise capture a narrower one.
    /// </summary>
    internal static EventType MapTopic(string topic)
    {
        if (string.IsNullOrWhiteSpace(topic))
        {
            return EventType.VendorSpecific;
        }

        var t = topic.Replace('\\', '/');

        if (Contains(t, "Tamper")) return EventType.TamperDetected;
        if (Contains(t, "SignalLoss") || Contains(t, "VideoLoss")) return EventType.VideoLoss;
        if (Contains(t, "LineDetector") || Contains(t, "LineCross")) return EventType.LineCrossed;
        if (Contains(t, "LoiteringDetector") || Contains(t, "Loitering")) return EventType.LoiteringDetected;
        if (Contains(t, "FieldDetector") || Contains(t, "Intrusion")) return EventType.IntrusionDetected;
        if (Contains(t, "CountAggregation") || Contains(t, "Crowd")) return EventType.CrowdDetected;
        if (Contains(t, "ObjectRemoval") || Contains(t, "MissingObject")) return EventType.ObjectRemoved;
        if (Contains(t, "LicensePlate") || Contains(t, "ANPR") || Contains(t, "PlateReader"))
            return EventType.AnprDetection;
        if (Contains(t, "Face")) return EventType.FaceDetection;
        if (Contains(t, "MotionAlarm") || Contains(t, "MotionDetector") || Contains(t, "Motion"))
            return EventType.MotionDetected;
        if (Contains(t, "ImageTooDark") || Contains(t, "ImageTooBright")
            || Contains(t, "ImageTooBlurry") || Contains(t, "GlobalSceneChange"))
            return EventType.SceneChange;
        if (Contains(t, "RecordingJobState") || Contains(t, "Recording"))
            return EventType.RecordingStarted;
        if (Contains(t, "StorageFailure") || Contains(t, "HardDisk")) return EventType.StorageFailure;

        return EventType.VendorSpecific;

        static bool Contains(string haystack, string needle) =>
            haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Severity for a mapped event. Kept deliberately conservative: over-reporting severity
    /// across 80,000 cameras produces an alert stream nobody reads.
    /// </summary>
    internal static Severity MapSeverity(EventType eventType) => eventType switch
    {
        EventType.TamperDetected or EventType.VideoLoss or EventType.StorageFailure => Severity.High,
        EventType.IntrusionDetected or EventType.LineCrossed => Severity.Medium,
        EventType.AnprDetection or EventType.FaceDetection => Severity.Low,
        _ => Severity.Info,
    };

    /// <summary>
    /// Reads the <c>SimpleItem</c> key/value bag from a notification message.
    /// </summary>
    /// <remarks>
    /// The ONVIF message body is untyped by specification — <c>Source</c> and <c>Data</c> hold
    /// <c>xs:any</c> content. Any client, generated or hand-written, parses this by hand, which
    /// is why a typed SOAP binding buys little exactly where ONVIF matters most.
    /// </remarks>
    internal static Dictionary<string, string> ReadItems(XElement? section)
    {
        var items = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (section is null)
        {
            return items;
        }

        foreach (var item in section.Descendants().Where(e => e.Name.LocalName == "SimpleItem"))
        {
            var name = item.Attribute("Name")?.Value;
            var value = item.Attribute("Value")?.Value;
            if (!string.IsNullOrEmpty(name) && value is not null)
            {
                items[name] = value;
            }
        }

        return items;
    }

    /// <summary>
    /// Extracts the identifier a correlation join would use — a plate, a face or object id.
    /// </summary>
    /// <remarks>
    /// Deliberately does <b>not</b> fall back to a track id. An ONVIF track id is only
    /// meaningful within one camera, so promoting it to <c>ObjectReference</c> would let the
    /// correlation engine join unrelated subjects across cameras that happen to share a counter.
    /// </remarks>
    internal static string? ExtractObjectReference(IReadOnlyDictionary<string, string> data)
    {
        foreach (var key in (string[])["PlateNumber", "LicensePlate", "Plate", "FaceId", "ObjectId"])
        {
            if (data.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    /// <summary>Confidence, where the device reported one. Normalised to 0..1.</summary>
    internal static double? ExtractConfidence(IReadOnlyDictionary<string, string> data)
    {
        foreach (var key in (string[])["Likelihood", "Confidence", "Score"])
        {
            if (data.TryGetValue(key, out var raw)
                && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                // Devices report either 0..1 or 0..100; both appear in the field.
                return value > 1 ? Math.Min(value / 100.0, 1.0) : Math.Max(value, 0.0);
            }
        }

        return null;
    }

    /// <summary>
    /// Whether a state-change notification represents the state becoming active.
    /// </summary>
    /// <remarks>
    /// ONVIF property events fire on both edges — motion starting <i>and</i> motion stopping,
    /// both under the same topic. Publishing both as "motion detected" would double the event
    /// volume and make every duration calculation wrong.
    /// </remarks>
    internal static bool IsActiveState(IReadOnlyDictionary<string, string> data)
    {
        foreach (var key in (string[])["State", "IsMotion", "Active", "LogicalState"])
        {
            if (data.TryGetValue(key, out var raw))
            {
                if (bool.TryParse(raw, out var parsed))
                {
                    return parsed;
                }

                return !string.Equals(raw, "false", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(raw, "0", StringComparison.Ordinal)
                    && !string.Equals(raw, "Inactive", StringComparison.OrdinalIgnoreCase);
            }
        }

        // No state item: a one-shot event rather than a property change.
        return true;
    }
}
