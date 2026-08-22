namespace Trinetra.Federation.Api.Contracts;

// Request and response shapes for the API. Kept separate from the domain records so a field
// added to a domain type is never automatically exposed over HTTP — which is how credential
// material and internal state leak into responses.

public sealed record LoginRequest(string Username, string Password);

public sealed record LoginResponse(string Token, DateTimeOffset ExpiresAt, bool MustChangePassword);

public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

public sealed record OrganizationRequest(
    string Code, string Name, string OrganizationType,
    string? Description = null, string? Status = null);

public sealed record OrganizationUnitRequest(
    Guid OrganizationId, string Code, string Name, string UnitType,
    Guid? ParentUnitId = null, string? Status = null);

public sealed record GeographicAreaRequest(
    string Code, string Name, string AreaType,
    Guid? ParentAreaId = null, string? Status = null);

public sealed record SiteRequest(
    string Code, string Name, Guid GeographicAreaId,
    string? SiteType = null, string? Address = null,
    double? Latitude = null, double? Longitude = null, string? Status = null);

/// <summary>Deactivation with an explicit resolution for active children.</summary>
/// <remarks>
/// <c>ChildStrategy</c> has no default on purpose. Silently cascading can deactivate hundreds of
/// cameras from one click; silently orphaning drops them out of every scope with nothing
/// reporting it. The caller states which they meant.
/// </remarks>
public sealed record DeactivateRequest(string? ChildStrategy = null, Guid? NewParentId = null);

public sealed record ConnectorTargetRequest(
    string Code,
    Guid OrganizationUnitId,
    string DisplayName,
    string Vendor,
    string Endpoint,
    string CredentialReference,
    Guid? SiteId = null,
    bool VerifyTls = true,
    string? RuntimeClass = null,
    double? RateLimitPerSecond = null,
    int? RateLimitBurst = null,
    int? InventoryPollSeconds = null,
    int? StatusPollSeconds = null,
    int? EventPollSeconds = null,
    int? MaxConcurrentRequests = null,
    int? ExpectedCameraCount = null);

public sealed record TargetStateRequest(string State);

/// <summary>
/// Setting a credential. Write-only: no endpoint returns these values, at any path.
/// </summary>
public sealed record CredentialRequest(
    string? Username = null, string? Password = null,
    string? Token = null, string? Description = null);

/// <summary>Confirms a credential was stored. Deliberately echoes no secret material.</summary>
public sealed record CredentialResponse(string CredentialReference, DateTimeOffset UpdatedAt);

/// <summary>One page of events, with the cursor for the next.</summary>
/// <remarks>
/// Keyset pagination, never OFFSET: a deep offset on a table taking hundreds of millions of rows
/// a day scans everything it skips.
/// </remarks>
public sealed record EventPage(IReadOnlyList<EventSummary> Events, string? NextCursor);

public sealed record EventSummary(
    string EventId,
    Guid SourceVmsId,
    string CameraId,
    string EventType,
    string? VendorEventType,
    DateTimeOffset OccurredAt,
    string Severity,
    string? ObjectReference,
    double? Confidence);

public sealed record ConnectionTestAccepted(Guid TestId, string Status, string StatusUrl);

public sealed record ConnectionTestResult(
    Guid TestId, Guid TargetId, string Status,
    DateTimeOffset RequestedAt, DateTimeOffset? CompletedAt,
    string? FailureReason, object? Result);

// ---- Users and groups ------------------------------------------------------

public sealed record CreateUserRequest(
    string Username, string DisplayName, string Password, string? Email = null);

public sealed record UpdateUserRequest(
    string DisplayName, string? Email = null, string? Status = null);

public sealed record ResetPasswordRequest(string NewPassword);

public sealed record AssignGroupRequest(Guid GroupId, DateTimeOffset? ExpiresAt = null);

public sealed record CreateGroupRequest(
    string Code, string Name, Guid RoleId,
    string? Description = null, string? Status = null);

/// <summary>
/// One boundary a group applies within: an organization unit, a geographic area, or a specific
/// resource. Exactly one dimension is supplied.
/// </summary>
public sealed record AddScopeRequest(
    string ScopeType, Guid? OrganizationUnitId = null, Guid? GeographicAreaId = null,
    string? ResourceType = null, Guid? ResourceId = null, string? Description = null);
