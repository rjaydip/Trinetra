import type { ReactNode } from 'react';
import { Navigate, useLocation } from 'react-router-dom';

import { useAuth } from './AuthProvider';

export function RequireAuth({ children, passwordChangeOnly = false }: { children: ReactNode; passwordChangeOnly?: boolean }) {
  const { session } = useAuth();
  const location = useLocation();

  if (!session) return <Navigate replace state={{ from: location }} to="/login" />;
  if (passwordChangeOnly) {
    return session.mustChangePassword ? <>{children}</> : <Navigate replace to="/dashboard" />;
  }
  return session.mustChangePassword ? <Navigate replace to="/password" /> : <>{children}</>;
}
