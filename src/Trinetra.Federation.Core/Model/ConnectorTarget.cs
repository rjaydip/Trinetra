namespace Trinetra.Federation.Core.Model;

/// <summary>
/// One VMS instance to federate.
/// </summary>
/// <remarks>
/// <para>
/// <b>This — not the camera — is the unit of scale, scheduling and failover.</b> An
/// 80,000-camera estate is a few hundred to a few thousand of these, which is a comfortable
/// number to schedule; 80,000 would not be. See <c>docs/ARCHITECTURE-MODEL-3.md</c> §1.
/// </para>
/// <para>
/// Identity follows <c>CAMERA-SCHEMA.md</c>: an immutable <see cref="Id"/> for the system and a
/// human-readable <see cref="Code"/> for people. Endpoints, names and vendors all change over a
/// device's life; the identity that events and audit records point at must not.
/// </para>
/// </remarks>
public sealed record ConnectorTarget
{
    public required Guid Id { get; init; }

    /// <summary>Human-facing identifier, e.g. <c>AHM-RINGROAD-NVR-01</c>. Unique, and editable.</summary>
    public required string Code { get; init; }

    /// <summary>
    /// Owning organization unit. Ownership and location are independent dimensions, per
    /// <c>DEPARTMENT-SCHEMA.md</c> — this answers "whose is it", never "where is it".
    /// </summary>
    public required Guid OrganizationUnitId { get; init; }

    /// <summary>
    /// Physical site, if known. Optional, and the only thing that makes a VMS geographically
    /// scopeable before Model 1's camera registry exists.
    /// </summary>
    public Guid? SiteId { get; init; }

    public required string DisplayName { get; init; }
    public required VendorKind Vendor { get; init; }
    public RuntimeClass RuntimeClass { get; init; } = RuntimeClass.Managed;

    /// <summary>Base URL or host:port of the VMS.</summary>
    public required string Endpoint { get; init; }

    /// <summary>
    /// Pointer into the secret store — never the secret itself.
    /// </summary>
    /// <remarks>
    /// Resolved at connect time, held in worker memory only, never logged and never persisted.
    /// Per <c>MODEL-1</c>: "Store only a secure reference to a secret-management system."
    /// </remarks>
    public required string CredentialReference { get; init; }

    /// <summary>
    /// Whether to validate the VMS's TLS certificate. Departmental NVRs frequently ship
    /// self-signed certificates, so disabling verification is a recorded per-target decision
    /// rather than a global convenience switch.
    /// </summary>
    public bool VerifyTls { get; init; } = true;

    public TargetState State { get; init; } = TargetState.Active;

    // ---- Per-target tuning -------------------------------------------------
    // Not global settings. Vendor tolerance varies by an order of magnitude: a small NVR and a
    // Milestone cluster cannot share a rate limit without either starving one or killing the other.

    public double RateLimitPerSecond { get; init; } = 5.0;
    public int RateLimitBurst { get; init; } = 10;

    /// <summary>Inventory changes on a roughly daily cadence, so it is polled slowly.</summary>
    public TimeSpan InventoryPollInterval { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Status changes on a ~30s cadence and warrants a much tighter loop.</summary>
    public TimeSpan StatusPollInterval { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan EventPollInterval { get; init; } = TimeSpan.FromSeconds(10);

    public int MaxConcurrentRequests { get; init; } = 4;

    /// <summary>
    /// The operator's expectation, used to flag inventory drift. A target that silently returns
    /// 3 of its 128 cameras is a common vendor failure mode that otherwise looks exactly like a
    /// successful sync.
    /// </summary>
    public int? ExpectedCameraCount { get; init; }
}
