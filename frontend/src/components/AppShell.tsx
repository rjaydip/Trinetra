import { useState } from 'react';
import { Outlet } from 'react-router-dom';

import { AppHeader } from './AppHeader';
import { AppSidebar } from './AppSidebar';

export function AppShell() {
  const [drawerOpen, setDrawerOpen] = useState(false);

  return (
    <div className="app-shell">
      <AppSidebar className={drawerOpen ? 'app-sidebar--open' : ''} onNavigate={() => setDrawerOpen(false)} />
      {drawerOpen && <button aria-label="Close navigation" className="app-sidebar__scrim" type="button" onClick={() => setDrawerOpen(false)} />}
      <div className="app-main">
        <AppHeader onMenuToggle={() => setDrawerOpen((open) => !open)} />
        <main className="app-content">
          <Outlet />
        </main>
      </div>
    </div>
  );
}
