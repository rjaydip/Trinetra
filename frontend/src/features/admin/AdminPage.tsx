import { useEffect, type ReactNode } from 'react';
import { Link, NavLink, Outlet, useLocation } from 'react-router-dom';

import { useAuth } from '../../auth/AuthProvider';
import { hasPermission } from '../../auth/permissions';
import { PageState } from '../../components/ui';
import './admin.css';

export const supportedAdminReadPermissions = [
  'organization.read', 'geography.read', 'geography.manage', 'group.read', 'user.read', 'apikey.read', 'worker.read', 'alert.read',
] as const;

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
    description: 'Organizations, units, and geographic areas.',
    permissions: ['organization.read', 'geography.read'],
  },
  {
    to: '/admin/roles',
    label: 'Roles & permissions',
    description: 'Maintain roles, workflow statuses, and permissions catalogue.',
    permissions: ['role.read', 'group.read'],
  },
  {
    to: '/admin/access-groups',
    label: 'Access groups',
    description: 'Review role grants, members, and access scopes.',
    permissions: ['group.read'],
  },
  {
    to: '/admin/users',
    label: 'Users',
    description: 'Accounts, group membership, and effective permissions.',
    permissions: ['user.read'],
  },
  {
    to: '/admin/api-keys',
    label: 'API keys',
    description: 'Provision and revoke machine-to-machine access keys.',
    permissions: ['apikey.read'],
  },
  {
    to: '/admin/worker-health',
    label: 'Worker health',
    description: 'Liveness of the AI-worker fleet.',
    permissions: ['worker.read'],
  },
  {
    to: '/admin/watchlist',
    label: 'Watchlist',
    description: 'Flagged plates and the alerts they raise.',
    permissions: ['alert.read'],
  },
  {
    to: '/admin/boundaries',
    label: 'Geographic boundaries',
    description: 'Load surveyed boundary polygons for coverage-gap analysis.',
    permissions: ['geography.manage'],
  },
];

export function AdminPage() {
  const { session } = useAuth();
  const location = useLocation();
  const availableDestinations = adminDestinations.filter((destination) => (
    destination.permissions.some((permission) => hasPermission(session, permission))
  ));
  const isLanding = location.pathname.replace(/\/$/, '') === '/admin';
  // Only set a title when landing directly on /admin — an active sub-route owns its own title,
  // and this effect running after the child's (parent effects fire after children) would clobber it.
  useEffect(() => { if (isLanding) document.title = 'Administration · Trinetra Registry'; }, [isLanding]);

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
