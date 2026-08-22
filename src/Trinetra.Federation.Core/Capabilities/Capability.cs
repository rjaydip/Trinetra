namespace Trinetra.Federation.Core.Capabilities;

/// <summary>
/// What an adapter can actually do against its VMS.
/// </summary>
/// <remarks>
/// Mirrors the conceptual contract in the Model 3 spec, split finer where real vendors
/// diverge: plenty of systems expose a camera inventory but no event subscription, or
/// recordings but no export. Capability varies by model, licence tier and configuration
/// <i>within</i> a single vendor, which is why it is probed rather than inferred.
/// </remarks>
[Flags]
public enum Capability
{
    None = 0,
    Inventory = 1 << 0,
    CameraStatus = 1 << 1,
    Streams = 1 << 2,
    Recordings = 1 << 3,
    RecordingExport = 1 << 4,
    EventsPull = 1 << 5,
    EventsSubscribe = 1 << 6,
    Metadata = 1 << 7,
    Ptz = 1 << 8,
    Snapshot = 1 << 9,

    /// <summary>Can report the VMS clock, enabling skew detection. See architecture §8.</summary>
    TimeSyncCheck = 1 << 10,
}
