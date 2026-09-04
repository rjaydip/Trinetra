import type { ReactNode } from 'react';
import { Link, NavLink, Outlet, useLocation } from 'react-router-dom';

import { useAuth } from '../../auth/AuthProvider';
import { hasPermission } from '../../auth/permissions';
import { PageState } from '../../components/ui';

export const supportedAdminReadPermissions = ['organization.read', 'geography.read', 'group.read'] as const;

export function RequireAnyAdminPermission({ permissions, children }: { permissions: readonly string[]; children: ReactNode }) {
  const { session } = useAuth();
  if (!permissions.some((permission) => hasPermission(session, permission))) {
    return <section>
      <PageState title="Action unavailable">Your current session does not include permission for this admin area. The server controls access.</PageState>
      <Link to="/dashboard">Back to dashboard</Link>
    </section>;
  }
  return <>{children}</>;
}

const adminDestinations = [
  {
    to: '/admin/hierarchy',
    label: 'Hierarchy',
    description: 'Organizations, units, geographic areas, and sites.',
    permissions: ['organization.read', 'geography.read'],
  },
  {
    to: '/admin/roles',
    label: 'Roles & permissions',
    description: 'Read the platform role and permission catalogue.',
    permissions: ['group.read'],
  },
  {
    to: '/admin/access-groups',
    label: 'Access groups',
    description: 'Review role grants, members, and access scopes.',
    permissions: ['group.read'],
  },
];

export function AdminPage() {
  const { session } = useAuth();
  const location = useLocation();
  const availableDestinations = adminDestinations.filter((destination) => (
    destination.permissions.some((permission) => hasPermission(session, permission))
  ));
  const isLanding = location.pathname.replace(/\/$/, '') === '/admin';

  return <section className="admin-page" aria-labelledby="admin-title">
    <header className="admin-page__header">
      <div><p className="eyebrow">Administration</p><h1 id="admin-title">Control plane</h1></div>
      <p>Review the structures that determine ownership and access. Navigation is a presentation aid; the API remains the authorization authority.</p>
    </header>
    <nav className="admin-nav" aria-label="Admin navigation">
      {availableDestinations.map((destination) => <NavLink key={destination.to} to={destination.to}>{destination.label}</NavLink>)}
    </nav>
    {isLanding ? <div className="admin-destination-grid">
      {availableDestinations.map((destination) => <article key={destination.to}>
        <p className="eyebrow">Admin area</p>
        <h2><Link to={destination.to}>{destination.label}</Link></h2>
        <p>{destination.description}</p>
      </article>)}
    </div> : <Outlet />}
  </section>;
}
