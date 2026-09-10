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

/// <summary>
/// A node in an organization's hierarchy. <see cref="GeographicAreaId"/> is a descriptive
/// "home area" only — never an authorization input (invariant 12).
/// </summary>
public sealed record OrganizationUnitResponse(
    Guid Id, Guid OrganizationId, Guid? ParentUnitId,
    string Code, string Name, string UnitType,
    string? Description, Guid? GeographicAreaId, string Status);

/// <summary>
/// A node in the geographic hierarchy. <see cref="AreaType"/> is operator-defined, not an enum.
/// </summary>
public sealed record GeographicAreaResponse(
    Guid Id, Guid? ParentAreaId, string Code, string Name, string AreaType,
    string? Description, string Status);

/// <summary>An operator-declared level in the geographic hierarchy.</summary>
public sealed record AreaTypeResponse(string Code, string Name, int LevelOrder);

/// <summary>An access group affected by a cross-organization unit move.</summary>
public sealed record AffectedGroupResponse(Guid Id, string Code, int MemberCount);

/// <summary>Result of a cross-organization unit move.</summary>
public sealed record MoveUnitResponse(
    Guid FromOrganizationId, Guid ToOrganizationId, int SubtreeSize,
    int CamerasFollowing, int TargetsFollowing,
    IReadOnlyList<AffectedGroupResponse> AffectedGroups);

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
/// <remarks>
/// <see cref="Permissions"/> is the permission codes this group's <b>role</b> composes — not a
/// live grant. The group actually confers them only when <see cref="GrantsEffective"/> is true
/// (the group is <c>ACTIVE</c> and its role is <c>ACTIVE</c>). Read <see cref="Scopes"/> too: a
/// dimension with no scope row is unrestricted on that dimension.
/// </remarks>
public sealed record AccessGroupResponse(
    Guid Id, string Code, string Name, string? Description, string Status,
    string RoleCode, IReadOnlyList<string> Permissions,
    IReadOnlyList<ScopeResponse> Scopes, int MemberCount,
    bool GrantsEffective,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>One permission a role composes, with its vocabulary metadata.</summary>
public sealed record RolePermissionDetailResponse(
    string Code, string Name, string Category, string? Description);

/// <summary>An access group that references a role.</summary>
public sealed record RoleUsedByResponse(Guid Id, string Code, string Name, string Status);

/// <summary>
/// A role with the permission codes it composes. <see cref="IsSystem"/> marks a preset shipped
/// with the platform — editable, but its <c>code</c> is fixed and it cannot be deleted.
/// <see cref="Customized"/> is true once an operator has edited a preset. Lifecycle is
/// <c>DRAFT -&gt; ACTIVE -&gt; INACTIVE</c>; a role that is not <c>ACTIVE</c> grants nothing to
/// any group on it.
/// </summary>
/// <remarks>
/// <see cref="Permissions"/> is the role's composed code list. <see cref="PermissionDetails"/>
/// (metadata) and <see cref="UsedBy"/> (the access groups on this role, filtered to the ones the
/// caller may see) are populated only on the single-role read; the list read leaves them null
/// and carries <see cref="UsageCount"/> — a true total — only.
/// </remarks>
public sealed record RoleResponse(
    Guid Id, string Code, string Name, string? Description, bool IsSystem, string Status,
    bool Customized,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? CustomizedAt,
    int UsageCount,
    IReadOnlyList<string> Permissions,
    IReadOnlyList<RolePermissionDetailResponse>? PermissionDetails,
    IReadOnlyList<RoleUsedByResponse>? UsedBy);

/// <summary>A permission in the vocabulary.</summary>
public sealed record PermissionResponse(
    string Code, string Name, string Category, string? Description);

/// <summary>
/// A provisioned API key, as listed. Carries no key material — the raw value is returned once,
/// only in the creation response.
/// </summary>
public sealed record ApiKeyResponse(
    Guid Id, string KeyId, string DisplayName, Guid GroupId, string GroupCode,
    DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt, DateTimeOffset? LastUsedAt,
    DateTimeOffset? RevokedAt);

/// <summary>A connector target — one VMS the platform federates.</summary>
public sealed record VmsResponse(
    Guid Id, string Code, Guid OrganizationUnitId, Guid? GeographicAreaId, string DisplayName,
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
/// <c>CameraId</c> is the camera registry identifier and stays null until reconciliation matches
/// this camera to a registered one. Until then, events carry the VMS's own channel id.
/// </remarks>
public sealed record FederatedCameraResponse(
    string NativeCameraId,
    Guid? CameraId,
    string? Name, string? VendorModel, string? Firmware,
    bool IsEnabled, bool? IsRecording, string Health, DateTimeOffset? LastSeen,
    IReadOnlyList<string> StreamReferences,
    DateTimeOffset? StatusChangedAt);

/// <summary>One recorded change in a camera's status.</summary>
/// <remarks>
/// The previous values are null on the camera's first observation, meaning "no earlier status"
/// rather than "the earlier status is unknown". Rows exist only where something actually changed:
/// a camera that has been healthy for a month has one row, not a month of samples.
/// </remarks>
public sealed record CameraStatusChangeResponse(
    DateTimeOffset ChangedAt,
    string? PreviousHealth, string Health,
    bool? PreviousEnabled, bool IsEnabled,
    bool? PreviousRecording, bool? IsRecording);

/// <summary>A camera's status timeline over a requested window.</summary>
/// <remarks>
/// <c>Changes</c> is newest first, and its last element may pre-date <c>From</c>: that is the
/// transition which established the status the window opened with, without which a chart cannot
/// draw its first segment.
/// </remarks>
public sealed record CameraStatusHistoryResponse(
    string NativeCameraId,
    DateTimeOffset From,
    DateTimeOffset To,
    IReadOnlyList<CameraStatusChangeResponse> Changes);

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
    IReadOnlyList<string> AffectedChildren, IReadOnlyList<string> Resolutions);

/// <summary>Whether a credential is provisioned. Reports presence only, never content.</summary>
public sealed record CredentialExistsResponse(string Reference, bool Exists);
