import type { AuthResponse } from '../api/models';

export const SESSION_STORAGE_KEY = 'trinetra.auth.session';

export type AuthSession = AuthResponse;

type SessionListener = () => void;

const listeners = new Set<SessionListener>();

function notify(): void {
  listeners.forEach((listener) => listener());
}

function isExpired(session: AuthSession): boolean {
  const expiresAt = Date.parse(session.expiresAt);
  return !Number.isFinite(expiresAt) || expiresAt <= Date.now();
}

export function readSession(): AuthSession | null {
  const stored = sessionStorage.getItem(SESSION_STORAGE_KEY);
  if (!stored) return null;

  try {
    const session = JSON.parse(stored) as AuthSession;
    if (!session.token || !session.expiresAt || isExpired(session)) {
      clearSession();
      return null;
    }

    return session;
  } catch {
    clearSession();
    return null;
  }
}

export function saveSession(session: AuthSession): void {
  sessionStorage.setItem(SESSION_STORAGE_KEY, JSON.stringify(session));
  notify();
}

export function clearSession(): void {
  sessionStorage.removeItem(SESSION_STORAGE_KEY);
  notify();
}

export function subscribeToSession(listener: SessionListener): () => void {
  listeners.add(listener);
  return () => listeners.delete(listener);
}
