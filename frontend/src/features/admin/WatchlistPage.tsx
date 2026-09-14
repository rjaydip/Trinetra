import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { type FormEvent, useState } from 'react';

import { errorDetail } from '../../api/client';
import { api } from '../../api/endpoints';
import type { CreateWatchlistEntryRequest } from '../../api/models';
import { queryKeys } from '../../api/queryKeys';
import { useAuth } from '../../auth/AuthProvider';
import { hasPermission } from '../../auth/permissions';
import { Button, Pager, PageState, StatusBadge } from '../../components/ui';
import { useDocumentTitle } from '../../lib/useDocumentTitle';
import { OrganizationUnitFilter } from '../cameras/OrganizationUnitFilter';
import './admin.css';

function field(form: FormData, name: string) {
  return String(form.get(name) ?? '').trim();
}

function EntriesSection({ canManage }: { canManage: boolean }) {
  const queryClient = useQueryClient();
  const [page, setPage] = useState(1);
  const pageSize = 20;

  const [organizationUnitId, setOrganizationUnitId] = useState('');

  const entries = useQuery({
    queryKey: queryKeys.watchlist.entriesPage(true, page, pageSize),
    queryFn: ({ signal }) => api.admin.watchlist.listPage({ active: true, page, pageSize }, signal),
  });

  const create = useMutation({
    mutationFn: (body: CreateWatchlistEntryRequest) => api.admin.watchlist.create(body),
    onSuccess: () => {
      // Adding a plate backfills alerts for any prior sighting already on record, in the same
      // transaction as creating the entry — the alerts list needs invalidating too, not just
      // the entries list, or a newly-backfilled alert stays invisible until something else
      // happens to refetch it.
      queryClient.invalidateQueries({ queryKey: queryKeys.watchlist.allEntries });
      queryClient.invalidateQueries({ queryKey: queryKeys.watchlist.allAlerts });
    },
  });

  const deactivate = useMutation({
    mutationFn: (id: string) => api.admin.watchlist.deactivate(id),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: queryKeys.watchlist.allEntries }),
  });

  function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const form = event.currentTarget;
    const data = new FormData(form);
    const reason = field(data, 'reason');
    create.mutate({
      organizationUnitId,
      plateNumber: field(data, 'plateNumber'),
      severity: field(data, 'severity'),
      ...(reason ? { reason } : {}),
    });
    form.reset();
    setOrganizationUnitId('');
  }

  return <section aria-labelledby="watchlist-entries-title">
    <h3 id="watchlist-entries-title">Watchlist entries</h3>
    {entries.isPending ? <PageState title="Loading watchlist entries">Retrieving active entries…</PageState>
      : entries.isError ? <><PageState title="Couldn&apos;t load watchlist entries">{errorDetail(entries.error, 'Watchlist entries could not be loaded.')}</PageState><button className="button" type="button" onClick={() => entries.refetch()}>Try again</button></>
        : entries.data.items.length === 0 ? <p className="admin-empty">No active watchlist entries.</p>
          : <>
            <ul className="admin-record-list">{entries.data.items.map((entry) => <li key={entry.id}>
              <div><strong>{entry.plateNumberNormalized}</strong><span>{entry.reason ?? 'No reason recorded'}</span></div>
              <StatusBadge tone={entry.severity === 'Critical' || entry.severity === 'High' ? 'danger' : 'warning'}>{entry.severity}</StatusBadge>
              {canManage && <button className="admin-action-link" type="button" disabled={deactivate.isPending} onClick={() => deactivate.mutate(entry.id)}>Remove</button>}
            </li>)}</ul>
            <Pager page={entries.data.page} pageSize={entries.data.pageSize} total={entries.data.total} onPageChange={setPage} />
          </>}
    {deactivate.isError && <p className="form-error" role="alert">{errorDetail(deactivate.error, 'The watchlist entry could not be removed.')}</p>}
    {canManage && <form aria-label="Add watchlist entry" className="admin-form" onSubmit={submit}>
      <h4>Add a plate to the watchlist</h4>
      <OrganizationUnitFilter organizationUnitId={organizationUnitId || undefined} onChange={(id) => setOrganizationUnitId(id ?? '')} />
      <label>Plate number<input name="plateNumber" required /></label>
      <label>Severity<select name="severity" required defaultValue="Medium">
        <option value="Low">Low</option>
        <option value="Medium">Medium</option>
        <option value="High">High</option>
        <option value="Critical">Critical</option>
      </select></label>
      <label>Reason<textarea name="reason" /></label>
      {create.isError && <p className="form-error" role="alert">{errorDetail(create.error, 'The watchlist entry could not be created.')}</p>}
      {create.isSuccess && (
        create.data.historicalAlertsRaised > 0
          ? <p role="status">
            This plate was already seen {create.data.historicalAlertsRaised}
            {create.data.historicalMatchesCapped ? '+' : ''} time{create.data.historicalAlertsRaised === 1 ? '' : 's'} before —
            {' '}alert{create.data.historicalAlertsRaised === 1 ? '' : 's'} raised for {create.data.historicalMatchesCapped ? 'the most recent 1000 sightings' : 'each sighting'}.
          </p>
          : <p role="status">Added — no prior sightings of this plate on record.</p>
      )}
      <Button disabled={create.isPending} type="submit">Add entry</Button>
    </form>}
  </section>;
}

function AlertsSection({ canAcknowledge }: { canAcknowledge: boolean }) {
  const queryClient = useQueryClient();
  const [page, setPage] = useState(1);
  const pageSize = 20;

  const alerts = useQuery({
    queryKey: queryKeys.watchlist.alertsPage(false, page, pageSize),
    queryFn: ({ signal }) => api.admin.watchlist.listAlertsPage({ acknowledged: false, page, pageSize }, signal),
  });

  const acknowledge = useMutation({
    mutationFn: (id: string) => api.admin.watchlist.acknowledgeAlert(id),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: queryKeys.watchlist.allAlerts }),
  });

  return <section aria-labelledby="watchlist-alerts-title">
    <h3 id="watchlist-alerts-title">Unacknowledged alerts</h3>
    {alerts.isPending ? <PageState title="Loading alerts">Retrieving raised alerts…</PageState>
      : alerts.isError ? <><PageState title="Couldn&apos;t load alerts">{errorDetail(alerts.error, 'Watchlist alerts could not be loaded.')}</PageState><button className="button" type="button" onClick={() => alerts.refetch()}>Try again</button></>
        : alerts.data.items.length === 0 ? <p className="admin-empty">No unacknowledged alerts.</p>
          : <>
            <ul className="admin-record-list">{alerts.data.items.map((alert) => <li key={alert.id}>
              <div><strong>{alert.plateNumberNormalized}</strong><span>Raised {new Date(alert.raisedAt).toLocaleString()}</span><span>{alert.reason ?? 'No reason recorded'}</span></div>
              <StatusBadge tone={alert.severity === 'Critical' || alert.severity === 'High' ? 'danger' : 'warning'}>{alert.severity}</StatusBadge>
              {canAcknowledge && <button className="admin-action-link" type="button" disabled={acknowledge.isPending} onClick={() => acknowledge.mutate(alert.id)}>Acknowledge</button>}
            </li>)}</ul>
            <Pager page={alerts.data.page} pageSize={alerts.data.pageSize} total={alerts.data.total} onPageChange={setPage} />
          </>}
    {acknowledge.isError && <p className="form-error" role="alert">{errorDetail(acknowledge.error, 'The alert could not be acknowledged.')}</p>}
  </section>;
}

export function WatchlistPage() {
  useDocumentTitle('Watchlist');
  const { session } = useAuth();
  const canManage = hasPermission(session, 'watchlist.manage');
  const canAcknowledge = hasPermission(session, 'alert.acknowledge');

  return <section className="admin-workspace watchlist-page" aria-labelledby="watchlist-title">
    <header><div><p className="eyebrow">Plate alerts</p><h2 id="watchlist-title">Watchlist</h2></div><p>Flagged plates and the alerts raised when a fresh detection matches one.</p></header>
    <EntriesSection canManage={canManage} />
    <AlertsSection canAcknowledge={canAcknowledge} />
  </section>;
}
