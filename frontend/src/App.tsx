import { Navigate, Route, Routes } from 'react-router-dom';

import { LoginPage } from './auth/LoginPage';
import { PasswordPage } from './auth/PasswordPage';
import { RequireAuth } from './auth/RequireAuth';
import { AppShell } from './components/AppShell';

export function App() {
  return (
    <Routes>
      <Route path="/login" element={<LoginPage />} />
      <Route path="/password" element={<RequireAuth passwordChangeOnly><PasswordPage /></RequireAuth>} />
      <Route element={<RequireAuth><AppShell /></RequireAuth>}>
        <Route path="/dashboard" element={<DashboardPage />} />
        <Route path="*" element={<Navigate to="/dashboard" replace />} />
      </Route>
    </Routes>
  );
}

function DashboardPage() {
  return (
    <section aria-labelledby="camera-map-title">
      <h1 id="camera-map-title">Camera map</h1>
      <p>Live map data will appear here when you are signed in.</p>
    </section>
  );
}
