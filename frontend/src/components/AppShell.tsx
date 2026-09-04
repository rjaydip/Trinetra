import { NavLink, Outlet } from 'react-router-dom';
import { useAuth } from '../auth/AuthProvider';
import { hasPermission } from '../auth/permissions';

const navigation = [
  { to: '/dashboard', label: 'Dashboard' },
  { to: '/cameras', label: 'Camera registry' },
  { to: '/reports', label: 'Reports' },
];

export function AppShell() {
  const { session } = useAuth();
  const showAdmin = ['organization.read', 'geography.read', 'group.read'].some((permission) => hasPermission(session, permission));
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
            {showAdmin && <li><NavLink to="/admin">Admin</NavLink></li>}
          </ul>
        </nav>
      </header>
      <main className="app-content">
        <Outlet />
      </main>
    </div>
  );
}
