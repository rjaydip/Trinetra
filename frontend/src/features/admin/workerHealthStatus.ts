/** Shared with the dashboard's worker-health pill — keep both readings of "is a worker alive" in sync. */
export const WORKER_STALE_AFTER_MINUTES = 5;

export function isWorkerStale(lastHeartbeatAt: string): boolean {
  return Date.now() - new Date(lastHeartbeatAt).getTime() > WORKER_STALE_AFTER_MINUTES * 60 * 1000;
}
