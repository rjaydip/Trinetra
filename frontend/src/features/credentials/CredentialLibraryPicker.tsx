import { useQuery } from '@tanstack/react-query';
import { useEffect, useState } from 'react';

import { api } from '../../api/endpoints';
import type { SavedCredentialResponse } from '../../api/models';
import { queryKeys } from '../../api/queryKeys';
import { AddSavedCredentialModal } from './AddSavedCredentialModal';

const MANUAL_ENTRY = '';

/**
 * A "saved credential" dropdown plus an "Add credential" button that opens the library popup —
 * meant to sit next to a camera's own username/password fields. Selecting an entry hands back its
 * `credentialReference`: the camera should be pointed at that same reference (a shared secret,
 * not a copy) rather than having its own username/password typed in.
 */
export function CredentialLibraryPicker({ value, onSelect }: {
  /** The currently selected saved credential's reference, or `''` for "enter manually below". */
  value: string;
  onSelect(reference: string, entry: SavedCredentialResponse | null): void;
}) {
  const [adding, setAdding] = useState(false);
  const saved = useQuery({
    queryKey: queryKeys.credentialLibrary.all,
    queryFn: ({ signal }) => api.credentialLibrary.list(signal),
  });
  const entries = saved.data ?? [];

  function handleChange(reference: string) {
    onSelect(reference, entries.find((entry) => entry.credentialReference === reference) ?? null);
  }

  // A camera being edited seeds `value` from its own existing `credentialReference` (see
  // CameraForm's `initialCredentialReference`) before the library list is known. Once it loads,
  // a reference that isn't actually a shared library entry — a camera with its own self-sealed
  // credential, never created through this picker — must fall back to manual rather than the
  // dropdown looking "selected" with no matching option to show for it.
  useEffect(() => {
    if (!saved.isSuccess || !value) return;
    if (!entries.some((entry) => entry.credentialReference === value)) onSelect(MANUAL_ENTRY, null);
    // eslint-disable-next-line react-hooks/exhaustive-deps -- only the library list arriving should re-run this; `value`/`onSelect` changing (e.g. the operator picking a different entry) must not.
  }, [saved.isSuccess, saved.data]);

  return <div className="credential-library-picker">
    <label htmlFor="saved-credential-select">Saved credential
      <select
        disabled={saved.isPending}
        id="saved-credential-select"
        value={value}
        onChange={(event) => handleChange(event.target.value)}
      >
        <option value={MANUAL_ENTRY}>Enter manually below</option>
        {entries.map((entry) => (
          <option key={entry.id} value={entry.credentialReference}>{entry.name}</option>
        ))}
      </select>
    </label>
    <button className="button button--secondary" type="button" onClick={() => setAdding(true)}>+ Add credential</button>
    {saved.isError && <p className="form-error" role="alert">Saved credentials could not be loaded. You can still enter one manually below.</p>}
    {adding && <AddSavedCredentialModal
      onClose={() => setAdding(false)}
      onCreated={(entry) => handleChange(entry.credentialReference)}
    />}
  </div>;
}
