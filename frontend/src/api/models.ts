export interface ApiProblemShape {
  title?: string;
  detail?: string;
  status?: number;
  errors?: Record<string, string[]>;
}

export interface AuthResponse {
  token: string;
  expiresAt: string;
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
  siteId: string;
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
  siteId?: string;
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

export interface OrganizationResponse {
  id: string;
  code: string;
  name: string;
  organizationType: string;
  description: string | null;
  status: string;
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

export interface SiteResponse {
  id: string;
  code: string;
  name: string;
  geographicAreaId: string;
  siteType: string | null;
  address: string | null;
  latitude: number | null;
  longitude: number | null;
  status: string;
}

export interface GeographicAreaResponse {
  id: string;
  parentAreaId: string | null;
  code: string;
  name: string;
  areaType: string;
  status: string;
}

export interface OverviewResponse {
  targets: number;
  activeTargets: number;
  quarantinedTargets: number;
  cameras: number;
  unreachableCameras: number;
}
