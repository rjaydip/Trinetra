import type { AuthSession } from './session';

/** Presentation hints from an already-issued token, NOT signature validation or authorization. */
export function hasPermission(session: AuthSession | null, permission: string): boolean {
  if (!session) return false;
  try {
    const parts = session.token.split('.');
    if (parts.length !== 3) return false;
    const encoded = parts[1].replaceAll('-', '+').replaceAll('_', '/');
    const payload: unknown = JSON.parse(new TextDecoder().decode(Uint8Array.from(atob(encoded), (value) => value.charCodeAt(0))));
    if (!payload || typeof payload !== 'object') return false;
    const claim = (payload as Record<string, unknown>)['trinetra:perm'];
    return typeof claim === 'string' ? claim === permission : Array.isArray(claim) && claim.includes(permission);
  } catch {
    return false;
  }
}
