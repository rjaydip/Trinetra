using Trinetra.Federation.Core.Events;

namespace Trinetra.Federation.Core.Model;

/// <summary>
/// A camera as reported by a VMS.
/// </summary>
/// <remarks>
/// Deliberately thinner than Model 1's registry record: this is what the <i>VMS</i> knows. Model 1
/// owns location, azimuth, FOV, effective range and coverage; Model 3 reconciles against that
/// registry rather than competing with it.
/// </remarks>
public sealed record FederatedCamera
{
    /// <summary>The connector target that reported this camera.</summary>
    public required Guid TargetId { get; init; }

    /// <summary>The VMS's own identifier for this camera — a channel number, a GUID, a name.</summary>
    public required string NativeCameraId { get; init; }

    /// <summary>
    /// Model 1's registry id, resolved by reconciliation.
    /// </summary>
    /// <remarks>
    /// Null until the camera is matched to a registry entry. Model 1 does not exist yet, so this
    /// is currently always null — which is why events carry the VMS's own camera identifier and
    /// why geographic scoping of events is still incomplete.
    /// </remarks>
    public Guid? CameraId { get; init; }

    public required Guid OrganizationUnitId { get; init; }

    /// <summary>Physical site, inherited from the target unless the VMS reports otherwise.</summary>
    public Guid? SiteId { get; init; }

    public string? Name { get; init; }
    public string? VendorModel { get; init; }
    public string? Firmware { get; init; }

    /// <summary>Only populated if the VMS reports it. Model 1 remains authoritative.</summary>
    public GeoPoint? Location { get; init; }

    public bool IsEnabled { get; init; } = true;
    public bool? IsRecording { get; init; }
    public HealthStatus Health { get; init; } = HealthStatus.Unknown;
    public DateTimeOffset? LastSeen { get; init; }

    /// <summary>
    /// RTSP/HTTP stream URIs. <b>References only</b> — Model 3 never proxies or transcodes video;
    /// that is Model 2's role.
    /// </summary>
    public IReadOnlyList<string> StreamReferences { get; init; } = [];

    public string? RawReference { get; init; }
}

/// <summary>A named stream a camera exposes. A reference, never a video byte stream.</summary>
public sealed record StreamProfile
{
    public required string CameraId { get; init; }
    public required string ProfileName { get; init; }
    public required string Uri { get; init; }
    public string? Codec { get; init; }
    public string? Resolution { get; init; }
    public double? Framerate { get; init; }
    public bool IsPrimary { get; init; }
}

/// <summary>A recorded segment held by the VMS. <see cref="Reference"/> is a vendor handle for
/// retrieval, not the recording itself.</summary>
public sealed record RecordingSegment
{
    public required string CameraId { get; init; }
    public required DateTimeOffset StartTime { get; init; }
    public required DateTimeOffset EndTime { get; init; }
    public required string Reference { get; init; }
    public long? SizeBytes { get; init; }
}
