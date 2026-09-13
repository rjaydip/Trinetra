import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';

import { errorDetail } from '../../api/client';
import { api } from '../../api/endpoints';
import { queryKeys } from '../../api/queryKeys';
import { useAuth } from '../../auth/AuthProvider';
import { hasPermission } from '../../auth/permissions';
import { Pager, PageState, StatusBadge } from '../../components/ui';

const STALE_AFTER_MINUTES = 5;

function isStale(lastHeartbeatAt: string) {
  return Date.now() - new Date(lastHeartbeatAt).getTime() > STALE_AFTER_MINUTES * 60 * 1000;
}

export function WorkerHealthPage() {
  const { session } = useAuth();
  const queryClient = useQueryClient();
  const canManage = hasPermission(session, 'worker.manage');
  const [page, setPage] = useState(1);
  const pageSize = 20;

  const workers = useQuery({
    queryKey: queryKeys.workerHealth.page(page, pageSize),
    queryFn: () => api.admin.workerHealth.listPage({ page, pageSize }),
  });

  const retire = useMutation({
    mutationFn: (id: string) => api.admin.workerHealth.retire(id),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: queryKeys.workerHealth.allPages }),
  });

  return <section className="admin-workspace worker-health-page" aria-labelledby="worker-health-title">
    <header><div><p className="eyebrow">Analytics fleet</p><h2 id="worker-health-title">AI worker health</h2></div><p>Every worker that has reported a heartbeat. A stale last-heartbeat is the signal an instance has died or lost connectivity.</p></header>
    <section aria-labelledby="worker-list-title">
      <h3 id="worker-list-title">Workers</h3>
      {workers.isPending ? <PageState title="Loading workers">Retrieving reported workers…</PageState>
        : workers.isError ? <><PageState title="Couldn&apos;t load workers">{errorDetail(workers.error, 'Worker health could not be loaded.')}</PageState><button className="button" type="button" onClick={() => workers.refetch()}>Try again</button></>
          : workers.data.items.length === 0 ? <p className="admin-empty">No AI workers have reported in yet.</p>
            : <>
              <ul className="admin-record-list">{workers.data.items.map((worker) => <li key={worker.id}>
                <div>
                  <strong>{worker.workerId}</strong>
                  <span>{worker.hostname} · Key {worker.apiKeyName}</span>
                  <span>Last heartbeat {new Date(worker.lastHeartbeatAt).toLocaleString()}</span>
                  {worker.clockDriftSeconds !== null && Math.abs(worker.clockDriftSeconds) > 30 && <span>Clock drift {worker.clockDriftSeconds.toFixed(0)}s</span>}
                  {canManage && <button className="admin-action-link" type="button" disabled={retire.isPending} onClick={() => retire.mutate(worker.id)}>Retire</button>}
                </div>
                <StatusBadge tone={isStale(worker.lastHeartbeatAt) ? 'danger' : 'success'}>{isStale(worker.lastHeartbeatAt) ? 'Stale' : 'Live'}</StatusBadge>
              </li>)}</ul>
              <Pager page={workers.data.page} pageSize={workers.data.pageSize} total={workers.data.total} onPageChange={setPage} />
            </>}
    </section>
  </section>;
}
