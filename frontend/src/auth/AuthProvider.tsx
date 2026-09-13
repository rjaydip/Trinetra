import { createContext, useContext, useEffect, useMemo, useState, type ReactNode } from 'react';

import { api } from '../api/endpoints';
import type { ChangePasswordRequest, LoginRequest } from '../api/models';
import { devCredentials } from '../config/env';
import { clearSession, readSession, saveSession, subscribeToSession, type AuthSession } from './session';

interface AuthContextValue {
  session: AuthSession | null;
  login(credentials: LoginRequest): Promise<AuthSession>;
  changePassword(request: ChangePasswordRequest): Promise<AuthSession>;
  logout(): Promise<void>;
}

const AuthContext = createContext<AuthContextValue | null>(null);

// Refresh this far ahead of the access token's actual expiry, so the request round trip and any
// clock drift against the server can never land after the token has already gone stale.
const REFRESH_SKEW_MS = 30_000;

export function AuthProvider({ children }: { children: ReactNode }) {
  const [session, setSession] = useState<AuthSession | null>(() => readSession());

  useEffect(() => subscribeToSession(() => setSession(readSession())), []);

  useEffect(() => {
    if (!session) return undefined;

    // No refresh token (e.g. a change-password response before that rotates in) — fall back to
    // the old behaviour of just ending the session at expiry.
    if (!session.refreshToken) {
      const delay = Date.parse(session.expiresAt) - Date.now();
      if (!Number.isFinite(delay) || delay <= 0) {
        clearSession();
        return undefined;
      }
      const expiryTimer = window.setTimeout(clearSession, delay);
      return () => window.clearTimeout(expiryTimer);
    }

    const delay = Date.parse(session.expiresAt) - Date.now() - REFRESH_SKEW_MS;
    let cancelled = false;
    const refreshTimer = window.setTimeout(() => {
      void (async () => {
        try {
          const nextSession = await api.auth.refresh(session.refreshToken!);
          if (!cancelled) saveSession(nextSession);
        } catch {
          // An unknown, expired, revoked, or already-rotated refresh token — the session is
          // genuinely over; the user has to log in again.
          if (!cancelled) clearSession();
        }
      })();
    }, Math.max(delay, 0));

    return () => {
      cancelled = true;
      window.clearTimeout(refreshTimer);
    };
  }, [session]);

  const value = useMemo<AuthContextValue>(() => ({
    session,
    async login(credentials) {
      const payload: LoginRequest = {
        username: credentials.username || devCredentials().username,
        password: credentials.password || devCredentials().password,
      };
      const nextSession = await api.auth.login(payload);
      saveSession(nextSession);
      return nextSession;
    },
    async changePassword(request) {
      const nextSession = await api.auth.changePassword(request);
      saveSession(nextSession);
      return nextSession;
    },
    async logout() {
      try {
        await api.auth.logout();
      } catch {
        // Best-effort: the server-side revocation may fail (already logged out, network drop),
        // but the local session must clear either way so the user is signed out on this device.
      } finally {
        clearSession();
      }
    },
  }), [session]);

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

export function useAuth(): AuthContextValue {
  const auth = useContext(AuthContext);
  if (!auth) throw new Error('useAuth must be used inside AuthProvider.');
  return auth;
}
