import { useQuery } from '@tanstack/react-query';
import { useMemo, useState } from 'react';
import { Link } from 'react-router-dom';

import { errorDetail } from '../../api/client';
import { api } from '../../api/endpoints';
import type { CorrelationGroupSummary } from '../../api/models';
import { queryKeys } from '../../api/queryKeys';
import { useAuth } from '../../auth/AuthProvider';
import { hasPermission } from '../../auth/permissions';
import { PageState, StatusBadge } from '../../components/ui';
import { useDocumentTitle } from '../../lib/useDocumentTitle';
import '../reports/reports.css';
import './correlation.css';

function confidenceTone(confidence: number): 'success' | 'warning' | 'neutral' {
  if (confidence >= 0.75) return 'success';
  if (confidence >= 0.5) return 'warning';
  return 'neutral';
}

/** Groups a caller might never have expanded stay collapsed — fetched lazily, one at a time, only
 * when a row is opened, rather than eagerly loading every group's member events up front. */
function GroupRow({ group, expanded, onToggle }: {
  group: CorrelationGroupSummary; expanded: boolean; onToggle(): void;
}) {
  const detail = useQuery({
    queryKey: ['correlation', 'group', group.id],
    queryFn: ({ signal }) => api.correlation.get(group.id, signal),
    enabled: expanded,
  });

  return <>
    <tr>
      <td>
        <button className="button button--secondary correlation-groups__toggle" type="button" aria-expanded={expanded} onClick={onToggle}>
          {expanded ? 'Hide' : 'Show'} members
        </button>
      </td>
      <td>{group.ruleCode}</td>
      <td><StatusBadge tone={confidenceTone(group.confidence)}>{Math.round(group.confidence * 100)}% possible match</StatusBadge></td>
      <td>{group.memberCount}</td>
      <td>{new Date(group.firstOccurredAt).toLocaleString()} – {new Date(group.lastOccurredAt).toLocaleString()}</td>
    </tr>
    {expanded && <tr className="correlation-groups__detail-row">
      <td colSpan={5}>
        {detail.isPending ? <p role="status">Loading member events…</p>
          : detail.isError ? <p className="form-error" role="alert">{errorDetail(detail.error, 'Could not load this group’s member events.')}</p>
            : <div className="camera-table-wrap"><table className="camera-table">
              <caption className="sr-only">Member events for correlation group {group.naturalKey}</caption>
              <thead><tr><th>Occurred</th><th>Camera</th><th>VMS</th></tr></thead>
              <tbody>{detail.data!.members.map((member) => <tr key={member.federationEventId}>
                <td>{new Date(member.occurredAt).toLocaleString()}</td>
                <td>{member.cameraId}</td>
                <td>{member.sourceVmsId}</td>
              </tr>)}</tbody>
            </table></div>}
      </td>
    </tr>}
  </>;
}

/**
 * RFP Model 3 "unified workflow and alert dashboard" — ties together the three things that were
 * previously only reachable as separate admin pages with no shared view: cross-camera
 * correlation groups (the actual output of `SqlCorrelationEngine`, v1.18), recent watchlist
 * alerts, and recent raw events. Each section also links out to its full page (Watchlist,
 * Events) for the deeper filter/search/acknowledge workflows this page doesn't try to duplicate
 * — it's a triage surface, not a replacement for those pages.
 *
 * The "Federated cross-VMS analytics" section is the RFP's separate "sample federated analytics
 * report" deliverable: aggregated directly from the same correlation-groups data (no new
 * endpoint), since a correlation group is definitionally a cross-camera, and typically
 * cross-VMS, possible match — the natural federated-analytics unit this platform already
 * computes.
 */
export function CorrelationDashboardPage() {
  useDocumentTitle('Correlation dashboard');
  const { session } = useAuth();
  const canSeeAlerts = hasPermission(session, 'alert.read');
  const canSeeEvents = hasPermission(session, 'event.read');
  const [expandedGroupId, setExpandedGroupId] = useState<string | null>(null);

  const groups = useQuery({
    queryKey: queryKeys.correlationGroups(undefined, undefined),
    queryFn: ({ signal }) => api.correlation.groups({}, signal),
  });

  const alerts = useQuery({
    queryKey: queryKeys.watchlist.allAlerts,
    queryFn: ({ signal }) => api.admin.watchlist.listAlerts({ limit: 8 }, signal),
    enabled: canSeeAlerts,
  });

  const recentEventsFrom = useMemo(() => new Date(Date.now() - 60 * 60 * 1000).toISOString(), []);
  const recentEventsTo = useMemo(() => new Date().toISOString(), []);
  const events = useQuery({
    queryKey: queryKeys.events(recentEventsFrom, recentEventsTo, '', '', '', undefined),
    queryFn: ({ signal }) => api.events.query({ from: recentEventsFrom, to: recentEventsTo, limit: 8 }, signal),
    enabled: canSeeEvents,
  });

  const analytics = useMemo(() => {
    const rows = groups.data ?? [];
    const totalMembers = rows.reduce((sum, g) => sum + g.memberCount, 0);
    const avgConfidence = rows.length === 0 ? null : rows.reduce((sum, g) => sum + g.confidence, 0) / rows.length;
    const byRule = new Map<string, number>();
    rows.forEach((g) => byRule.set(g.ruleCode, (byRule.get(g.ruleCode) ?? 0) + 1));
    return { totalGroups: rows.length, totalMembers, avgConfidence, byRule: [...byRule.entries()] };
  }, [groups.data]);

  return <section className="correlation-dashboard" aria-labelledby="correlation-dashboard-title">
    <header>
      <p className="eyebrow">Model 3 · Cross-system workflow</p>
      <h1 id="correlation-dashboard-title">Correlation &amp; alert dashboard</h1>
      <p>Cross-camera possible matches, recent watchlist alerts, and recent events in one place. Every confidence value here is a possible match, never certainty.</p>
    </header>

    <section className="report-panel" aria-labelledby="federated-analytics-title">
      <h2 id="federated-analytics-title">Federated cross-VMS analytics</h2>
      <p>Aggregated from the same cross-camera correlation groups below — the platform's federated analytics unit, since a group is by definition a possible match across distinct cameras (and typically distinct VMS instances).</p>
      {groups.isPending ? <p role="status">Loading…</p>
        : groups.isError ? <p className="form-error" role="alert">{errorDetail(groups.error, 'Could not load correlation analytics.')}</p>
          : <dl className="report-metrics">
            <div><dt>Correlation groups (last 24h)</dt><dd>{analytics.totalGroups}</dd></div>
            <div><dt>Correlated member events</dt><dd>{analytics.totalMembers}</dd></div>
            <div><dt>Average possible-match confidence</dt><dd>{analytics.avgConfidence === null ? 'Not available' : `${Math.round(analytics.avgConfidence * 100)}%`}</dd></div>
            <div><dt>Correlation rules matched</dt><dd>{analytics.byRule.length}</dd></div>
          </dl>}
      {analytics.byRule.length > 0 && <div className="camera-table-wrap"><table className="camera-table">
        <caption className="sr-only">Correlation groups by rule</caption>
        <thead><tr><th>Rule</th><th>Groups</th></tr></thead>
        <tbody>{analytics.byRule.map(([rule, count]) => <tr key={rule}><td>{rule}</td><td>{count}</td></tr>)}</tbody>
      </table></div>}
    </section>

    <section className="report-panel" aria-labelledby="correlation-groups-title">
      <h2 id="correlation-groups-title">Cross-camera correlation groups</h2>
      {groups.isPending ? <PageState title="Loading correlation groups">Retrieving groups from the last 24 hours…</PageState>
        : groups.isError ? <><PageState title="Couldn&apos;t load correlation groups">{errorDetail(groups.error, 'Please try again.')}</PageState><button className="button" type="button" onClick={() => groups.refetch()}>Try again</button></>
          : groups.data.length === 0 ? <PageState title="No correlation groups">No cross-camera possible matches in the last 24 hours.</PageState>
            : <div className="camera-table-wrap"><table className="camera-table">
              <caption className="sr-only">Cross-camera correlation groups, newest activity first</caption>
              <thead><tr><th scope="col"></th><th scope="col">Rule</th><th scope="col">Confidence</th><th scope="col">Members</th><th scope="col">Activity window</th></tr></thead>
              <tbody>{groups.data.map((group) => <GroupRow
                key={group.id} group={group}
                expanded={expandedGroupId === group.id}
                onToggle={() => setExpandedGroupId((current) => current === group.id ? null : group.id)}
              />)}</tbody>
            </table></div>}
    </section>

    {canSeeAlerts && <section className="report-panel" aria-labelledby="dashboard-alerts-title">
      <div className="report-panel__header"><h2 id="dashboard-alerts-title">Recent watchlist alerts</h2><Link className="button button--secondary" to="/admin/watchlist">Open Watchlist</Link></div>
      {alerts.isPending ? <p role="status">Loading…</p>
        : alerts.isError ? <p className="form-error" role="alert">{errorDetail(alerts.error, 'Could not load recent alerts.')}</p>
          : alerts.data.length === 0 ? <p className="report-empty">No watchlist alerts raised recently.</p>
            : <ul className="correlation-dashboard__feed">{alerts.data.map((alert) => <li key={alert.id}>
              <StatusBadge tone={alert.severity === 'High' ? 'danger' : alert.severity === 'Medium' ? 'warning' : 'neutral'}>{alert.severity}</StatusBadge>
              <span>Plate <strong>{alert.plateNumberNormalized}</strong> — {new Date(alert.raisedAt).toLocaleString()}{alert.acknowledgedAt ? ' · Acknowledged' : ''}</span>
            </li>)}</ul>}
    </section>}

    {canSeeEvents && <section className="report-panel" aria-labelledby="dashboard-events-title">
      <div className="report-panel__header"><h2 id="dashboard-events-title">Recent events</h2><Link className="button button--secondary" to="/events">Open Events</Link></div>
      {events.isPending ? <p role="status">Loading…</p>
        : events.isError ? <p className="form-error" role="alert">{errorDetail(events.error, 'Could not load recent events.')}</p>
          : events.data.events.length === 0 ? <p className="report-empty">No events in the last hour.</p>
            : <ul className="correlation-dashboard__feed">{events.data.events.map((event) => <li key={event.eventId}>
              <StatusBadge tone={event.severity === 'critical' || event.severity === 'high' ? 'danger' : 'neutral'}>{event.eventType}</StatusBadge>
              <span>{event.cameraId} — {new Date(event.occurredAt).toLocaleString()}</span>
            </li>)}</ul>}
    </section>}
  </section>;
}
