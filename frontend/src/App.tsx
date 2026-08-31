import { Navigate, Route, Routes } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { useState } from 'react';

import { LoginPage } from './auth/LoginPage';
import { PasswordPage } from './auth/PasswordPage';
import { RequireAuth } from './auth/RequireAuth';
import { AppShell } from './components/AppShell';
import { MapPage } from './features/map/MapPage';

export function App() {
  const [queryClient] = useState(() => new QueryClient({
    defaultOptions: { queries: { retry: false } },
  }));

  return (
    <QueryClientProvider client={queryClient}>
      <Routes>
        <Route path="/login" element={<LoginPage />} />
        <Route path="/password" element={<RequireAuth passwordChangeOnly><PasswordPage /></RequireAuth>} />
        <Route element={<RequireAuth><AppShell /></RequireAuth>}>
          <Route path="/dashboard" element={<MapPage />} />
          <Route path="*" element={<Navigate to="/dashboard" replace />} />
        </Route>
      </Routes>
    </QueryClientProvider>
  );
}
