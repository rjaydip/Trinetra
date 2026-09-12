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

export interface WatchlistEntryResponse {
  id: string;
  organizationUnitId: string;
  plateNumberNormalized: string;
  reason: string | null;
  severity: string;
  isActive: boolean;
  createdAt: string;
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

export interface OverviewResponse {
  targets: number;
  activeTargets: number;
  quarantinedTargets: number;
  cameras: number;
  unreachableCameras: number;
}
