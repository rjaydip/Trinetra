/** Shared by the dashboard's attention list and the camera registry table/cards — anything that
 * shows how long a camera has been unreachable. `EMPHASIZE_AFTER_HOURS` gates the stronger
 * StatusBadge treatment; keep both consumers using the same threshold so "down a long time" means
 * the same thing everywhere in the app. */
export const EMPHASIZE_AFTER_HOURS = 24;

export function downSince(lastSeenAt: string | null): { label: string; hours: number } {
  if (!lastSeenAt) return { label: 'Never reported', hours: Infinity };
  const hours = (Date.now() - new Date(lastSeenAt).getTime()) / (60 * 60 * 1000);
  if (hours < 1) return { label: `${Math.max(1, Math.round(hours * 60))}m`, hours };
  if (hours < 48) return { label: `${Math.round(hours)}h`, hours };
  return { label: `${Math.round(hours / 24)}d`, hours };
}
