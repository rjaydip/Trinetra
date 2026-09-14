import { useQueryClient } from '@tanstack/react-query';
import { useState, type FormEvent } from 'react';

import { isApiProblem } from '../../api/client';
import { api } from '../../api/endpoints';
import type { SavedCredentialResponse } from '../../api/models';
import { queryKeys } from '../../api/queryKeys';
import { Modal, PasswordInput } from '../../components/ui';

/**
 * Rotate or rename an existing saved credential. Supplying a password (or leaving it blank)
 * matters: a non-blank password reseals the SAME shared reference every camera pointed at this
 * entry already carries, so this is also the rotation route for all of them at once — leaving it
 * blank only renames/redescribes the entry and touches no secret.
 */
export function EditSavedCredentialModal({ entry, onClose, onUpdated }: {
  entry: SavedCredentialResponse;
  onClose(): void;
  onUpdated(entry: SavedCredentialResponse): void;
}) {
  const queryClient = useQueryClient();
  const [name, setName] = useState(entry.name);
  const [description, setDescription] = useState(entry.description ?? '');
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
    setSaving(true);
    try {
      const updated = await api.credentialLibrary.update(entry.id, {
        name: name.trim(),
        description: description.trim() || undefined,
        username: username.trim() || undefined,
        password: password || undefined,
      });
      await queryClient.invalidateQueries({ queryKey: queryKeys.credentialLibrary.all });
      onUpdated(updated);
      onClose();
    } catch (err) {
      setError(isApiProblem(err) ? err.detail : 'Unable to save the credential. Please try again.');
    } finally {
      setSaving(false);
    }
  }

  return <Modal titleId="edit-credential-title" onClose={onClose}>
    <h2 id="edit-credential-title">Edit saved credential</h2>
    <p>{entry.usageCount > 0
      ? `Rotating the password here updates it for all ${entry.usageCount} camera${entry.usageCount === 1 ? '' : 's'} using this credential — no per-camera change needed.`
      : 'No camera is using this credential yet.'}</p>
    <form onSubmit={save}>
      <label htmlFor="edit-credential-name">Name<span aria-hidden="true"> *</span>
        <input id="edit-credential-name" required value={name} onChange={(event) => setName(event.target.value)} />
      </label>
      <label htmlFor="edit-credential-description">Description
        <input id="edit-credential-description" value={description} onChange={(event) => setDescription(event.target.value)} />
      </label>
      <label htmlFor="edit-credential-username">Username
        <input autoComplete="username" id="edit-credential-username" placeholder="Leave blank to keep unchanged" value={username} onChange={(event) => setUsername(event.target.value)} />
      </label>
      <label htmlFor="edit-credential-password">New password
        <PasswordInput autoComplete="new-password" id="edit-credential-password" value={password} onChange={(event) => setPassword(event.target.value)} />
      </label>
      <p>Leave the password blank to keep the current one and just update the name/description.</p>
      {error && <p className="form-error" role="alert">{error}</p>}
      <div className="form-actions">
        <button className="button" disabled={saving} type="submit">{saving ? 'Saving…' : 'Save changes'}</button>
        <button className="button button--secondary" type="button" onClick={onClose}>Cancel</button>
      </div>
    </form>
  </Modal>;
}
