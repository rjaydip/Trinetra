import type { AuthSession } from './session';

/**
 * Reads the JWT payload from an already-issued token — a presentation hint, NOT signature
 * validation or authorization. The server remains the sole authority on what a token actually
 * grants; this is only used to decide what the UI shows.
 */
export function decodeToken(session: AuthSession | null): Record<string, unknown> | null {
  if (!session) return null;
  try {
    const parts = session.token.split('.');
    if (parts.length !== 3) return null;
    const base64 = parts[1].replaceAll('-', '+').replaceAll('_', '/');
    // atob() requires proper base64 padding, but a JWT's base64url segments have their `=`
    // padding stripped per RFC 7515 — whether the unpadded length happens to already be a
    // multiple of 4 depends on the exact byte size of the payload (how many permissions, how
    // long the username is, etc.), so this failed unpredictably from one token to the next
    // until padding is restored here.
    const encoded = base64.padEnd(base64.length + (4 - (base64.length % 4)) % 4, '=');
    const payload: unknown = JSON.parse(new TextDecoder().decode(Uint8Array.from(atob(encoded), (value) => value.charCodeAt(0))));
    return payload && typeof payload === 'object' ? payload as Record<string, unknown> : null;
  } catch {
    return null;
  }
}

export function hasPermission(session: AuthSession | null, permission: string): boolean {
  const claim = decodeToken(session)?.['trinetra:perm'];
  return typeof claim === 'string' ? claim === permission : Array.isArray(claim) && claim.includes(permission);
}

/** The `unique_name` claim — the actual username. `sub` is deliberately NOT used here: the
 * backend puts the user's internal id (a GUID) in `sub` (JwtTokenService.cs), so reading `sub`
 * would render a raw UUID as the "username" instead of the real one. */
export function usernameFromSession(session: AuthSession | null): string | null {
  const username = decodeToken(session)?.unique_name;
  return typeof username === 'string' && username.length > 0 ? username : null;
}

/** Every permission code the token carries, sorted for stable display. There is no role claim in
 * this token model — many fine-grained permissions, not one role — so this is the real answer to
 * "what can this user do", not a count or an invented label. */
export function permissionsFromSession(session: AuthSession | null): string[] {
  const claim = decodeToken(session)?.['trinetra:perm'];
  const permissions = typeof claim === 'string' ? [claim] : Array.isArray(claim) ? claim.filter((value): value is string => typeof value === 'string') : [];
  return [...permissions].sort((a, b) => a.localeCompare(b));
}

/** Count of permissions the token carries — used as the compact subtitle before the full list is
 * expanded. */
export function permissionCountFromSession(session: AuthSession | null): number {
  return permissionsFromSession(session).length;
}
