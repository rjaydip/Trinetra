import { Navigate, Route, Routes } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { lazy, Suspense, useEffect, useState } from 'react';

import { LoginPage } from './auth/LoginPage';
import { useAuth } from './auth/AuthProvider';
import { PasswordPage } from './auth/PasswordPage';
import { RequireAuth } from './auth/RequireAuth';
import { RequirePermission } from './auth/RequirePermission';
import { AppShell } from './components/AppShell';
import { ToastProvider } from './components/Toast';
import { PageState } from './components/ui';
import { AdminPage, RequireAnyAdminPermission, supportedAdminReadPermissions } from './features/admin/AdminPage';

const AccessGroupsPage = lazy(() => import('./features/admin/AccessGroupsPage').then((m) => ({ default: m.AccessGroupsPage })));
const ApiKeysPage = lazy(() => import('./features/admin/ApiKeysPage').then((m) => ({ default: m.ApiKeysPage })));
const HierarchyPage = lazy(() => import('./features/admin/HierarchyPage').then((m) => ({ default: m.HierarchyPage })));
const RolesPage = lazy(() => import('./features/admin/RolesPage').then((m) => ({ default: m.RolesPage })));
const UsersPage = lazy(() => import('./features/admin/UsersPage').then((m) => ({ default: m.UsersPage })));
const WatchlistPage = lazy(() => import('./features/admin/WatchlistPage').then((m) => ({ default: m.WatchlistPage })));
const WorkerHealthPage = lazy(() => import('./features/admin/WorkerHealthPage').then((m) => ({ default: m.WorkerHealthPage })));
const CameraDetailPage = lazy(() => import('./features/cameras/CameraDetailPage').then((m) => ({ default: m.CameraDetailPage })));
const BulkImportPage = lazy(() => import('./features/cameras/BulkImportPage').then((m) => ({ default: m.BulkImportPage })));
const NewCameraPage = lazy(() => import('./features/cameras/NewCameraPage').then((m) => ({ default: m.NewCameraPage })));
const ReconciliationPage = lazy(() => import('./features/cameras/ReconciliationPage').then((m) => ({ default: m.ReconciliationPage })));
const RegistryPage = lazy(() => import('./features/cameras/RegistryPage').then((m) => ({ default: m.RegistryPage })));
const DetectionsPage = lazy(() => import('./features/detections/DetectionsPage').then((m) => ({ default: m.DetectionsPage })));
const EventsPage = lazy(() => import('./features/events/EventsPage').then((m) => ({ default: m.EventsPage })));
const MapPage = lazy(() => import('./features/map/MapPage').then((m) => ({ default: m.MapPage })));
const ReportsPage = lazy(() => import('./features/reports/ReportsPage').then((m) => ({ default: m.ReportsPage })));
const DiscoveryPage = lazy(() => import('./features/vms/DiscoveryPage').then((m) => ({ default: m.DiscoveryPage })));
const VmsPage = lazy(() => import('./features/vms/VmsPage').then((m) => ({ default: m.VmsPage })));

export function App() {
  const { session } = useAuth();
  // A new issued session owns a fresh cache and fresh feature state. The key is
  // never rendered or sent to the server; it is not an authorization decision.
  // ToastProvider sits outside that remount so a toast fired during the transition
  // (e.g. the session-expiry toast itself) survives it.
  return (
    <ToastProvider>
      <SessionApplication key={session?.token ?? 'signed-out'} />
    </ToastProvider>
  );
}

function SessionApplication() {
  const [queryClient] = useState(() => new QueryClient({
    defaultOptions: { queries: { retry: false } },
  }));

  useEffect(() => () => {
    void queryClient.cancelQueries();
    queryClient.clear();
  }, [queryClient]);

  return (
    <QueryClientProvider client={queryClient}>
      <Suspense fallback={<PageState title="Loading…" />}>
        <Routes>
          <Route path="/login" element={<LoginPage />} />
          <Route path="/password" element={<RequireAuth passwordChangeOnly><PasswordPage /></RequireAuth>} />
          <Route element={<RequireAuth><AppShell /></RequireAuth>}>
            <Route path="/dashboard" element={<MapPage />} />
            <Route path="/reports" element={<ReportsPage />} />
            <Route path="/cameras" element={<RegistryPage />} />
            <Route path="/cameras/new" element={<RequirePermission permission="camera.create"><NewCameraPage /></RequirePermission>} />
            <Route path="/cameras/import" element={<RequirePermission permission="camera.import"><BulkImportPage /></RequirePermission>} />
            <Route path="/cameras/reconciliation" element={<RequirePermission permission="camera.reconcile"><ReconciliationPage /></RequirePermission>} />
            <Route path="/detections" element={<RequirePermission permission="observation.read"><DetectionsPage /></RequirePermission>} />
            <Route path="/events" element={<RequirePermission permission="event.read"><EventsPage /></RequirePermission>} />
            <Route path="/cameras/:cameraId" element={<CameraDetailPage />} />
            <Route path="/vms" element={<RequirePermission permission="vms.read"><VmsPage /></RequirePermission>} />
            <Route path="/vms/:vmsId" element={<RequirePermission permission="vms.read"><VmsPage /></RequirePermission>} />
            <Route path="/vms/:vmsId/discovery" element={<RequirePermission permission="vms.read"><RequirePermission permission="camera.import"><DiscoveryPage /></RequirePermission></RequirePermission>} />
            <Route path="/admin" element={<RequireAnyAdminPermission permissions={supportedAdminReadPermissions}><AdminPage /></RequireAnyAdminPermission>}>
              <Route path="hierarchy" element={<RequireAnyAdminPermission permissions={['organization.read', 'geography.read']}><HierarchyPage /></RequireAnyAdminPermission>} />
              <Route path="roles" element={<RequirePermission permission="group.read"><RolesPage /></RequirePermission>} />
              <Route path="access-groups" element={<RequirePermission permission="group.read"><AccessGroupsPage /></RequirePermission>} />
              <Route path="users" element={<RequirePermission permission="user.read"><UsersPage /></RequirePermission>} />
              <Route path="api-keys" element={<RequirePermission permission="apikey.read"><ApiKeysPage /></RequirePermission>} />
              <Route path="worker-health" element={<RequirePermission permission="worker.read"><WorkerHealthPage /></RequirePermission>} />
              <Route path="watchlist" element={<RequirePermission permission="alert.read"><WatchlistPage /></RequirePermission>} />
            </Route>
            <Route path="*" element={<Navigate to="/dashboard" replace />} />
          </Route>
        </Routes>
      </Suspense>
    </QueryClientProvider>
  );
}
