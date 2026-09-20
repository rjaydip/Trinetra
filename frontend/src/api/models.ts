export interface ApiProblemShape {
  title?: string;
  detail?: string;
  status?: number;
  errors?: Record<string, string[]>;
}

export interface AuthResponse {
  token: string;
  accessToken?: string;
  expiresAt: string;
  accessExpiresIn?: number;
  refreshToken?: string;
  refreshExpiresIn?: number;
  mustChangePassword: boolean;
}

export interface LoginRequest {
  username: string;
  password: string;
}

export interface ChangePasswordRequest {
  currentPassword: string;
  newPassword: string;
}

export interface CameraWriteRequest {
  cameraCode: string;
  name: string;
  organizationUnitId: string;
  geographicAreaId: string;
  cameraType: string;
  latitude: number;
  longitude: number;
  manufacturer?: string | null;
  model?: string | null;
  serialNumber?: string | null;
  altitude?: number | null;
  mountingHeight?: number | null;
  azimuth?: number | null;
  tilt?: number | null;
  horizontalFov?: number | null;
  verticalFov?: number | null;
  effectiveRange?: number | null;
  ipAddress?: string | null;
  port?: number | null;
  protocol?: string | null;
  vmsId?: string | null;
  streamReference?: string | null;
  credentialReference?: string | null;
  installationDate?: string | null;
  operationalStatus?: string | null;
  connectivityStatus?: string | null;
  maintenanceStatus?: string | null;
  recordEvents?: boolean;
  /** `RTSP` (default, pulled through the MediaMTX gateway), `HLS` (the camera's own
   * `nativeHlsUrl`, proxied directly), or `WEBRTC` (the camera's own `nativeWebrtcUrl`, relayed
   * via WHEP signaling only — media flows browser<->device directly). */
  streamPreference?: string;
  /** Required when `streamPreference` is `HLS` — the camera's own HLS master playlist URL. */
  nativeHlsUrl?: string | null;
  /** Required when `streamPreference` is `WEBRTC` — the camera's own WHEP endpoint URL. */
  nativeWebrtcUrl?: string | null;
}

export interface CameraConnectionTestRequest {
  protocol: string;
  ipAddress: string;
  port: number;
}

export interface CameraConnectionTestAccepted {
  testId: string;
  status: string;
  statusUrl: string;
}

export interface CameraConnectionTestResult {
  id: string;
  protocol: string;
  ipAddress: string;
  port: number;
  status: string;
  requestedAt: string;
  completedAt: string | null;
  failureReason: string | null;
  result: { reachable: boolean; connectMs?: number | null; failure?: string | null } | null;
}

export interface CameraCredentialTestAccepted {
  testId: string;
  status: string;
  statusUrl: string;
}

export interface CameraCredentialTestReport {
  reachable: boolean;
  authOutcome: 'authenticated' | 'credential_rejected' | 'not_verifiable' | 'unreachable' | 'error';
  connectMs?: number | null;
  detail?: string | null;
  failure?: string | null;
}

export interface CameraCredentialTestResult {
  id: string;
  cameraId: string;
  protocol: string;
  ipAddress: string;
  port: number;
  status: string;
  requestedAt: string;
  completedAt: string | null;
  failureReason: string | null;
  result: CameraCredentialTestReport | null;
}

/** Fields accepted by `PATCH /api/v1/cameras/{id}`. Omitted fields are unchanged; nullable fields clear when sent as `null`. */
export interface CameraPatchRequest {
  name?: string;
  manufacturer?: string | null;
  model?: string | null;
  serialNumber?: string | null;
  cameraType?: string;
  organizationUnitId?: string;
  geographicAreaId?: string;
  latitude?: number;
  longitude?: number;
  altitude?: number | null;
  mountingHeight?: number | null;
  azimuth?: number | null;
  tilt?: number | null;
  horizontalFov?: number | null;
  verticalFov?: number | null;
  effectiveRange?: number | null;
  ipAddress?: string | null;
  port?: number | null;
  protocol?: string | null;
  vmsId?: string | null;
  streamReference?: string | null;
  credentialReference?: string | null;
  installationDate?: string | null;
  operationalStatus?: string;
  connectivityStatus?: string;
  maintenanceStatus?: string;
  recordEvents?: boolean;
  streamPreference?: string;
  nativeHlsUrl?: string | null;
  nativeWebrtcUrl?: string | null;
}

export interface CameraResponse extends CameraWriteRequest {
  id: string;
  operationalStatus: string;
  connectivityStatus: string;
  maintenanceStatus: string;
  hasCoverage: boolean;
  lastSeenAt: string | null;
  lastHealthCheckAt: string | null;
  retiredAt: string | null;
}

export interface CameraPage {
  items: CameraResponse[];
  nextCursor: string | null;
}

/** The caller's saved video-wall layout — cameras in grid order, wrapping every `columnCount`
 * tiles. There are no empty-tile placeholders; `cameraIds.length` is the tile count. */
export interface VideoWallPreferences {
  columnCount: number;
  cameraIds: string[];
}

/** A short-lived, single-camera HLS viewing session (`GET /api/v1/streams/{cameraId}/session`) —
 * `token` is presented as `Authorization: Bearer {token}` on every playlist/segment request to
 * the streaming gateway, never to this API. */
export interface StreamSessionResponse {
  cameraId: string;
  token: string;
  expiresAt: string;
  /** The camera's `streamPreference` at mint time — `RTSP`/`HLS` both mean "play
   * `GET /{cameraId}/{*hlsPath}` as HLS"; `WEBRTC` means "use `POST /{cameraId}/whep` instead". */
  mode: string;
}

/** The `{ items, page, pageSize, total, totalPages }` envelope returned by any list endpoint that
 * opts into page/pageSize pagination (see `Paginate.Render` on the API side). */
export interface PageResult<T> {
  items: T[];
  page: number;
  pageSize: number;
  total: number;
  totalPages: number;
}

export interface CameraListQuery {
  limit?: number;
  cursor?: string;
  includeRetired?: boolean;
  organizationUnitId?: string;
  geographicAreaId?: string;
  cameraType?: string;
  operationalStatus?: string;
  connectivityStatus?: string;
  maintenanceStatus?: string;
  q?: string;
  bbox?: string;
}

export interface CreatedResponse {
  id: string;
}

export interface BulkImportRequest {
  mode: 'insert' | 'upsert';
  items: CameraWriteRequest[];
}

export interface BulkRowResult {
  index: number;
  cameraCode: string;
  status: string;
  cameraId: string | null;
  error: string | null;
}

export interface BulkImportResult {
  created: number;
  updated: number;
  failed: number;
  rows: BulkRowResult[];
}

export interface CameraHealthResponse {
  cameraId: string;
  operationalStatus: string;
  connectivityStatus: string;
  maintenanceStatus: string;
  lastSeenAt: string | null;
  lastHealthCheckAt: string | null;
  failureReason: string | null;
}

export interface CameraHealthCheckResponse {
  operationalStatus: string;
  connectivityStatus: string;
  checkedAt: string;
  latencyMs: number | null;
  errorCode: string | null;
  failureReason: string | null;
  source: string;
}

export interface CameraHealthHistoryResponse {
  cameraId: string;
  from: string;
  to: string;
  items: CameraHealthCheckResponse[];
}

export interface HealthOverrideRequest {
  reason: string;
  operationalStatus?: string | null;
  connectivityStatus?: string | null;
}

export interface MaintenanceRecordResponse {
  id: string;
  cameraId: string;
  maintenanceType: string;
  status: string;
  description: string;
  failureReason: string | null;
  reportedAt: string;
  startedAt: string | null;
  completedAt: string | null;
  nextDueAt: string | null;
  performedBy: string | null;
}

export interface MaintenanceCreateRequest {
  maintenanceType: string;
  description: string;
  status?: string | null;
  failureReason?: string | null;
  reportedAt?: string | null;
  startedAt?: string | null;
  nextDueAt?: string | null;
  performedBy?: string | null;
}

export interface MaintenanceUpdateRequest {
  status?: string | null;
  description?: string | null;
  failureReason?: string | null;
  startedAt?: string | null;
  completedAt?: string | null;
  nextDueAt?: string | null;
  performedBy?: string | null;
}

export interface GeoJsonGeometry {
  type: 'Point' | 'Polygon';
  coordinates: unknown;
}

export interface GeoJsonFeature {
  type: 'Feature';
  geometry: GeoJsonGeometry;
  properties: Record<string, unknown>;
}

export interface GeoJsonFeatureCollection {
  type: 'FeatureCollection';
  features: GeoJsonFeature[];
}

export interface CoverageSummaryResponse {
  buckets: Record<string, Record<string, number>>;
}

/** `GET /api/v1/gis/gaps` — the part of a geographic area's surveyed boundary that no in-scope
 * camera's estimated coverage sector reaches. `geometry: null` means every sector considered
 * unions to cover the whole boundary (no gap), not "unknown" — an area with no boundary at all
 * is a 404, not this shape with a null geometry. */
export interface GapAnalysisResponse {
  type: 'Feature';
  geometry: { type: string; coordinates: unknown } | null;
  properties: {
    geographicAreaId: string;
    cameraSectorsConsidered: number;
    estimated: true;
    disclaimer: string;
  };
}

// ---- VMS management (state, health, capabilities) ----------------------

export interface TargetStateRequest {
  state: 'Active' | 'Disabled' | 'Quarantined';
}

export interface ConnectorHealthResponse {
  checkedAt: string;
  status: string;
  latencyMs: number | null;
  cameraCount: number | null;
  consecutiveFailures: number;
  circuitOpen: boolean;
  lastError: string | null;
  eventsSinceCheck: number;
  cursorLagSeconds: number | null;
}

/** `supported` is a bitmask (see `Capability` in Federation.Core) — decode client-side to names. */
export interface CapabilityResponse {
  supported: number;
  adapterVersion: string;
  probedAt: string;
  notes: Record<string, string>;
}

export interface CameraStatusChangeResponse {
  changedAt: string;
  previousHealth: string | null;
  health: string;
  previousEnabled: boolean | null;
  isEnabled: boolean;
  previousRecording: boolean | null;
  isRecording: boolean | null;
}

export interface CameraStatusHistoryResponse {
  nativeCameraId: string;
  from: string;
  to: string;
  changes: CameraStatusChangeResponse[];
}

// ---- Camera reconciliation -----------------------------------------

export interface UnreconciledCameraResponse {
  targetId: string;
  nativeCameraId: string;
  name: string | null;
  vendorModel: string | null;
  firmware: string | null;
  organizationUnitId: string;
  geographicAreaId: string | null;
  latitude: number | null;
  longitude: number | null;
  lastSeen: string | null;
  streamReferences: string[];
}

export interface UnreconciledPage {
  items: UnreconciledCameraResponse[];
  nextCursor: string | null;
}

export interface ReconcileRequest {
  targetId: string;
  nativeCameraId: string;
  adoptStreamReference?: boolean;
  adoptVmsId?: boolean;
}

export interface ReconcileResponse {
  cameraId: string;
  targetId: string;
  nativeCameraId: string;
  vmsId: string | null;
}

export interface CreateFromFederatedRequest {
  targetId: string;
  nativeCameraId: string;
  cameraCode: string;
  cameraType: string;
  name?: string | null;
  organizationUnitId?: string | null;
  geographicAreaId?: string | null;
  latitude?: number | null;
  longitude?: number | null;
  azimuth?: number | null;
  horizontalFov?: number | null;
  effectiveRange?: number | null;
  adoptStreamReference?: boolean;
  adoptVmsId?: boolean;
}

export interface OrganizationRequest {
  code: string;
  name: string;
  organizationType: string;
  description?: string | null;
  status?: string | null;
}

export interface OrganizationResponse {
  id: string;
  code: string;
  name: string;
  organizationType: string;
  description: string | null;
  status: string;
}

export interface OrganizationUnitRequest {
  organizationId: string;
  code: string;
  name: string;
  unitType: string;
  parentUnitId?: string | null;
  status?: string | null;
}

export interface OrganizationUnitResponse {
  id: string;
  organizationId: string;
  parentUnitId: string | null;
  code: string;
  name: string;
  unitType: string;
  status: string;
}

/** `POST /api/v1/organization-units/{id}/move` — re-parents a unit into a different organization. */
export interface MoveUnitRequest {
  newParentUnitId: string;
  confirmScopeImpact?: boolean;
}

/** One access group whose organization scope points into a subtree being moved. */
export interface AffectedGroupResponse {
  id: string;
  code: string;
  memberCount: number;
}

export interface MoveUnitResponse {
  fromOrganizationId: string;
  toOrganizationId: string;
  subtreeSize: number;
  camerasFollowing: number;
  targetsFollowing: number;
  affectedGroups: AffectedGroupResponse[];
}

export interface GeographicAreaRequest {
  code: string;
  name: string;
  areaType: string;
  parentAreaId?: string | null;
  status?: string | null;
}

export interface AreaTypeResponse {
  code: string;
  name: string;
  levelOrder: number;
}

export interface VmsResponse {
  id: string;
  code: string;
  organizationUnitId: string;
  geographicAreaId: string | null;
  displayName: string;
  vendor: string;
  runtimeClass: string;
  endpoint: string;
  credentialReference: string;
  verifyTls: boolean;
  state: string;
  expectedCameraCount: number | null;
  lastInventoryPollAt: string | null;
  lastInventoryCameraCount: number | null;
}

export interface PollInventoryResponse {
  targetId: string;
  requestedAt: string;
  circuitOpen: boolean;
}

export interface ConnectorTargetRequest {
  code: string;
  organizationUnitId: string;
  displayName: string;
  vendor: string;
  endpoint: string;
  credentialReference: string;
  geographicAreaId?: string | null;
  verifyTls?: boolean;
  runtimeClass?: string | null;
  rateLimitPerSecond?: number | null;
  rateLimitBurst?: number | null;
  inventoryPollSeconds?: number | null;
  statusPollSeconds?: number | null;
  eventPollSeconds?: number | null;
  maxConcurrentRequests?: number | null;
  expectedCameraCount?: number | null;
}

export interface CredentialRequest {
  username?: string | null;
  password?: string | null;
  token?: string | null;
  description?: string | null;
}

export interface CredentialResponse {
  credentialReference: string;
  updatedAt: string;
}

export interface CredentialExistsResponse {
  reference: string;
  exists: boolean;
}

/** Creates a reusable named credential in the saved-credential library. Write-only, like
 * `CredentialRequest` — nothing here is ever echoed back. */
export interface SavedCredentialRequest {
  name: string;
  description?: string | null;
  username?: string | null;
  password?: string | null;
  token?: string | null;
}

/** Rotates or renames a saved credential. Supplying a password or a token reseals the SAME
 * `credentialReference` every camera pointed at this entry already carries — that's the rotation
 * route, and it takes effect for all of them with no per-camera write. Supplying neither only
 * updates `name`/`description`. */
export interface SavedCredentialUpdateRequest {
  name?: string;
  description?: string | null;
  username?: string | null;
  password?: string | null;
  token?: string | null;
}

/** One camera pointed at a saved credential's reference (`GET /credential-library/{id}`'s
 * `usedBy`). */
export interface SavedCredentialUsedByResponse {
  id: string;
  cameraCode: string;
  name: string;
}

/** A saved-credential library entry. Metadata only — `credentialReference` is the pointer a
 * camera's own `credentialReference` field is set to in order to share this entry's sealed
 * secret; it is never secret material itself. `usageCount` is a true total; `usedBy` is populated
 * only by `GET /credential-library/{id}` (null on the list route), scoped to cameras the caller
 * can see. */
export interface SavedCredentialResponse {
  id: string;
  name: string;
  description: string | null;
  credentialReference: string;
  createdAt: string;
  updatedAt: string;
  usageCount: number;
  usedBy?: SavedCredentialUsedByResponse[] | null;
}

export interface FederatedCameraResponse {
  nativeCameraId: string;
  cameraId: string | null;
  name: string | null;
  vendorModel: string | null;
  firmware: string | null;
  isEnabled: boolean;
  isRecording: boolean | null;
  health: string;
  lastSeen: string | null;
  streamReferences: string[];
  statusChangedAt: string | null;
}

export interface ConnectionTestAccepted {
  testId: string;
  status: string;
  statusUrl: string;
}

export interface ConnectionTestResult {
  testId: string;
  targetId: string;
  status: string;
  requestedAt: string;
  completedAt: string | null;
  failureReason: string | null;
  result: unknown | null;
}

export interface GeographicAreaResponse {
  id: string;
  parentAreaId: string | null;
  code: string;
  name: string;
  areaType: string;
  status: string;
}

/** One row of `POST /api/v1/geographic-areas/bulk-import-boundaries` — exactly one of `wkt`/
 * `geoJson` is required, describing a single WGS84 `Polygon` (no SRID prefix needed). */
export interface BoundaryImportItem {
  geographicAreaId: string;
  wkt?: string | null;
  geoJson?: string | null;
}

export interface BoundaryImportRequest {
  items: BoundaryImportItem[];
}

export interface BoundaryRowResult {
  index: number;
  geographicAreaId: string;
  status: string;
  error: string | null;
}

export interface BoundaryImportResult {
  updated: number;
  failed: number;
  rows: BoundaryRowResult[];
}

/** No hierarchy child strategy is implied when both fields are omitted. */
export interface DeactivateRequest {
  childStrategy?: 'cascade' | 'reparent';
  newParentId?: string;
}

export interface CreateGroupRequest {
  code: string;
  name: string;
  roleId: string;
  description?: string | null;
  status?: string | null;
}

export interface AddScopeRequest {
  scopeType: string;
  organizationUnitId?: string | null;
  geographicAreaId?: string | null;
  resourceType?: string | null;
  resourceId?: string | null;
  description?: string | null;
}

export interface ScopeResponse {
  id: string;
  scopeType: string;
  organizationUnitId: string | null;
  geographicAreaId: string | null;
  resourceType: string | null;
  resourceId: string | null;
  description: string | null;
}

export interface AccessGroupResponse {
  id: string;
  code: string;
  name: string;
  description: string | null;
  status: string;
  roleCode: string;
  roleId?: string;
  permissions: string[];
  scopes: ScopeResponse[];
  memberCount: number;
}

export interface UpdateGroupRequest {
  code: string;
  name: string;
  roleId: string;
  description?: string | null;
}

export interface ActivateGroupRequest {
  confirmUnscoped?: boolean;
}

export interface GroupMemberResponse {
  userId: string;
  username: string;
  expiresAt: string | null;
}

export interface RolePermissionDetailResponse {
  code: string;
  name: string;
  category: string;
  description: string | null;
}

export interface RoleUsedByResponse {
  id: string;
  code: string;
  name: string;
  status: string;
}

export interface RoleWriteRequest {
  code: string;
  name: string;
  permissions: string[];
  description?: string | null;
  status?: string | null;
}

export interface RoleResponse {
  id: string;
  code: string;
  name: string;
  description: string | null;
  isSystem: boolean;
  status?: string;
  customized?: boolean;
  createdAt?: string;
  updatedAt?: string;
  customizedAt?: string | null;
  usageCount?: number;
  permissions?: string[];
  permissionDetails?: RolePermissionDetailResponse[] | null;
  usedBy?: RoleUsedByResponse[] | null;
}

export interface PermissionResponse {
  code: string;
  name: string;
  category: string;
  description: string | null;
}

export interface CreateWatchlistEntryRequest {
  organizationUnitId: string;
  plateNumber: string;
  reason?: string | null;
  severity?: string;
}

/** Adding a plate backfills alerts for any prior sighting already in detection_event, in the
 * same transaction as creating the entry — this reports how many, and whether the historical
 * scan hit its cap (1000) rather than being exhaustive. */
export interface WatchlistEntryCreatedResponse {
  id: string;
  historicalAlertsRaised: number;
  historicalMatchesCapped: boolean;
}

export interface WatchlistEntryResponse {
  id: string;
  organizationUnitId: string;
  plateNumberNormalized: string;
  reason: string | null;
  severity: string;
  isActive: boolean;
  createdAt: string;
}

// ---- Cross-camera correlation (Model 3) --------------------------------

/** One cross-camera possible match. `confidence` is a possible-match score in [0,1] and is
 * never certainty — cross-camera identity is probabilistic. */
export interface CorrelationGroupSummary {
  id: string;
  ruleCode: string;
  naturalKey: string;
  windowBucket: string;
  confidence: number;
  memberCount: number;
  firstOccurredAt: string;
  lastOccurredAt: string;
  createdAt: string;
}

export interface CorrelationGroupMember {
  federationEventId: string;
  occurredAt: string;
  cameraId: string;
  sourceVmsId: string;
  organizationUnitId: string;
  geographicAreaId: string | null;
}

export interface CorrelationGroupDetail {
  group: CorrelationGroupSummary;
  members: CorrelationGroupMember[];
}

export interface WatchlistAlertResponse {
  id: string;
  watchlistEntryId: string;
  plateNumberNormalized: string;
  reason: string | null;
  severity: string;
  detectionEventId: string;
  detectionOccurredAt: string;
  raisedAt: string;
  acknowledgedAt: string | null;
}

export interface CreateApiKeyRequest {
  displayName: string;
  groupId: string;
  expiresAt?: string | null;
}

/** The only API-key response carrying secret material; returned once on creation. */
export interface ApiKeyCreatedResponse {
  id: string;
  keyId: string;
  rawKey: string;
}

/** Persisted API-key metadata. Raw key material is deliberately absent. */
export interface ApiKeyResponse {
  id: string;
  keyId: string;
  displayName: string;
  groupId: string;
  groupCode: string;
  createdAt: string;
  expiresAt: string | null;
  lastUsedAt: string | null;
  revokedAt: string | null;
}

export interface AiWorkerHealthResponse {
  id: string;
  apiKeyId: string;
  apiKeyName: string;
  workerId: string;
  hostname: string;
  firstSeenAt: string;
  lastHeartbeatAt: string;
  reportedAt: string | null;
  clockDriftSeconds: number | null;
  /** How many cameras this worker currently holds a lease on (federation.camera_worker_lease) —
   * 0 for a worker that has never claimed any, not necessarily one that's unhealthy. */
  leasedCameraCount: number;
  leasedCameraRefs: string[];
  /** Same order as leasedCameraRefs — the camera's own display name where it resolves, falling
   * back to the raw ref for an unnamed camera or an unresolved VMS reference. */
  leasedCameraNames: string[];
}

// ---- Users and group membership -------------------------------------------

/** A platform user account. Never carries password material. */
export interface UserResponse {
  id: string;
  username: string;
  displayName: string;
  email: string | null;
  mustChangePassword: boolean;
  status: string;
  lastLoginAt: string | null;
  isSystem: boolean;
  /** Descriptive HR metadata only (which department/area/title) — never an access-control
   * input; access comes entirely from the user's Access Group memberships. */
  organizationUnitId: string | null;
  organizationUnitName: string | null;
  geographicAreaId: string | null;
  geographicAreaName: string | null;
  designation: string | null;
}

/** A group a user belongs to, with any expiry on that membership. */
export interface UserGroupResponse {
  groupId: string;
  code: string;
  name: string;
  expiresAt: string | null;
}

export interface CreateUserRequest {
  username: string;
  displayName: string;
  password: string;
  email?: string | null;
  organizationUnitId?: string | null;
  geographicAreaId?: string | null;
  designation?: string | null;
}

/** A full replace, like every other PUT in this API — a field left out clears it, the same as
 * sending it as `null` explicitly. */
export interface UpdateUserRequest {
  displayName: string;
  email?: string | null;
  status?: string | null;
  organizationUnitId?: string | null;
  geographicAreaId?: string | null;
  designation?: string | null;
}

export interface ResetPasswordRequest {
  newPassword: string;
}

export interface AssignGroupRequest {
  groupId: string;
  expiresAt?: string | null;
}

// ---- Detections (read side; ingest is the AI worker's, not a UI action) ----

export interface DetectionResponse {
  id: string;
  cameraId: string;
  registeredCameraId: string | null;
  /** The camera's own display name, resolved server-side — null only when neither a standalone
   * registry camera nor a VMS-federated camera has a name on file for this detection. */
  cameraName: string | null;
  eventType: string;
  timestamp: string;
  confidence: number | null;
  vehicleType: string | null;
  plateNumber: string | null;
  snapshotReference: string | null;
  /** Free-form operator tags (v1.26) — distinct from `eventType`'s closed machine
   * classification. Empty, never absent, when the detection carries none. */
  tags: string[];
}

/** `POST /api/v1/detections/{eventId}/tags` — `occurredAt` must match the detection's own
 * `timestamp`, since `(occurredAt, eventId)` together identify it. */
export interface DetectionTagRequest {
  occurredAt: string;
  tag: string;
}

// ---- Ageing-infrastructure report (registry reporting) --------------------

export interface AgeingInfrastructureBucketResponse {
  bucket: string;
  label: string;
  count: number;
}

export interface AgeingCameraSummaryResponse {
  id: string;
  cameraCode: string;
  name: string;
  installationDate: string;
  ageYears: number;
  maintenanceStatus: string;
}

export interface AgeingInfrastructureResponse {
  totalCameras: number;
  buckets: AgeingInfrastructureBucketResponse[];
  oldestCameras: AgeingCameraSummaryResponse[];
}

// ---- Events (hot-window query) -------------------------------------

export interface EventSummary {
  eventId: string;
  sourceVmsId: string;
  cameraId: string;
  eventType: string;
  vendorEventType: string | null;
  occurredAt: string;
  severity: string;
  objectReference: string | null;
  confidence: number | null;
}

export interface EventPage {
  events: EventSummary[];
  nextCursor: string | null;
}

export interface OverviewResponse {
  targets: number;
  activeTargets: number;
  quarantinedTargets: number;
  cameras: number;
  unreachableCameras: number;
}
