import { useQuery } from '@tanstack/react-query';
import { useEffect, useState } from 'react';
import { Link, useSearchParams } from 'react-router-dom';

import { api } from '../../api/endpoints';
import { isApiProblem } from '../../api/client';
import { queryKeys } from '../../api/queryKeys';
import { useAuth } from '../../auth/AuthProvider';
import { hasPermission } from '../../auth/permissions';
import { PageState } from '../../components/ui';
import { CameraCards } from './CameraCards';
import { CameraFilters, type RegistryFilters } from './CameraFilters';
import { CameraTable } from './CameraTable';

const filterNames: Array<keyof RegistryFilters> = [
  'q', 'cameraType', 'organizationUnitId', 'geographicAreaId',
  'operationalStatus', 'connectivityStatus', 'maintenanceStatus', 'includeRetired',
];

function filtersFromSearch(search: URLSearchParams): RegistryFilters {
  const filters: RegistryFilters = {};
  filterNames.forEach((name) => {
    const value = search.get(name);
    if (name === 'includeRetired') {
      if (value === 'true') filters.includeRetired = true;
    } else if (value) {
      filters[name] = value;
    }
  });
  return filters;
}

function filterSignature(filters: RegistryFilters) {
  return filterNames.map((name) => `${name}:${filters[name] ?? ''}`).join('|');
}

export function RegistryPage() {
  const { session } = useAuth();
  const [search, setSearch] = useSearchParams();
  const committedFilters = filtersFromSearch(search);
  const [draftFilters, setDraftFilters] = useState<RegistryFilters>(() => committedFilters);
  const committedSignature = filterSignature(committedFilters);
  const draftSignature = filterSignature(draftFilters);

  useEffect(() => {
    if (draftSignature !== committedSignature) setDraftFilters(committedFilters);
  }, [search.toString()]);

  useEffect(() => {
    if (draftSignature === committedSignature) return undefined;
    const updateUrl = window.setTimeout(() => {
      const updated = new URLSearchParams(search);
      filterNames.forEach((name) => updated.delete(name));
      updated.delete('cursor');
      filterNames.forEach((name) => {
        const value = draftFilters[name];
        if (value !== undefined && value !== false) updated.set(name, String(value));
      });
      if (updated.toString() !== search.toString()) setSearch(updated);
    }, 200);
    return () => window.clearTimeout(updateUrl);
  }, [committedSignature, draftFilters, draftSignature, search, setSearch]);

  const request = { ...committedFilters, cursor: search.get('cursor') || undefined, limit: 50 };
  const registry = useQuery({ queryKey: queryKeys.cameras.registry(search.toString()), queryFn: () => api.cameras.list(request) });
  const filtersPending = draftSignature !== committedSignature;

  function setFilter(name: keyof RegistryFilters, value: string | boolean | undefined) {
    setDraftFilters((previous) => ({ ...previous, [name]: value }));
  }

  function clearFilters() {
    setDraftFilters({});
  }

  function nextPage() {
    if (filtersPending || !registry.data?.nextCursor) return;
    const updated = new URLSearchParams(search);
    updated.set('cursor', registry.data.nextCursor);
    setSearch(updated);
  }

  return (
    <section className="registry-page" aria-labelledby="camera-registry-title">
      <header className="registry-page__header"><div><p className="eyebrow">Live registry</p><h1 id="camera-registry-title">Camera registry</h1></div><div className="registry-page__actions">{hasPermission(session, 'camera.import') && <Link className="button button--secondary" to="/cameras/import">Bulk import</Link>}{hasPermission(session, 'camera.create') && <Link className="button" to="/cameras/new">Register camera</Link>}</div></header>
      <CameraFilters filters={draftFilters} onChange={setFilter} onClear={clearFilters} />
      <div aria-label="Registry results" role="region" aria-busy={registry.isFetching}>
      {registry.isPending ? <PageState title="Loading camera registry">Retrieving authorized camera records…</PageState>
        : registry.isError ? <><PageState title="Couldn&apos;t load camera registry">{isApiProblem(registry.error) ? registry.error.detail : 'Change or clear the filters, or try again.'}</PageState><button className="button" type="button" onClick={() => registry.refetch()}>Try again</button></>
        : registry.data.items.length === 0 ? <PageState title="No cameras found">Change or clear filters to view authorized cameras.</PageState> : <>
        <CameraTable cameras={registry.data.items} />
        <CameraCards cameras={registry.data.items} />
      </>}
      </div>
      <nav className="registry-pagination" aria-label="Camera registry pagination">
        <span aria-live="polite">{registry.isSuccess ? registry.data.nextCursor ? 'More camera records are available.' : 'End of available camera records.' : 'Camera results are unavailable.'}</span>
        <button className="button" disabled={filtersPending || registry.isFetching || !registry.isSuccess || !registry.data?.nextCursor} onClick={nextPage} type="button">Next</button>
      </nav>
    </section>
  );
}
