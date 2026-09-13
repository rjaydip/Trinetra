/** Shared by every place a VMS target's state or a connector/discovered-camera health check is
 * shown as a StatusBadge. Two separate enums — never conflate them into one mapping:
 *
 * - target_state (db/versions/v1.sql): Active / Disabled / Quarantined. Was previously mapped as
 *   `state === 'active' ? success : warning`, which put Quarantined (a genuinely bad state — the
 *   target has been suspended) in the same bucket as Disabled (merely inactive by choice).
 * - health_status (db/versions/v1.sql): Unknown / Healthy / Degraded / Unreachable / AuthFailed.
 *   Was previously checked against 'ok' (VmsManagement.tsx) and 'online' (DiscoveryPage.tsx) —
 *   neither value exists in the real enum, so every health check and every discovered camera's
 *   health rendered as a flat warning color regardless of actual status, including Healthy ones.
 */
export function vmsStateTone(state: string): 'success' | 'warning' | 'danger' | 'neutral' {
  const value = state.toUpperCase();
  if (value === 'ACTIVE') return 'success';
  if (value === 'QUARANTINED') return 'danger';
  return 'neutral';
}

export function healthStatusTone(status: string): 'success' | 'warning' | 'danger' | 'neutral' {
  const value = status.toUpperCase();
  if (value === 'HEALTHY') return 'success';
  if (value === 'DEGRADED') return 'warning';
  if (value === 'UNREACHABLE' || value === 'AUTHFAILED') return 'danger';
  return 'neutral';
}
