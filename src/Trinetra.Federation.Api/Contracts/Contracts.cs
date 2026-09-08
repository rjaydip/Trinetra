using System.Text.Json.Serialization;

namespace Trinetra.Federation.Api.Contracts;

// Request and response shapes for the API. Kept separate from the domain records so a field
// added to a domain type is never automatically exposed over HTTP — which is how credential
// material and internal state leak into responses.

public sealed record LoginRequest(string Username, string Password);

/// <summary>
/// The token pair returned by <c>/auth/login</c>, <c>/auth/refresh</c> and <c>/auth/password</c>.
/// </summary>
/// <remarks>
/// The access token is a short-lived (~15 min) JWT sent as <c>Authorization: Bearer</c>. The
/// refresh token is an opaque 8h sliding secret — store it, and present it to
/// <c>POST /auth/refresh</c> before the access token expires to obtain a new pair without
/// re-entering credentials. Each refresh <b>rotates</b> the refresh token. A response with
/// <c>mustChangePassword: true</c> is still valid — route the user to <c>POST /auth/password</c>
/// first.
/// </remarks>
public sealed record AuthTokenResponse(
    string AccessToken,
    int AccessExpiresIn,
    string RefreshToken,
    int RefreshExpiresIn,
    bool MustChangePassword);

/// <summary>The refresh-token exchange body: the opaque value from a prior <see cref="AuthTokenResponse"/>.</summary>
public sealed record RefreshRequest(string RefreshToken);

public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

// OAuth2 "password" grant response. Development only — this is the shape Scalar's Authorize
// dialog expects back from the token URL. Field names are the RFC 6749 wire names.
public sealed record OAuthTokenResponse(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("token_type")] string TokenType,
    [property: JsonPropertyName("expires_in")] int ExpiresIn);

public sealed record OrganizationRequest(
    string Code, string Name, string OrganizationType,
    string? Description = null, string? Status = null);

public sealed record OrganizationUnitRequest(
    Guid OrganizationId, string Code, string Name, string UnitType,
    Guid? ParentUnitId = null, string? Description = null,
    Guid? GeographicAreaId = null, string? Status = null);

public sealed record GeographicAreaRequest(
    string Code, string Name, string AreaType,
    Guid? ParentAreaId = null, string? Description = null, string? Status = null);

/// <summary>Deactivation with an explicit resolution for active children.</summary>
/// <remarks>
/// <c>ChildStrategy</c> has no default on purpose. Silently cascading can deactivate hundreds of
/// cameras from one click; silently orphaning drops them out of every scope with nothing
/// reporting it. The caller states which they meant.
/// </remarks>
public sealed record DeactivateRequest(string? ChildStrategy = null, Guid? NewParentId = null);

/// <summary>
/// Moves an organization unit under a parent in a different organization. The whole subtree's
/// organization is rewritten. <c>confirmScopeImpact</c> must be <c>true</c> when access groups
/// have an organization scope pointing into the subtree.
/// </summary>
public sealed record MoveUnitRequest(Guid NewParentUnitId, bool ConfirmScopeImpact = false);

public sealed record ConnectorTargetRequest(
    string Code,
    Guid OrganizationUnitId,
    string DisplayName,
    string Vendor,
    string Endpoint,
    string CredentialReference,
    Guid? GeographicAreaId = null,
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

/// <summary>
/// A resolved device credential. <b>The only response on this API that carries secret material.</b>
/// </summary>
/// <remarks>
/// Returned only by <c>GET /vms/{id}/credential/resolve</c>, only to a machine caller holding
/// <c>credential.resolve</c>, and every call is written to <c>credential_access_log</c>. It exists
/// for the AI worker (<c>ai-worker/</c>), which reads camera RTSP streams directly and needs their
/// login. Only the fields that were actually stored are populated; vendor extras
/// (<c>Credential.Extra</c> — Milestone OAuth client, Genetec application id) are not carried, so
/// a target that needs them cannot be resolved through this route.
/// </remarks>
public sealed record ResolvedCredentialResponse(
    string CredentialReference, string? Username, string? Password, string? Token);

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
/// Body for <c>POST /access-groups/{id}/activate</c>. When a scope dimension has no scope row the
/// group is unrestricted on it (an estate-wide grant); activating one requires the caller be
/// unscoped for <c>group.manage</c> on that dimension <b>and</b> pass <c>confirmUnscoped: true</c>.
/// </summary>
public sealed record ActivateGroupRequest(bool ConfirmUnscoped = false);

/// <summary>
/// Edits an access group's code, name, description and role. <c>status</c> is not touched here —
/// use the activate / disable routes. Switching the role re-runs the escalation guard.
/// </summary>
public sealed record UpdateGroupRequest(
    string Code, string Name, Guid RoleId, string? Description = null);

/// <summary>
/// Creates or replaces a role — a named set of permission codes. On <c>PUT</c> the
/// <c>permissions</c> list fully replaces the role's current set. <c>code</c> is ignored on
/// <c>PUT</c> (a role's code never changes). <c>status</c> is <c>DRAFT</c>, <c>ACTIVE</c> or
/// <c>INACTIVE</c>; only an <c>ACTIVE</c> role grants anything to a group that uses it. A custom
/// role is created <c>DRAFT</c> when <c>status</c> is omitted. A role that has left <c>DRAFT</c>
/// cannot be set back to it.
/// </summary>
public sealed record RoleWriteRequest(
    string Code, string Name, IReadOnlyList<string> Permissions,
    string? Description = null, string? Status = null);

/// <summary>
/// One boundary a group applies within: an organization unit, a geographic area, or a specific
/// resource. Exactly one dimension is supplied.
/// </summary>
public sealed record AddScopeRequest(
    string ScopeType, Guid? OrganizationUnitId = null, Guid? GeographicAreaId = null,
    string? ResourceType = null, Guid? ResourceId = null, string? Description = null);

// ---- Detections, watchlist, AI-worker health --------------------------------

/// <summary>
/// A detection submitted by Model 2's AI worker — mirrors <c>ai-worker/pipeline.py</c>'s
/// <c>DetectionEvent.to_json()</c> exactly. <c>CameraId</c> is <c>"{targetId}:{nativeCameraId}"</c>.
/// </summary>
public sealed record DetectionEventRequest(
    string Id, string CameraId, string EventType, DateTimeOffset Timestamp, double Confidence,
    IReadOnlyDictionary<string, string>? Attributes = null,
    IReadOnlyDictionary<string, string>? Evidence = null);

/// <summary>A stored detection, projected for search results.</summary>
public sealed record DetectionResponse(
    string Id, string CameraId, Guid? RegisteredCameraId, string EventType,
    DateTimeOffset Timestamp, double? Confidence,
    string? VehicleType, string? PlateNumber, string? SnapshotReference);

public sealed record CreateWatchlistEntryRequest(
    Guid OrganizationUnitId, string PlateNumber, string? Reason = null, string Severity = "Medium");

public sealed record WatchlistEntryResponse(
    Guid Id, Guid OrganizationUnitId, string PlateNumberNormalized,
    string? Reason, string Severity, bool IsActive, DateTimeOffset CreatedAt);

public sealed record WatchlistAlertResponse(
    Guid Id, Guid WatchlistEntryId, string PlateNumberNormalized, string? Reason, string Severity,
    string DetectionEventId, DateTimeOffset DetectionOccurredAt,
    DateTimeOffset RaisedAt, DateTimeOffset? AcknowledgedAt);

/// <summary>Heartbeat body posted by <c>ai-worker/monitoring/heartbeat.py</c>.</summary>
public sealed record WorkerHeartbeatRequest(string WorkerId, string? Hostname, DateTimeOffset ReportedAt);

public sealed record AiWorkerHealthResponse(
    string WorkerId, string? Hostname, DateTimeOffset FirstSeenAt, DateTimeOffset LastHeartbeatAt);

public sealed record CreateApiKeyRequest(
    string DisplayName, Guid GroupId, DateTimeOffset? ExpiresAt = null);

/// <summary>The raw key value — returned exactly once, at creation. Never retrievable again.</summary>
public sealed record ApiKeyCreatedResponse(Guid Id, string KeyId, string RawKey);
