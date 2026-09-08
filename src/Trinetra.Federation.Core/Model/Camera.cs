namespace Trinetra.Federation.Core.Model;

/// <summary>
/// A CCTV asset in the authoritative camera registry.
/// </summary>
/// <remarks>
/// <para>
/// Distinct from <see cref="FederatedCamera"/>, which is an <i>observation</i> of what a VMS
/// currently reports. This is the registered record: created deliberately (manually, by
/// import, or via API), and the thing observations and GIS coverage point at.
/// </para>
/// <para>
/// Identity follows <c>CAMERA-SCHEMA.md</c>: an immutable <see cref="Id"/> for the system and a
/// human-facing <see cref="Code"/> for people. Ownership (<see cref="OrganizationUnitId"/>) and
/// location (<see cref="GeographicAreaId"/>) are independent dimensions and both are always set — a
/// registered camera always has an owner and a place.
/// </para>
/// <para>
/// Status vocabularies (<see cref="CameraStatus"/>) are strings rather than enums, matching the
/// schema's <c>VARCHAR + CHECK</c> columns and the fact that the same three axes appear across
/// the registry, analytics and federation layers with slightly different producers.
/// </para>
/// </remarks>
public sealed record Camera
{
    /// <summary>Immutable internal identity. <see cref="Guid.Empty"/> on an unsaved record.</summary>
    public required Guid Id { get; init; }

    /// <summary>Human-facing identifier, e.g. <c>CAM-AHM-001245</c>. Unique among live rows, editable.</summary>
    public required string Code { get; init; }

    public required string Name { get; init; }

    /// <summary>Owner/operator. Answers "whose is it", never "where is it".</summary>
    public required Guid OrganizationUnitId { get; init; }

    /// <summary>Geographic area the camera sits in, at any level — the camera's geo-scope dimension.</summary>
    public required Guid GeographicAreaId { get; init; }

    public string? Manufacturer { get; init; }
    public string? Model { get; init; }

    /// <summary>One of <see cref="CameraVocab.Types"/>.</summary>
    public required string CameraType { get; init; }

    public string? SerialNumber { get; init; }

    // ---- Position and optics --------------------------------------------
    // Degrees for azimuth/tilt/FOV, metres for range/height/altitude.

    public required double Latitude { get; init; }
    public required double Longitude { get; init; }
    public double? Altitude { get; init; }
    public double? MountingHeight { get; init; }
    public double? Azimuth { get; init; }
    public double? Tilt { get; init; }
    public double? HorizontalFov { get; init; }
    public double? VerticalFov { get; init; }
    public double? EffectiveRange { get; init; }

    // ---- Network ------------------------------------------------------
    // Infrastructure properties: they change, and none of them is the camera's identity.

    public string? IpAddress { get; init; }
    public int? Port { get; init; }
    public string? Protocol { get; init; }

    /// <summary>The connector target this camera is served by, once reconciled.</summary>
    public Guid? VmsId { get; init; }

    public string? StreamReference { get; init; }

    /// <summary>Opaque pointer into the secret store. The registry never resolves it.</summary>
    public string? CredentialReference { get; init; }

    public DateOnly? InstallationDate { get; init; }

    public string OperationalStatus { get; init; } = CameraStatus.OperationalUnknown;
    public string ConnectivityStatus { get; init; } = CameraStatus.ConnectivityUnknown;
    public string MaintenanceStatus { get; init; } = CameraStatus.MaintenanceNormal;

    public DateTimeOffset? LastSeenAt { get; init; }
    public DateTimeOffset? LastHealthCheckAt { get; init; }

    /// <summary>Set when the camera has been retired (soft-deleted).</summary>
    public DateTimeOffset? DeletedAt { get; init; }
}

/// <summary>The camera status vocabularies, mirroring the <c>cameras</c> table CHECK constraints.</summary>
public static class CameraStatus
{
    public const string OperationalOnline = "ONLINE";
    public const string OperationalOffline = "OFFLINE";
    public const string OperationalDegraded = "DEGRADED";
    public const string OperationalUnknown = "UNKNOWN";

    public const string ConnectivityConnected = "CONNECTED";
    public const string ConnectivityDisconnected = "DISCONNECTED";
    public const string ConnectivityUnknown = "UNKNOWN";

    public const string MaintenanceNormal = "NORMAL";
    public const string MaintenanceRequired = "REQUIRED";
    public const string MaintenanceUnderMaintenance = "UNDER_MAINTENANCE";
    public const string MaintenanceRetired = "RETIRED";

    public static readonly IReadOnlySet<string> Operational =
        new HashSet<string>(StringComparer.Ordinal)
        { OperationalOnline, OperationalOffline, OperationalDegraded, OperationalUnknown };

    public static readonly IReadOnlySet<string> Connectivity =
        new HashSet<string>(StringComparer.Ordinal)
        { ConnectivityConnected, ConnectivityDisconnected, ConnectivityUnknown };

    public static readonly IReadOnlySet<string> Maintenance =
        new HashSet<string>(StringComparer.Ordinal)
        {
            MaintenanceNormal, MaintenanceRequired, MaintenanceUnderMaintenance, MaintenanceRetired,
        };
}

/// <summary>Camera type and network-protocol vocabularies for the <c>cameras</c> table.</summary>
public static class CameraVocab
{
    public static readonly IReadOnlySet<string> Types =
        new HashSet<string>(StringComparer.Ordinal)
        { "FIXED", "PTZ", "DOME", "BULLET", "ANPR", "THERMAL", "MULTISENSOR", "OTHER" };

    public static readonly IReadOnlySet<string> Protocols =
        new HashSet<string>(StringComparer.Ordinal)
        { "RTSP", "RTSPS", "ONVIF", "HTTP", "HTTPS", "RTMP", "SRT", "OTHER" };
}
