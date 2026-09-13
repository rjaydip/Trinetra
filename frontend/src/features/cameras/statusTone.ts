/** Shared by every place a camera's operational/connectivity/maintenance status is shown as a
 * StatusBadge — was duplicated three ways (CameraTable, CameraCards, CameraDetailSections) and
 * had already drifted: CameraDetailSections was missing UNDER_MAINTENANCE from the warning
 * bucket, so the same status could read as warning in one place and neutral in another.
 *
 * Covers the exact enum values for all three fields (db/versions/v1.6.sql cameras table CHECK
 * constraints): operationalStatus ONLINE/OFFLINE/DEGRADED/UNKNOWN, connectivityStatus
 * CONNECTED/DISCONNECTED/UNKNOWN, maintenanceStatus NORMAL/REQUIRED/UNDER_MAINTENANCE/RETIRED.
 * The connectivity values were missing entirely until now, so every camera's connectivity badge
 * silently fell through to neutral gray regardless of whether it was connected or disconnected —
 * the one column meant to answer "is this camera reachable" carried no color signal at all. */
export function statusTone(status: string): 'success' | 'warning' | 'danger' | 'neutral' {
  const value = status.toUpperCase();
  if (['ACTIVE', 'ONLINE', 'CONNECTED', 'NORMAL', 'CURRENT'].includes(value)) return 'success';
  if (['OFFLINE', 'DISCONNECTED', 'FAILED', 'RETIRED'].includes(value)) return 'danger';
  if (['DEGRADED', 'MAINTENANCE', 'UNDER_MAINTENANCE', 'REQUIRED', 'DUE'].includes(value)) return 'warning';
  return 'neutral';
}
