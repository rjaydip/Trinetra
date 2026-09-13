import { useQuery } from '@tanstack/react-query';
import { useState, type FormEvent } from 'react';
import { Link } from 'react-router-dom';
import { isApiProblem } from '../../api/client';
import { api } from '../../api/endpoints';
import { queryKeys } from '../../api/queryKeys';
import { StatusBadge } from '../../components/ui';
import './map.css';

/** The 3-second "is my fleet healthy" read — the first thing an operator sees after login. */
export function FleetStatusStrip() {
  const overview = useQuery({ queryKey: queryKeys.overview, queryFn: api.overview });

  if (overview.isPending) return <section aria-labelledby="dashboard-fleet-heading"><h2 id="dashboard-fleet-heading">Fleet status</h2><p role="status">Loading fleet summary…</p></section>;
  if (overview.isError) {
    return <section aria-labelledby="dashboard-fleet-heading">
      <h2 id="dashboard-fleet-heading">Fleet status</h2>
      <div role="status">
        <p>{isApiProblem(overview.error) && overview.error.status === 403 ? 'Fleet summary is unavailable for your permissions.' : 'Could not load fleet summary.'}</p>
        <button className="button" type="button" onClick={() => overview.refetch()}>Retry fleet summary</button>
      </div>
    </section>;
  }

  const { cameras, unreachableCameras, targets, activeTargets, quarantinedTargets } = overview.data;

  return <section aria-labelledby="dashboard-fleet-heading">
    <h2 id="dashboard-fleet-heading">Fleet status</h2>
    <dl className="fleet-status-strip">
      <div className="fleet-status-strip__stat">
        <dt>Total cameras</dt>
        <dd>{cameras}</dd>
      </div>
      <div className="fleet-status-strip__stat">
        <dt>Camera connectivity</dt>
        <dd>
          {cameras - unreachableCameras} online
          {unreachableCameras > 0 && <StatusBadge tone="danger">{unreachableCameras} unreachable</StatusBadge>}
        </dd>
      </div>
      <div className="fleet-status-strip__stat">
        <dt>Quarantined targets</dt>
        <dd>{quarantinedTargets > 0 ? <StatusBadge tone="warning">{quarantinedTargets}</StatusBadge> : quarantinedTargets}</dd>
      </div>
      <div className="fleet-status-strip__stat">
        <dt>VMS targets connected</dt>
        <dd>{activeTargets} / {targets}</dd>
      </div>
    </dl>
  </section>;
}

export function DashboardSearch() {
  const [draft, setDraft] = useState('');
  const [query, setQuery] = useState('');
  const results = useQuery({ queryKey: queryKeys.cameras.dashboardSearch(query), queryFn: () => api.cameras.list({ q: query, limit: 10 }), enabled: Boolean(query) });
  function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const next = draft.trim();
    if (next === query && next) void results.refetch();
    setQuery(next);
  }

  return <section className="dashboard-search" aria-labelledby="dashboard-search-heading">
    <h2 id="dashboard-search-heading">Registry search</h2>
    <form className="dashboard-search__form" onSubmit={submit} role="search">
      <label>Search registry<input type="search" value={draft} onChange={(event) => setDraft(event.target.value)} aria-describedby="dashboard-search-help" /></label>
      <p id="dashboard-search-help">Search does not filter the map. It finds authorized registry records across locations.</p>
      <button className="button" type="submit">Search cameras</button>
    </form>
    {query && <div aria-live="polite" aria-busy={results.isFetching}>
      {results.isPending ? <p>Searching registry…</p>
        : results.isError ? <div role="alert"><p>{isApiProblem(results.error) ? results.error.detail : 'Could not search the registry.'}</p><button className="button" type="button" onClick={() => results.refetch()}>Retry search</button></div>
          : <>
            {results.data.items.length ? <ul>{results.data.items.map((camera) => <li key={camera.id}><Link to={`/cameras/${camera.id}`}>{camera.name}</Link> · {camera.cameraCode}</li>)}</ul> : <p>No registry cameras match this search.</p>}
            <Link to={`/cameras?${new URLSearchParams({ q: query })}`}>View registry search results</Link>
          </>}
    </div>}
  </section>;
}
