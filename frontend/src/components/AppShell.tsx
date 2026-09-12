import { useState } from 'react';
import { NavLink, Outlet } from 'react-router-dom';
import { useAuth } from '../auth/AuthProvider';
import { hasPermission } from '../auth/permissions';

const navigation = [
  { to: '/dashboard', label: 'Dashboard' },
  { to: '/cameras', label: 'Camera registry' },
  { to: '/reports', label: 'Reports' },
];

export function AppShell() {
  const { session, logout } = useAuth();
  const [loggingOut, setLoggingOut] = useState(false);
  const showAdmin = ['organization.read', 'geography.read', 'group.read'].some((permission) => hasPermission(session, permission));

  async function handleLogout() {
    setLoggingOut(true);
    try {
      await logout();
    } finally {
      // If logout fails to unmount this component (session somehow survives), don't leave the
      // button stuck disabled forever.
      setLoggingOut(false);
    }
  }

  return (
    <div className="app-shell">
      <header className="app-header">
        <a className="brand" href="/dashboard">Trinetra Registry</a>
        <nav aria-label="Primary navigation">
          <ul className="primary-nav">
            {navigation.map((item) => (
              <li key={item.to}>
                <NavLink to={item.to}>{item.label}</NavLink>
              </li>
            ))}
            {hasPermission(session, 'vms.read') && <li><NavLink to="/vms">VMS</NavLink></li>}
            {hasPermission(session, 'camera.reconcile') && <li><NavLink to="/cameras/reconciliation">Reconcile</NavLink></li>}
            {hasPermission(session, 'observation.read') && <li><NavLink to="/detections">Detections</NavLink></li>}
            {hasPermission(session, 'event.read') && <li><NavLink to="/events">Events</NavLink></li>}
            {showAdmin && <li><NavLink to="/admin">Admin</NavLink></li>}
            <li><button className="button button--secondary" disabled={loggingOut} type="button" onClick={() => { void handleLogout(); }}>Log out</button></li>
          </ul>
        </nav>
      </header>
      <main className="app-content">
        <Outlet />
      </main>
    </div>
  );
}
