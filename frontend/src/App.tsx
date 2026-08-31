import { Navigate, Route, Routes } from 'react-router-dom';

import { AppShell } from './components/AppShell';

export function App() {
  return (
    <Routes>
      <Route path="/login" element={<SignInPage />} />
      <Route element={<AppShell />}>
        <Route path="/dashboard" element={<DashboardPage />} />
        <Route path="*" element={<Navigate to="/dashboard" replace />} />
      </Route>
    </Routes>
  );
}

function SignInPage() {
  return (
    <main className="auth-page">
      <section className="auth-card" aria-labelledby="sign-in-title">
        <p className="eyebrow">Trinetra Registry</p>
        <h1 id="sign-in-title">Sign in</h1>
        <p>Use your Trinetra account to access the camera registry.</p>
      </section>
    </main>
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
