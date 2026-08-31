import type { ReactNode } from 'react';
import { Link } from 'react-router-dom';
import { PageState } from '../components/ui';
import { useAuth } from './AuthProvider';
import { hasPermission } from './permissions';

export function RequirePermission({ permission, children }: { permission: string; children: ReactNode }) {
  const { session } = useAuth();
  if (!hasPermission(session, permission)) return <section>
    <PageState title="Action unavailable">Your current session does not include permission for this action. The server controls access.</PageState>
    <Link to="/cameras">Back to camera registry</Link>
  </section>;
  return <>{children}</>;
}
