import type { ReactNode } from 'react';
import { NavLink } from 'react-router-dom';

import { useAuth } from '../auth/AuthProvider';
import { hasPermission } from '../auth/permissions';
import { supportedAdminReadPermissions } from '../features/admin/AdminPage';
import { AppLogo } from './AppLogo';
import { UserInfo } from './UserInfo';

/** Small stroke-only icon set, inline so the rail has no icon-library dependency. Each is purely
 * decorative — the link's own text is the accessible name — so every icon carries aria-hidden.
 * Shared stroke settings (round caps/joins, slightly thinner weight) live in one place so every
 * icon reads consistently at the rail's small display size instead of looking hand-varied. */
function Icon({ children }: { children: ReactNode }) {
  return <svg aria-hidden="true" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.6" strokeLinecap="round" strokeLinejoin="round">{children}</svg>;
}
function DashboardIcon() {
  return <Icon><rect x="3.5" y="3.5" width="7.5" height="7.5" rx="1.5" /><rect x="13" y="3.5" width="7.5" height="5" rx="1.5" /><rect x="13" y="10.5" width="7.5" height="10" rx="1.5" /><rect x="3.5" y="14.5" width="7.5" height="6" rx="1.5" /></Icon>;
}
function CameraIcon() {
  return <Icon><rect x="2.5" y="6.5" width="14" height="11" rx="2" /><path d="M16.5 10.5 21 8v8l-4.5-2.5" /></Icon>;
}
function VmsIcon() {
  return <Icon><rect x="3" y="4.5" width="18" height="6" rx="1.5" /><rect x="3" y="13.5" width="18" height="6" rx="1.5" /><path d="M7 7.5h.01M7 16.5h.01" /></Icon>;
}
function VideoWallIcon() {
  return <Icon><rect x="3" y="4" width="8" height="8" rx="1.25" /><rect x="13" y="4" width="8" height="8" rx="1.25" /><rect x="3" y="14" width="8" height="6" rx="1.25" /><rect x="13" y="14" width="8" height="6" rx="1.25" /></Icon>;
}
function ReportsIcon() {
  return <Icon><path d="M4 20V11M12 20V4M20 20v-6" /></Icon>;
}
function DetectionsIcon() {
  return <Icon><circle cx="10.5" cy="10.5" r="6.5" /><path d="m20 20-4.3-4.3" /></Icon>;
}
function EventsIcon() {
  return <Icon><path d="M18 8a6 6 0 1 0-12 0c0 5-2 6-2 6h16s-2-1-2-6" /><path d="M13.7 20a2 2 0 0 1-3.4 0" /></Icon>;
}
function CorrelationIcon() {
  return <Icon><circle cx="6" cy="6.5" r="2.5" /><circle cx="18" cy="6.5" r="2.5" /><circle cx="12" cy="17.5" r="2.5" /><path d="m8 8 3 7.5M16 8l-3 7.5" /></Icon>;
}
function AdminIcon() {
  return <Icon><path d="M12 3 5 6v5c0 4.5 3 7.5 7 9 4-1.5 7-4.5 7-9V6z" /></Icon>;
}
function CredentialsIcon() {
  return <Icon><circle cx="8" cy="15.5" r="3.5" /><path d="m10.5 13 8-8M15 5.5l2.5 2.5M18 8.5 20 6.5" /></Icon>;
}

interface NavItem {
  to: string;
  label: string;
  icon: ReactNode;
  permission?: string;
}

const primaryNavigation: NavItem[] = [
  { to: '/dashboard', label: 'Dashboard', icon: <DashboardIcon /> },
  { to: '/cameras', label: 'Camera registry', icon: <CameraIcon /> },
  { to: '/video-wall', label: 'Video wall', icon: <VideoWallIcon /> },
  { to: '/vms', label: 'VMS integrations', icon: <VmsIcon />, permission: 'vms.read' },
  { to: '/credentials', label: 'Credentials', icon: <CredentialsIcon />, permission: 'camera.read' },
  { to: '/reports', label: 'Reports', icon: <ReportsIcon /> },
  { to: '/detections', label: 'Detections', icon: <DetectionsIcon />, permission: 'observation.read' },
  { to: '/events', label: 'Events', icon: <EventsIcon />, permission: 'event.read' },
  { to: '/correlation', label: 'Correlation', icon: <CorrelationIcon />, permission: 'correlation.read' },
];

export function AppSidebar({ className = '', onNavigate }: { className?: string; onNavigate?(): void }) {
  const { session } = useAuth();
  const showAdmin = supportedAdminReadPermissions.some((permission) => hasPermission(session, permission));
  const items = primaryNavigation.filter((item) => !item.permission || hasPermission(session, item.permission));

  return (
    <nav aria-label="Primary navigation" className={`app-sidebar ${className}`.trim()}>
      <a className="app-sidebar__brand" href="/dashboard">
        <span aria-hidden="true" className="app-sidebar__brand-mark"><AppLogo /></span>
        <span className="app-sidebar__brand-name">Trinetra Registry</span>
      </a>
      <ul className="app-sidebar__list">
        {items.map((item) => (
          <li key={item.to}>
            <NavLink className="app-sidebar__link" to={item.to} onClick={onNavigate}>
              <span className="app-sidebar__icon">{item.icon}</span>
              <span className="app-sidebar__label">{item.label}</span>
            </NavLink>
          </li>
        ))}
      </ul>
      {showAdmin && <>
        <hr className="app-sidebar__divider" />
        <ul className="app-sidebar__list">
          <li>
            <NavLink className="app-sidebar__link" to="/admin" onClick={onNavigate}>
              <span className="app-sidebar__icon"><AdminIcon /></span>
              <span className="app-sidebar__label">Admin</span>
            </NavLink>
          </li>
        </ul>
      </>}
      <div className="app-sidebar__spacer" />
      <hr className="app-sidebar__divider" />
      <UserInfo />
    </nav>
  );
}
