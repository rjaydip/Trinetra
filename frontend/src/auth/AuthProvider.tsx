import { createContext, useContext, useEffect, useMemo, useState, type ReactNode } from 'react';

import { api } from '../api/endpoints';
import type { ChangePasswordRequest, LoginRequest } from '../api/models';
import { clearSession, readSession, saveSession, subscribeToSession, type AuthSession } from './session';

interface AuthContextValue {
  session: AuthSession | null;
  login(credentials: LoginRequest): Promise<AuthSession>;
  changePassword(request: ChangePasswordRequest): Promise<AuthSession>;
  logout(): void;
}

const AuthContext = createContext<AuthContextValue | null>(null);

export function AuthProvider({ children }: { children: ReactNode }) {
  const [session, setSession] = useState<AuthSession | null>(() => readSession());

  useEffect(() => subscribeToSession(() => setSession(readSession())), []);

  useEffect(() => {
    if (!session) return undefined;

    const delay = Date.parse(session.expiresAt) - Date.now();
    if (!Number.isFinite(delay) || delay <= 0) {
      clearSession();
      return undefined;
    }

    const expiryTimer = window.setTimeout(clearSession, delay);
    return () => window.clearTimeout(expiryTimer);
  }, [session]);

  const value = useMemo<AuthContextValue>(() => ({
    session,
    async login(credentials) {
      const payload: LoginRequest = {
        username: credentials.username || (import.meta.env.VITE_USERNAME ?? ''),
        password: credentials.password || (import.meta.env.VITE_PASSWORD ?? ''),
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
    logout: clearSession,
  }), [session]);

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

export function useAuth(): AuthContextValue {
  const auth = useContext(AuthContext);
  if (!auth) throw new Error('useAuth must be used inside AuthProvider.');
  return auth;
}
