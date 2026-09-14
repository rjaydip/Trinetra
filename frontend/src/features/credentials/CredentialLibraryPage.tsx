import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';

import { errorDetail } from '../../api/client';
import { api } from '../../api/endpoints';
import type { SavedCredentialResponse } from '../../api/models';
import { queryKeys } from '../../api/queryKeys';
import { useAuth } from '../../auth/AuthProvider';
import { hasPermission } from '../../auth/permissions';
import { PageState } from '../../components/ui';
import { useDocumentTitle } from '../../lib/useDocumentTitle';
import '../admin/admin.css';
import { AddSavedCredentialModal } from './AddSavedCredentialModal';
import { EditSavedCredentialModal } from './EditSavedCredentialModal';

function formatDate(value: string) {
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? value : new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'short' }).format(date);
}

function usageLabel(count: number) {
  return count === 0 ? 'Not used by any camera' : `Used by ${count} camera${count === 1 ? '' : 's'}`;
}

/**
 * The saved-credential library: reusable named device credentials an operator can point several
 * cameras at (via `CredentialLibraryPicker` on the camera form) instead of retyping a
 * username/password for every one of them. Metadata only, ever — no secret is ever shown here.
 */
export function CredentialLibraryPage() {
  useDocumentTitle('Saved credentials');
  const { session } = useAuth();
  const canManage = hasPermission(session, 'camera.update');
  const queryClient = useQueryClient();
  const [adding, setAdding] = useState(false);
  const [editing, setEditing] = useState<SavedCredentialResponse | null>(null);
  const [confirmingDelete, setConfirmingDelete] = useState<string | null>(null);

  const saved = useQuery({
    queryKey: queryKeys.credentialLibrary.all,
    queryFn: ({ signal }) => api.credentialLibrary.list(signal),
  });

  const remove = useMutation({
    mutationFn: (id: string) => api.credentialLibrary.delete(id),
    onSuccess: () => {
      setConfirmingDelete(null);
      return queryClient.invalidateQueries({ queryKey: queryKeys.credentialLibrary.all });
    },
  });

  return <section className="admin-workspace" aria-labelledby="credential-library-title">
    <header>
      <div><p className="eyebrow">Device access</p><h2 id="credential-library-title">Saved credentials</h2></div>
      <p>Reusable device credentials for registering cameras. Pick one from a dropdown while adding or editing a camera instead of retyping a username and password — rotating or deleting one here changes it for every camera that uses it.</p>
      {canManage && <button className="button" type="button" onClick={() => setAdding(true)}>+ Add credential</button>}
    </header>
    {saved.isPending ? <PageState title="Loading saved credentials">Retrieving the credential library…</PageState>
      : saved.isError ? <><PageState title="Couldn&apos;t load saved credentials">{errorDetail(saved.error, 'The credential library could not be loaded.')}</PageState><button className="button" type="button" onClick={() => saved.refetch()}>Try again</button></>
        : saved.data.length === 0 ? <p className="admin-empty">No saved credentials yet. {canManage ? 'Add one to reuse it across cameras.' : 'Ask an administrator to add one.'}</p>
          : <ul className="admin-record-list">{saved.data.map((entry) => <li key={entry.id}>
            <div>
              <strong>{entry.name}</strong>
              {entry.description && <span>{entry.description}</span>}
              <span>{usageLabel(entry.usageCount)}</span>
              <span>Added {formatDate(entry.createdAt)}</span>
              {canManage && <>
                <button className="admin-action-link" type="button" onClick={() => setEditing(entry)}>Edit</button>
                {confirmingDelete === entry.id ? <>
                  <button className="admin-action-link" disabled={remove.isPending} type="button" onClick={() => remove.mutate(entry.id)}>
                    {remove.isPending ? 'Deleting…' : 'Confirm delete'}
                  </button>
                  <button className="admin-action-link" type="button" onClick={() => setConfirmingDelete(null)}>Cancel</button>
                </> : <button className="admin-action-link" type="button" onClick={() => setConfirmingDelete(entry.id)}>Delete</button>}
                {remove.isError && remove.variables === entry.id && <span className="form-error" role="alert">{errorDetail(remove.error, 'The credential could not be deleted.')}</span>}
              </>}
            </div>
          </li>)}</ul>}
    {adding && <AddSavedCredentialModal onClose={() => setAdding(false)} onCreated={() => {}} />}
    {editing && <EditSavedCredentialModal entry={editing} onClose={() => setEditing(null)} onUpdated={() => {}} />}
  </section>;
}
