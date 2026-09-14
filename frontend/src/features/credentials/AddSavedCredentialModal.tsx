import { useQueryClient } from '@tanstack/react-query';
import { useState, type FormEvent } from 'react';

import { isApiProblem } from '../../api/client';
import { api } from '../../api/endpoints';
import type { SavedCredentialResponse } from '../../api/models';
import { queryKeys } from '../../api/queryKeys';
import { Modal, PasswordInput } from '../../components/ui';

/**
 * The "Add credential" popup: name, description, username, password. Shared between the
 * standalone credential library page and the inline picker on the camera form, so both stay in
 * sync on what a saved credential actually needs.
 */
export function AddSavedCredentialModal({ onClose, onCreated }: {
  onClose(): void;
  onCreated(entry: SavedCredentialResponse): void;
}) {
  const queryClient = useQueryClient();
  const [name, setName] = useState('');
  const [description, setDescription] = useState('');
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [error, setError] = useState('');
  const [saving, setSaving] = useState(false);

  async function save(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setError('');
    if (!name.trim()) {
      setError('Name is required.');
      return;
    }
    if (!password) {
      setError('Password is required.');
      return;
    }
    setSaving(true);
    try {
      const created = await api.credentialLibrary.create({
        name: name.trim(),
        description: description.trim() || undefined,
        username: username.trim() || undefined,
        password,
      });
      await queryClient.invalidateQueries({ queryKey: queryKeys.credentialLibrary.all });
      onCreated(created);
      onClose();
    } catch (err) {
      setError(isApiProblem(err) ? err.detail : 'Unable to save the credential. Please try again.');
    } finally {
      setSaving(false);
    }
  }

  return <Modal titleId="add-credential-title" onClose={onClose}>
    <h2 id="add-credential-title">Add saved credential</h2>
    <p>Saved once, then reusable from a dropdown when registering any camera. Editing or rotating it later updates every camera that uses it.</p>
    <form onSubmit={save}>
      <label htmlFor="saved-credential-name">Name<span aria-hidden="true"> *</span>
        <input autoFocus id="saved-credential-name" required value={name} onChange={(event) => setName(event.target.value)} />
      </label>
      <label htmlFor="saved-credential-description">Description
        <input id="saved-credential-description" value={description} onChange={(event) => setDescription(event.target.value)} />
      </label>
      <label htmlFor="saved-credential-username">Username
        <input autoComplete="username" id="saved-credential-username" value={username} onChange={(event) => setUsername(event.target.value)} />
      </label>
      <label htmlFor="saved-credential-password">Password<span aria-hidden="true"> *</span>
        <PasswordInput autoComplete="new-password" id="saved-credential-password" required value={password} onChange={(event) => setPassword(event.target.value)} />
      </label>
      {error && <p className="form-error" role="alert">{error}</p>}
      <div className="form-actions">
        <button className="button" disabled={saving} type="submit">{saving ? 'Saving…' : 'Save credential'}</button>
        <button className="button button--secondary" type="button" onClick={onClose}>Cancel</button>
      </div>
    </form>
  </Modal>;
}
