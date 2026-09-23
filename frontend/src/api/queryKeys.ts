// Central React Query key factory. A hand-typed literal array (`['vms']`) repeated across files
// is a silent typo risk — one file misspelling `'organization-units'` doesn't error, it just
// creates a second, disconnected cache entry that never gets invalidated together with the rest.
// Every query and every `invalidateQueries` call should come from here instead of a literal.
//
// A "base"/`All` key is exposed wherever a caller needs to invalidate every parameterized variant
// at once — React Query matches by prefix, so invalidating `queryKeys.vms.all` also invalidates
// `queryKeys.vms.detail(id)` for every id.

export const queryKeys = {
  overview: ['overview'] as const,
  ageingInfrastructure: ['ageing-infrastructure'] as const,
  cameraCount: ['cameras', 'count'] as const,

  vms: {
    all: ['vms'] as const,
    page: (page: number, pageSize: number) => ['vms', 'page', page, pageSize] as const,
    detail: (id: string) => ['vms', id] as const,
    credentialStatus: (id: string) => ['vms', id, 'credential-status'] as const,
    health: (id: string) => ['vms', id, 'health'] as const,
    capabilities: (id: string) => ['vms', id, 'capabilities'] as const,
    discoveredCameras: (id: string) => ['vms', id, 'discovered-cameras'] as const,
    cameraStatusHistory: (id: string, nativeCameraId: string) => ['vms', id, 'discovered-cameras', nativeCameraId, 'status-history'] as const,
    connectionTest: (id: string, testId: string) => ['vms', id, 'connection-test', testId] as const,
  },

  cameras: {
    all: ['cameras'] as const,
    registry: (search: string) => ['cameras', 'registry', search] as const,
    search: (query: string) => ['cameras', 'search', query] as const,
    mapBootstrap: ['cameras', 'map-bootstrap'] as const,
    dashboardSearch: (query: string) => ['cameras', 'dashboard-search', query] as const,
    attentionList: ['cameras', 'attention-list'] as const,
  },

  credentialLibrary: {
    all: ['credential-library'] as const,
  },

  dashboardFooter: {
    workerHealth: ['dashboard-footer', 'worker-health'] as const,
    unacknowledgedAlerts: ['dashboard-footer', 'unacknowledged-alerts'] as const,
  },

  camera: {
    all: ['camera'] as const,
    detail: (id: string) => ['camera', id] as const,
    health: (id: string) => ['camera', id, 'health'] as const,
    healthHistory: (id: string) => ['camera', id, 'health-history'] as const,
    maintenance: (id: string) => ['camera', id, 'maintenance'] as const,
  },

  gisCameras: {
    all: ['gis-cameras'] as const,
    feed: (params: string | undefined) => ['gis-cameras', params] as const,
    cameraPicker: (bbox: string | null) => ['gis-cameras', 'camera-picker', bbox] as const,
    vmsDiscoveryPicker: (nativeCameraId: string, bbox: string | null) =>
      ['gis-cameras', 'vms-discovery-picker', nativeCameraId, bbox] as const,
  },

  reconciliation: {
    unreconciledAll: ['reconciliation', 'unreconciled'] as const,
    unreconciled: (targetId: string, cursor?: string) => ['reconciliation', 'unreconciled', targetId, cursor ?? null] as const,
  },

  reference: {
    organizations: ['reference', 'organizations'] as const,
    organizationUnits: (organizationId: string) => ['reference', 'organization-units', organizationId] as const,
    organizationUnit: (id: string) => ['reference', 'organization-unit', id] as const,
    geographicAreas: ['reference', 'geographic-areas'] as const,
    geographicArea: (id: string) => ['reference', 'geographic-area', id] as const,
  },

  admin: {
    organizations: ['admin', 'organizations'] as const,
    organizationUnitsAll: ['admin', 'organization-units'] as const,
    organizationUnits: (organizationId: string) => ['admin', 'organization-units', organizationId] as const,
    geographicAreas: ['admin', 'geographic-areas'] as const,
    geographicAreaTypes: ['admin', 'geographic-area-types'] as const,
    roles: (includeInactive?: boolean) =>
      (includeInactive === undefined ? ['admin', 'roles'] as const : ['admin', 'roles', { includeInactive }] as const),
    role: (id: string) => ['admin', 'roles', id] as const,
    permissions: ['admin', 'permissions'] as const,
    accessGroups: ['admin', 'access-groups'] as const,
    accessGroupsPage: (page: number, pageSize: number) => ['admin', 'access-groups', 'page', page, pageSize] as const,
    accessGroup: (id: string) => ['admin', 'access-groups', id] as const,
    accessGroupMembers: (id: string) => ['admin', 'access-groups', id, 'members'] as const,
    accessGroupMembersPage: (id: string, page: number, pageSize: number) =>
      ['admin', 'access-groups', id, 'members', 'page', page, pageSize] as const,
    scopeOrganizations: ['admin', 'scope-organizations'] as const,
    scopeOrganizationUnits: (organizationId: string) => ['admin', 'scope-organization-units', organizationId] as const,
    scopeAreas: ['admin', 'scope-areas'] as const,
  },

  users: {
    allPages: ['users', 'page'] as const,
    page: (page: number, pageSize: number) => ['users', 'page', page, pageSize] as const,
    detail: (id: string) => ['users', id] as const,
    groups: (id: string) => ['users', id, 'groups'] as const,
    permissions: (id: string) => ['users', id, 'permissions'] as const,
  },

  apiKeys: {
    allPages: ['api-keys', 'page'] as const,
    page: (page: number, pageSize: number) => ['api-keys', 'page', page, pageSize] as const,
  },

  workerHealth: {
    allPages: ['worker-health', 'page'] as const,
    page: (page: number, pageSize: number) => ['worker-health', 'page', page, pageSize] as const,
  },

  watchlist: {
    allEntries: ['watchlist', 'entries'] as const,
    entriesPage: (active: boolean | undefined, page: number, pageSize: number) =>
      ['watchlist', 'entries', 'page', active, page, pageSize] as const,
    allAlerts: ['watchlist', 'alerts'] as const,
    alertsPage: (acknowledged: boolean | undefined, page: number, pageSize: number) =>
      ['watchlist', 'alerts', 'page', acknowledged, page, pageSize] as const,
  },

  coverageSummary: (scope: { organizationUnitId?: string; geographicAreaId?: string } | null) =>
    ['coverage-summary', scope] as const,

  coverageGaps: (geographicAreaId: string | null) => ['coverage-gaps', geographicAreaId] as const,

  correlationGroups: (from: string | undefined, to: string | undefined) => ['correlation', 'groups', from, to] as const,

  detections: (plateNumber: string, targetId: string, from: string | undefined, to: string | undefined) =>
    ['detections', plateNumber, targetId, from, to] as const,

  videoWall: {
    picker: (q: string, organizationUnitId?: string, geographicAreaId?: string) =>
      ['video-wall', 'picker', q, organizationUnitId, geographicAreaId] as const,
    preferences: ['video-wall', 'preferences'] as const,
  },

  streamSession: (cameraId: string) => ['stream-session', cameraId] as const,

  events: (
    from: string | null, to: string | null, cameraId: string, eventType: string,
    objectReference: string, cursor: string | undefined,
  ) => ['events', from, to, cameraId, eventType, objectReference, cursor] as const,
};
