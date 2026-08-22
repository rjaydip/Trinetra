namespace Trinetra.Federation.Core.Model;

/// <summary>
/// Adapter implementations, named for the <i>protocol surface</i> they speak rather than the
/// brand on the box — that is what determines the code path. CP Plus NVRs are Dahua OEM and
/// speak Dahua CGI, so they use the Dahua adapter rather than one of their own.
/// </summary>
public enum VendorKind
{
    Onvif,
    HikvisionIsapi,
    DahuaCgi,
    MilestoneGateway,
    GenetecWebSdk,
    Simulator,
}

/// <summary>
/// The process-isolation boundary for a connector. See architecture §3.
/// </summary>
public enum RuntimeClass
{
    /// <summary>
    /// Pure HTTP adapters and the .NET-native Milestone MIP / Genetec SDKs. Packed
    /// many-per-process; a fault is contained by ordinary exception handling.
    /// </summary>
    Managed,

    /// <summary>
    /// P/Invoke into a vendor C SDK (Hikvision HCNetSDK, Dahua NetSDK). Runs in a segregated
    /// process, one vendor per process: a native access violation kills the CLR outright, and
    /// no <c>try/catch</c> will contain it.
    /// </summary>
    Native,
}

public enum TargetState
{
    Active,
    Disabled,

    /// <summary>Repeatedly failing or misconfigured; excluded from lease claim until cleared
    /// by an operator, so a broken target cannot consume worker capacity indefinitely.</summary>
    Quarantined,
}

public enum HealthStatus
{
    Unknown,
    Healthy,
    Degraded,
    Unreachable,
    AuthFailed,
}
