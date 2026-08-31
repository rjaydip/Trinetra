import { NavLink, Outlet } from 'react-router-dom';

const navigation = [
  { to: '/dashboard', label: 'Dashboard' },
  { to: '/cameras', label: 'Camera registry' },
  { to: '/reports', label: 'Reports' },
];

export function AppShell() {
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
          </ul>
        </nav>
      </header>
      <main className="app-content">
        <Outlet />
      </main>
    </div>
  );
}
