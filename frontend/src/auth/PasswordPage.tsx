import { useState, type FormEvent } from 'react';
import { useNavigate } from 'react-router-dom';

import './auth.css';
import { isApiProblem } from '../api/client';
import { Button, PasswordInput } from '../components/ui';
import { AuthBrandPanel } from './AuthBrandPanel';
import { useAuth } from './AuthProvider';

export function PasswordPage() {
  const { changePassword } = useAuth();
  const navigate = useNavigate();
  const [currentPassword, setCurrentPassword] = useState('');
  const [newPassword, setNewPassword] = useState('');
  const [confirmPassword, setConfirmPassword] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [isSubmitting, setIsSubmitting] = useState(false);

  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setError(null);

    if (newPassword === currentPassword) {
      setError('Your new password must be different from your current password.');
      return;
    }
    if (newPassword !== confirmPassword) {
      setError('New password and confirmation must match.');
      return;
    }
    setIsSubmitting(true);

    try {
      await changePassword({ currentPassword, newPassword });
      navigate('/dashboard', { replace: true });
    } catch (reason) {
      setError(isApiProblem(reason) ? reason.detail : 'Unable to change the password. Please try again.');
    } finally {
      setIsSubmitting(false);
    }
  }

  return (
    <main className="auth-page">
      <AuthBrandPanel />
      <div className="auth-page__form-panel">
        <section className="auth-card" aria-labelledby="change-password-title">
          <p className="eyebrow">Trinetra Registry</p>
          <h1 id="change-password-title">Change password</h1>
          <p>You must set a new password before continuing.</p>
          <form className="auth-form" onSubmit={submit}>
            <label htmlFor="current-password">Current password</label>
            <PasswordInput
              aria-describedby={error ? 'password-change-error' : undefined}
              aria-invalid={error ? true : undefined}
              id="current-password"
              autoComplete="current-password"
              onChange={(event) => setCurrentPassword(event.target.value)}
              required
              value={currentPassword}
            />
            <label htmlFor="new-password">New password</label>
            <p className="field-hint" id="new-password-hint">Use at least 12 characters.</p>
            <PasswordInput
              aria-describedby={error ? 'password-change-error new-password-hint' : 'new-password-hint'}
              aria-invalid={error ? true : undefined}
              id="new-password"
              autoComplete="new-password"
              onChange={(event) => setNewPassword(event.target.value)}
              required
              value={newPassword}
            />
            <label htmlFor="confirm-password">Confirm new password</label>
            <PasswordInput
              aria-describedby={error ? 'password-change-error' : undefined}
              aria-invalid={error ? true : undefined}
              id="confirm-password"
              autoComplete="new-password"
              onChange={(event) => setConfirmPassword(event.target.value)}
              required
              value={confirmPassword}
            />
            {error && <p className="form-error" id="password-change-error" role="alert">{error}</p>}
            <Button aria-busy={isSubmitting} disabled={isSubmitting} type="submit">{isSubmitting ? 'Changing password…' : 'Change password'}</Button>
          </form>
        </section>
      </div>
    </main>
  );
}
