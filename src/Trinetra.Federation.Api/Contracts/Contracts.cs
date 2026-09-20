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

/// <summary>
/// One row of a boundary-polygon bulk import. Exactly one of <see cref="Wkt"/> /
/// <see cref="GeoJson"/> is required — WKT for a survey tool's native export, GeoJSON for a
/// browser-drawn polygon; either must describe a single WGS84 <c>Polygon</c> (no SRID prefix
/// needed — 4326 is assumed and applied server-side).
/// </summary>
public sealed record BoundaryImportItem(
    Guid GeographicAreaId, string? Wkt = null, string? GeoJson = null);

/// <summary>A boundary-import batch. <see cref="Items"/> is 1..200 rows.</summary>
public sealed record BoundaryImportRequest(IReadOnlyList<BoundaryImportItem> Items);

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

/// <summary>
/// Creates a reusable named credential in the saved-credential library (v1.23). Write-only, same
/// as <see cref="CredentialRequest"/> — nothing here is ever echoed back.
/// </summary>
public sealed record SavedCredentialRequest(
    string Name, string? Description = null, string? Username = null, string? Password = null, string? Token = null);

/// <summary>
/// Rotates or renames a saved credential (v1.24). Supplying a password or a token reseals the
/// <b>same</b> <c>credentialReference</c> every camera pointed at this entry already carries —
/// that is the entire point of the shared-reference design (v1.23's remarks): rotating here
/// rotates it for every camera using it, with no per-camera write needed. Supplying neither
/// leaves the sealed secret untouched and only updates the name/description. Username, if
/// supplied, is stored alongside whichever of password/token was also supplied — it cannot be
/// changed on its own without also resupplying a password or token, the same rule
/// <see cref="CredentialRequest"/> already applies.
/// </summary>
public sealed record SavedCredentialUpdateRequest(
    string? Name = null, string? Description = null, string? Username = null, string? Password = null, string? Token = null);

/// <summary>One camera pointed at a saved credential's reference (v1.24 <c>GET /{id}</c> usedBy).</summary>
public sealed record SavedCredentialUsedByResponse(Guid Id, string CameraCode, string Name);

/// <summary>
/// A saved-credential library entry as the picker in the camera form lists it. Metadata only —
/// <c>CredentialReference</c> is the pointer a camera's own <c>credentialReference</c> field is
/// set to in order to share this entry's sealed secret; it is not secret material itself.
/// </summary>
/// <remarks>
/// <see cref="UsageCount"/> is a true total (every camera pointed at this reference, regardless
/// of the caller's own scope) so an operator rotating or deleting a widely-shared credential
/// always sees the real blast radius; <see cref="UsedBy"/> — populated only on <c>GET /{id}</c>,
/// null on the list route, matching <c>RoleResponse</c>'s own <c>usageCount</c>/<c>usedBy</c>
/// split — is filtered to cameras the caller can actually see, the same scope
/// <c>GET /cameras/{id}</c> itself enforces.
/// </remarks>
public sealed record SavedCredentialResponse(
    Guid Id, string Name, string? Description, string CredentialReference,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, int UsageCount,
    IReadOnlyList<SavedCredentialUsedByResponse>? UsedBy = null);

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

// ---- Correlation ------------------------------------------------------------

/// <summary>
/// One cross-camera possible match. <c>confidence</c> is a possible-match score in [0,1] and is
/// never certainty — cross-camera identity is probabilistic (CLAUDE.md production posture).
/// </summary>
public sealed record CorrelationGroupSummary(
    Guid Id,
    string RuleCode,
    string NaturalKey,
    DateTimeOffset WindowBucket,
    double Confidence,
    int MemberCount,
    DateTimeOffset FirstOccurredAt,
    DateTimeOffset LastOccurredAt,
    DateTimeOffset CreatedAt);

/// <summary>One member event of a correlation group, as returned in the group detail.</summary>
public sealed record CorrelationGroupMember(
    string FederationEventId,
    DateTimeOffset OccurredAt,
    string CameraId,
    Guid SourceVmsId,
    Guid OrganizationUnitId,
    Guid? GeographicAreaId);

/// <summary>
/// A correlation group with its member events. <c>confidence</c> on <c>group</c> is a
/// possible-match score, never certainty. <c>members</c> is filtered to what the caller is
/// scoped to reach; the group itself is visible once at least one member is reachable.
/// </summary>
public sealed record CorrelationGroupDetail(
    CorrelationGroupSummary Group, IReadOnlyList<CorrelationGroupMember> Members);

public sealed record ConnectionTestAccepted(Guid TestId, string Status, string StatusUrl);

public sealed record ConnectionTestResult(
    Guid TestId, Guid TargetId, string Status,
    DateTimeOffset RequestedAt, DateTimeOffset? CompletedAt,
    string? FailureReason, object? Result);

// ---- Users and groups ------------------------------------------------------

/// <summary>
/// <see cref="OrganizationUnitId"/>, <see cref="GeographicAreaId"/> and
/// <see cref="Designation"/> are HR/org-chart metadata — DESCRIPTIVE ONLY, never an
/// authorization input. Validated for existence + ACTIVE when supplied; access itself is
/// granted entirely through group membership, never through these fields (invariant 12).
/// </summary>
public sealed record CreateUserRequest(
    string Username, string DisplayName, string Password, string? Email = null,
    Guid? OrganizationUnitId = null, Guid? GeographicAreaId = null, string? Designation = null);

/// <summary>
/// A full replace, like every other field here: a field left out of the body clears it, the
/// same as an explicit <c>null</c> — there is no separate "leave unchanged" signal.
/// <see cref="OrganizationUnitId"/>, <see cref="GeographicAreaId"/> and
/// <see cref="Designation"/> are HR/org-chart metadata — DESCRIPTIVE ONLY, never an
/// authorization input (invariant 12).
/// </summary>
public sealed record UpdateUserRequest(
    string DisplayName, string? Email = null, string? Status = null,
    Guid? OrganizationUnitId = null, Guid? GeographicAreaId = null, string? Designation = null);

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
/// A detection submitted by the AI worker — mirrors <c>ai-worker/pipeline.py</c>'s
/// <c>DetectionEvent.to_json()</c> exactly. <c>CameraId</c> is <c>"{targetId}:{nativeCameraId}"</c>.
/// </summary>
public sealed record DetectionEventRequest(
    string Id, string CameraId, string EventType, DateTimeOffset Timestamp, double Confidence,
    IReadOnlyDictionary<string, string>? Attributes = null,
    IReadOnlyDictionary<string, string>? Evidence = null);

/// <summary>A stored detection, projected for search results.</summary>
/// <summary><c>Tags</c> are free-form operator tags (v1.26) — distinct from <c>EventType</c>'s
/// closed machine classification. Empty, never null, when the detection carries none.</summary>
public sealed record DetectionResponse(
    string Id, string CameraId, Guid? RegisteredCameraId, string? CameraName, string EventType,
    DateTimeOffset Timestamp, double? Confidence,
    string? VehicleType, string? PlateNumber, string? SnapshotReference,
    IReadOnlyList<string> Tags);

/// <summary>A batch of detections (v1.30) — the same per-item shape as <see cref="DetectionEventRequest"/>,
/// submitted together so the worker's routine (non-watchlist-matching) detections can flush as one
/// request instead of one HTTP round trip per detection. <c>Items</c> is 1..500.</summary>
public sealed record DetectionBulkRequest(IReadOnlyList<DetectionEventRequest> Items);

/// <summary>The outcome of one item in a bulk detection submission.</summary>
public sealed record DetectionBulkItemResult(int Index, string Id, string Status, string? Error);

/// <summary>The report for a whole bulk submission — always 200, even with per-item failures, so
/// the worker can inspect exactly which items to keep in its local retry spool (everything but
/// <c>"inserted"</c>/<c>"duplicate"</c> — a <c>"conflict"</c> reused an id and must not be retried
/// verbatim, an <c>"error"</c> may be retried).</summary>
public sealed record DetectionBulkResponse(
    int Inserted, int Duplicate, int Failed, IReadOnlyList<DetectionBulkItemResult> Results);

/// <summary>Attaches one free-form operator tag to a detection. <c>occurredAt</c> is required —
/// <c>detection_event</c>'s primary key is <c>(occurred_at, event_id)</c>, so both together
/// identify the detection (see v1.26's own doc comment on why there's no simpler key). Every
/// search result already carries its own <c>timestamp</c>, so a caller tagging a detection it
/// just searched for always has this on hand.</summary>
public sealed record DetectionTagRequest(DateTimeOffset OccurredAt, string Tag);

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
/// <remarks>
/// The worker is identified by the API key it authenticates with plus <c>WorkerId</c> and
/// <c>Hostname</c> — a heartbeat can only touch a row under the caller's own key (Finding
/// 17-M1). <c>ReportedAt</c> is the worker's own clock and is stored for drift diagnosis only;
/// the server stamps the authoritative "last seen" time itself (Finding 17-M2).
/// </remarks>
public sealed record WorkerHeartbeatRequest(string WorkerId, string Hostname, DateTimeOffset? ReportedAt);

/// <summary><c>LeasedCameraCount</c>/<c>LeasedCameraRefs</c> come from
/// <c>camera_worker_lease</c> (v1.27) — which cameras this worker is currently monitoring, not
/// just that it's alive. Empty for a worker that has never claimed any (an older client, or one
/// still using the static <c>ai-worker/scaling.py</c> partition instead of claiming).</summary>
public sealed record AiWorkerHealthResponse(
    Guid Id, Guid ApiKeyId, string ApiKeyName, string WorkerId, string Hostname,
    DateTimeOffset FirstSeenAt, DateTimeOffset LastHeartbeatAt,
    DateTimeOffset? ReportedAt, double? ClockDriftSeconds,
    int LeasedCameraCount, IReadOnlyList<string> LeasedCameraRefs,
    IReadOnlyList<string> LeasedCameraNames);

/// <summary>Claims/renews up to <c>Capacity</c> cameras for one AI-worker instance — VMS-discovered
/// candidates first, then standalone registry cameras, until <c>Capacity</c> is reached or
/// candidates run out. Call on every poll; a lease this worker already holds is renewed, not
/// re-claimed from scratch. A camera whose lease has gone stale (no renewal within the server's
/// TTL) becomes claimable by any worker's next call — this is the whole failover mechanism, no
/// coordinator involved.</summary>
public sealed record ClaimCamerasRequest(string WorkerId, string Hostname, int Capacity);

public sealed record ReleaseCamerasRequest(string WorkerId, string Hostname);

/// <summary>One camera this claim call assigned to the caller, with what it needs to actually
/// capture/decode the stream. <c>Source</c> is <c>REGISTRY</c> or <c>VMS</c> — see
/// <c>db/versions/v1.27.sql</c>.</summary>
public sealed record ClaimedCameraResponse(
    string CameraRef, string Source, string Name,
    string? StreamReference, string StreamPreference,
    string? NativeHlsUrl, string? NativeWebrtcUrl, string? CredentialReference);

public sealed record CreateApiKeyRequest(
    string DisplayName, Guid GroupId, DateTimeOffset? ExpiresAt = null);

/// <summary>The raw key value — returned exactly once, at creation. Never retrievable again.</summary>
public sealed record ApiKeyCreatedResponse(Guid Id, string KeyId, string RawKey);
