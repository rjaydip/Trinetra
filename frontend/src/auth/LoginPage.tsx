import { useState, type FormEvent } from 'react';
import { useNavigate } from 'react-router-dom';

import { isApiProblem } from '../api/client';
import { Button } from '../components/ui';
import { useAuth } from './AuthProvider';

export function LoginPage() {
  const { login } = useAuth();
  const navigate = useNavigate();
  const configuredUsername = import.meta.env.VITE_USERNAME ?? '';
  const configuredPassword = import.meta.env.VITE_PASSWORD ?? '';
  const [username, setUsername] = useState(() => configuredUsername);
  const [password, setPassword] = useState(() => configuredPassword);
  const [error, setError] = useState<string | null>(null);
  const [isSubmitting, setIsSubmitting] = useState(false);

  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setError(null);
    setIsSubmitting(true);

    try {
      const session = await login({
        username: username.trim() || configuredUsername,
        password: password || configuredPassword,
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
      <section className="auth-card" aria-labelledby="sign-in-title">
        <p className="eyebrow">Trinetra Registry</p>
        <h1 id="sign-in-title">Sign in</h1>
        <p>Use your Trinetra account to access the camera registry.</p>
        <form className="auth-form" onSubmit={submit}>
          <label htmlFor="username">Username</label>
          <input id="username" autoComplete="username" onChange={(event) => setUsername(event.target.value)} required value={username} />
          <label htmlFor="password">Password</label>
          <input id="password" autoComplete="current-password" onChange={(event) => setPassword(event.target.value)} required type="password" value={password} />
          {error && <p className="form-error" role="alert">{error}</p>}
          <Button disabled={isSubmitting} type="submit">{isSubmitting ? 'Signing in…' : 'Sign in'}</Button>
        </form>
      </section>
    </main>
  );
}
