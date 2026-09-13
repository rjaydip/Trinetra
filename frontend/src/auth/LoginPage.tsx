import { useState, type FormEvent } from 'react';
import { useNavigate } from 'react-router-dom';

import './auth.css';
import { isApiProblem } from '../api/client';
import { Button, PasswordInput } from '../components/ui';
import { devCredentials } from '../config/env';
import { AuthBrandPanel } from './AuthBrandPanel';
import { useAuth } from './AuthProvider';

export function LoginPage() {
  const { login } = useAuth();
  const navigate = useNavigate();
  const [username, setUsername] = useState(() => devCredentials().username);
  const [password, setPassword] = useState(() => devCredentials().password);
  const [error, setError] = useState<string | null>(null);
  const [isSubmitting, setIsSubmitting] = useState(false);

  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setError(null);
    setIsSubmitting(true);

    try {
      const session = await login({
        username: username.trim() || devCredentials().username,
        password: password || devCredentials().password,
      });
      navigate(session.mustChangePassword ? '/password' : '/dashboard', { replace: true });
    } catch (reason) {
      setError(isApiProblem(reason) ? reason.detail : 'Unable to sign in. Please try again.');
    } finally {
      setIsSubmitting(false);
    }
  }

  return (
    <main className="auth-page">
      <AuthBrandPanel />
      <div className="auth-page__form-panel">
        <section className="auth-card" aria-labelledby="sign-in-title">
          <p className="eyebrow">Trinetra Registry</p>
          <h1 id="sign-in-title">Sign in</h1>
          <p>Use your Trinetra account to access the camera registry.</p>
          <form className="auth-form" onSubmit={submit}>
            <label htmlFor="username">Username</label>
            <input
              id="username"
              autoComplete="username"
              aria-describedby={error ? 'sign-in-error' : undefined}
              aria-invalid={error ? true : undefined}
              onChange={(event) => setUsername(event.target.value)}
              required
              value={username}
            />
            <label htmlFor="password">Password</label>
            <PasswordInput
              id="password"
              autoComplete="current-password"
              aria-describedby={error ? 'sign-in-error' : undefined}
              aria-invalid={error ? true : undefined}
              onChange={(event) => setPassword(event.target.value)}
              required
              value={password}
            />
            {error && <p className="form-error" id="sign-in-error" role="alert">{error}</p>}
            <Button aria-busy={isSubmitting} disabled={isSubmitting} type="submit">{isSubmitting ? 'Signing in…' : 'Sign in'}</Button>
          </form>
        </section>
      </div>
    </main>
  );
}
