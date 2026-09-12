import { Navigate, Route, Routes } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { useEffect, useState } from 'react';

import { LoginPage } from './auth/LoginPage';
import { useAuth } from './auth/AuthProvider';
import { PasswordPage } from './auth/PasswordPage';
import { RequireAuth } from './auth/RequireAuth';
import { RequirePermission } from './auth/RequirePermission';
import { AppShell } from './components/AppShell';
import { AccessGroupsPage } from './features/admin/AccessGroupsPage';
import { AdminPage, RequireAnyAdminPermission, supportedAdminReadPermissions } from './features/admin/AdminPage';
import { HierarchyPage } from './features/admin/HierarchyPage';
import { RolesPage } from './features/admin/RolesPage';
import { CameraDetailPage } from './features/cameras/CameraDetailPage';
import { BulkImportPage } from './features/cameras/BulkImportPage';
import { NewCameraPage } from './features/cameras/NewCameraPage';
import { ReconciliationPage } from './features/cameras/ReconciliationPage';
import { RegistryPage } from './features/cameras/RegistryPage';
import { DetectionsPage } from './features/detections/DetectionsPage';
import { EventsPage } from './features/events/EventsPage';
import { MapPage } from './features/map/MapPage';
import { ReportsPage } from './features/reports/ReportsPage';
import { DiscoveryPage } from './features/vms/DiscoveryPage';
import { VmsPage } from './features/vms/VmsPage';

export function App() {
  const { session } = useAuth();
  // A new issued session owns a fresh cache and fresh feature state. The key is
  // never rendered or sent to the server; it is not an authorization decision.
  return <SessionApplication key={session?.token ?? 'signed-out'} />;
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
          </Route>
          <Route path="*" element={<Navigate to="/dashboard" replace />} />
        </Route>
      </Routes>
    </QueryClientProvider>
  );
}
