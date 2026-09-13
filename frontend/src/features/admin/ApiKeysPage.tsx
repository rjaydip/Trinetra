import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { type FormEvent, useState } from 'react';

import { errorDetail } from '../../api/client';
import { api } from '../../api/endpoints';
import type { CreateApiKeyRequest } from '../../api/models';
import { queryKeys } from '../../api/queryKeys';
import { useAuth } from '../../auth/AuthProvider';
import { hasPermission } from '../../auth/permissions';
import { Button, Pager, PageState, StatusBadge } from '../../components/ui';

function field(form: FormData, name: string) {
  return String(form.get(name) ?? '').trim();
}

export function ApiKeysPage() {
  const { session } = useAuth();
  const queryClient = useQueryClient();
  const canManage = hasPermission(session, 'apikey.manage');
  const [page, setPage] = useState(1);
  const pageSize = 20;
  const [createdKey, setCreatedKey] = useState('');

  const keys = useQuery({
    queryKey: queryKeys.apiKeys.page(page, pageSize),
    queryFn: () => api.admin.apiKeys.listPage({ page, pageSize }),
  });

  const groups = useQuery({ queryKey: queryKeys.admin.accessGroups, queryFn: api.admin.groups.list, enabled: canManage });

  const create = useMutation({
    mutationFn: (body: CreateApiKeyRequest) => api.admin.apiKeys.create(body),
    onSuccess: async (created) => {
      setCreatedKey(created.rawKey);
      await queryClient.invalidateQueries({ queryKey: queryKeys.apiKeys.allPages });
    },
  });

  const revoke = useMutation({
    mutationFn: (id: string) => api.admin.apiKeys.revoke(id),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: queryKeys.apiKeys.allPages }),
  });

  function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const form = event.currentTarget;
    const data = new FormData(form);
    const expiresAt = field(data, 'expiresAt');
    setCreatedKey('');
    create.mutate({ displayName: field(data, 'displayName'), groupId: field(data, 'groupId'), ...(expiresAt ? { expiresAt: new Date(expiresAt).toISOString() } : {}) });
    form.reset();
  }

  return <section className="admin-workspace api-keys-page" aria-labelledby="api-keys-title">
    <header><div><p className="eyebrow">Machine access</p><h2 id="api-keys-title">API keys</h2></div><p>Keys act through their access group exactly as a user does. The raw value is shown only once, immediately after creation.</p></header>
    <section aria-labelledby="api-key-list-title">
      <h3 id="api-key-list-title">Provisioned keys</h3>
      {keys.isPending ? <PageState title="Loading API keys">Retrieving authorized keys…</PageState>
        : keys.isError ? <><PageState title="Couldn&apos;t load API keys">{errorDetail(keys.error, 'API keys could not be loaded.')}</PageState><button className="button" type="button" onClick={() => keys.refetch()}>Try again</button></>
          : keys.data.items.length === 0 ? <p className="admin-empty">No API keys are available.</p>
            : <>
              <ul className="admin-record-list">{keys.data.items.map((key) => <li key={key.id}>
                <div><strong>{key.displayName}</strong><span>{key.keyId} · Group {key.groupCode}</span>
                  <span>{key.lastUsedAt ? `Last used ${new Date(key.lastUsedAt).toLocaleString()}` : 'Never used'}</span>
                  {key.expiresAt && <span>Expires {new Date(key.expiresAt).toLocaleDateString()}</span>}
                  {canManage && !key.revokedAt && <button className="admin-action-link" type="button" disabled={revoke.isPending} onClick={() => revoke.mutate(key.id)}>Revoke</button>}
                </div>
                <StatusBadge tone={key.revokedAt ? 'danger' : 'success'}>{key.revokedAt ? 'Revoked' : 'Active'}</StatusBadge>
              </li>)}</ul>
              <Pager page={keys.data.page} pageSize={keys.data.pageSize} total={keys.data.total} onPageChange={setPage} />
            </>}
    </section>
    {canManage && <form aria-label="Provision API key" className="admin-form" onSubmit={submit}>
      <h3>Provision API key</h3>
      <label>Display name<input name="displayName" required /></label>
      <label>Access group<select name="groupId" required>
        <option value="">Select a group</option>
        {(groups.data ?? []).map((group) => <option key={group.id} value={group.id}>{group.name} ({group.code})</option>)}
      </select></label>
      <label>Expires at (optional)<input name="expiresAt" type="datetime-local" /></label>
      {create.isError && <p className="form-error" role="alert">{errorDetail(create.error, 'The API key could not be provisioned.')}</p>}
      <Button disabled={create.isPending} type="submit">Provision key</Button>
    </form>}
    {createdKey && <section aria-labelledby="new-key-title" role="status">
      <h3 id="new-key-title">Key created</h3>
      <p>This value is shown once and cannot be retrieved again. Store it now.</p>
      <code>{createdKey}</code>
    </section>}
  </section>;
}
