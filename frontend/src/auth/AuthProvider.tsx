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

  const value = useMemo<AuthContextValue>(() => ({
    session,
    async login(credentials) {
      const nextSession = await api.auth.login(credentials);
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
