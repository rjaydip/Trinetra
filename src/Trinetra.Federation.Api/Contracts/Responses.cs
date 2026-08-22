namespace Trinetra.Federation.Api.Contracts;

// Response shapes.
//
// Every endpoint returns one of these concrete types rather than an anonymous object or a Dapper
// `dynamic`. Two reasons, and the second matters more:
//
//   * A frontend can generate a typed client only from a described schema.
//   * The compiler enforces that the handler returns what the documentation claims. Metadata
//     asserted with .Produces<T>() is just a claim, and drifts silently the first time a handler
//     changes shape — leaving documentation that is confidently wrong.

/// <summary>Identifier of a newly created resource.</summary>
public sealed record CreatedResponse(Guid Id);

/// <summary>An organization: a department, institution or corporation.</summary>
public sealed record OrganizationResponse(
    Guid Id, string Code, string Name, string OrganizationType, string? Description, string Status);

/// <summary>A node in an organization's hierarchy.</summary>
public sealed record OrganizationUnitResponse(
    Guid Id, Guid OrganizationId, Guid? ParentUnitId,
    string Code, string Name, string UnitType, string Status);

/// <summary>
/// A node in the geographic hierarchy. <see cref="AreaType"/> is operator-defined, not an enum.
/// </summary>
public sealed record GeographicAreaResponse(
    Guid Id, Guid? ParentAreaId, string Code, string Name, string AreaType, string Status);

/// <summary>An operator-declared level in the geographic hierarchy.</summary>
public sealed record AreaTypeResponse(string Code, string Name, int LevelOrder);

/// <summary>A physical installation location.</summary>
public sealed record SiteResponse(
    Guid Id, string Code, string Name, Guid GeographicAreaId,
    string? SiteType, string? Address, double? Latitude, double? Longitude, string Status);

/// <summary>A platform user. Never carries password material.</summary>
public sealed record UserResponse(
    Guid Id, string Username, string DisplayName, string? Email,
    bool MustChangePassword, string Status, DateTimeOffset? LastLoginAt, bool IsSystem);

/// <summary>A group a user belongs to, with any expiry on that membership.</summary>
public sealed record UserGroupResponse(
    Guid GroupId, string Code, string Name, DateTimeOffset? ExpiresAt);

/// <summary>A member of an access group.</summary>
public sealed record GroupMemberResponse(Guid UserId, string Username, DateTimeOffset? ExpiresAt);

/// <summary>One boundary a group's permissions apply within.</summary>
public sealed record ScopeResponse(
    Guid Id, string ScopeType, Guid? OrganizationUnitId, Guid? GeographicAreaId,
    string? ResourceType, Guid? ResourceId, string? Description);

/// <summary>An access group with its role, permissions and scopes resolved.</summary>
public sealed record AccessGroupResponse(
    Guid Id, string Code, string Name, string? Description, string Status,
    string RoleCode, IReadOnlyList<string> Permissions,
    IReadOnlyList<ScopeResponse> Scopes, int MemberCount);

/// <summary>A role and whether it ships with the platform.</summary>
public sealed record RoleResponse(
    Guid Id, string Code, string Name, string? Description, bool IsSystem);

/// <summary>A permission in the vocabulary.</summary>
public sealed record PermissionResponse(
    string Code, string Name, string Category, string? Description);

/// <summary>A connector target — one VMS the platform federates.</summary>
public sealed record VmsResponse(
    Guid Id, string Code, Guid OrganizationUnitId, Guid? SiteId, string DisplayName,
    string Vendor, string RuntimeClass, string Endpoint, string CredentialReference,
    bool VerifyTls, string State, int? ExpectedCameraCount);

/// <summary>One health observation for a connector target.</summary>
/// <remarks>
/// <c>CursorLagSeconds</c> is the most useful field here: it catches a target silently falling
/// behind rather than failing loudly, which no other signal reveals — a connector can return
/// HTTP 200 while drifting hours behind.
/// </remarks>
public sealed record ConnectorHealthResponse(
    DateTimeOffset CheckedAt, string Status, double? LatencyMs, int? CameraCount,
    int ConsecutiveFailures, bool CircuitOpen, string? LastError,
    long EventsSinceCheck,
    double? CursorLagSeconds);

/// <summary>What a device reported it can do, as stored at last probe.</summary>
public sealed record CapabilityResponse(
    int Supported, string AdapterVersion, DateTimeOffset ProbedAt,
    IReadOnlyDictionary<string, string> Notes);

/// <summary>A camera as reported by a VMS.</summary>
/// <remarks>
/// <c>CameraId</c> is Model 1's registry identifier and stays null until reconciliation matches
/// this camera to a registered one. Until then, events carry the VMS's own channel id.
/// </remarks>
public sealed record FederatedCameraResponse(
    string NativeCameraId,
    Guid? CameraId,
    string? Name, string? VendorModel, string? Firmware,
    bool IsEnabled, bool? IsRecording, string Health, DateTimeOffset? LastSeen,
    IReadOnlyList<string> StreamReferences);

/// <summary>Fleet summary for a dashboard, scoped to what the caller may reach.</summary>
public sealed record OverviewResponse(
    long Targets, long ActiveTargets, long QuarantinedTargets,
    long Cameras, long UnreachableCameras);

/// <summary>A queued or completed connection test.</summary>
public sealed record ConnectionTestSummary(
    Guid Id, string Status, string RequestedBy,
    DateTimeOffset RequestedAt, DateTimeOffset? CompletedAt, string? FailureReason);

/// <summary>Confirms a deactivation was refused, and what it would have affected.</summary>
public sealed record DeactivationConflictResponse(
    IReadOnlyList<string> AffectedAreas, IReadOnlyList<string> AffectedSites,
    IReadOnlyList<string> Resolutions);

/// <summary>Whether a credential is provisioned. Reports presence only, never content.</summary>
public sealed record CredentialExistsResponse(string Reference, bool Exists);
