import { useQuery } from '@tanstack/react-query';
import { useEffect, useState } from 'react';
import { Link, useSearchParams } from 'react-router-dom';

import { api } from '../../api/endpoints';
import { PageState } from '../../components/ui';
import { CameraCards } from './CameraCards';
import { CameraFilters, type RegistryFilters } from './CameraFilters';
import { CameraTable } from './CameraTable';

const filterNames: Array<keyof RegistryFilters> = [
  'q', 'cameraType', 'organizationUnitId', 'siteId', 'geographicAreaId',
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
  const registry = useQuery({ queryKey: ['cameras', 'registry', search.toString()], queryFn: () => api.cameras.list(request) });
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

  if (registry.isPending) return <PageState title="Loading camera registry">Retrieving authorized camera records…</PageState>;
  if (registry.isError) return <PageState title="Couldn&apos;t load camera registry">Refresh the page to retrieve current registry records.</PageState>;

  return (
    <section className="registry-page" aria-labelledby="camera-registry-title">
      <header className="registry-page__header"><div><p className="eyebrow">Live registry</p><h1 id="camera-registry-title">Camera registry</h1></div><div className="registry-page__actions"><Link className="button button--secondary" to="/cameras/import">Bulk import</Link><Link className="button" to="/cameras/new">Register camera</Link></div></header>
      <CameraFilters filters={draftFilters} onChange={setFilter} onClear={clearFilters} />
      {registry.data.items.length === 0 ? <PageState title="No cameras found">Change or clear filters to view authorized cameras.</PageState> : <>
        <CameraTable cameras={registry.data.items} />
        <CameraCards cameras={registry.data.items} />
      </>}
      <nav className="registry-pagination" aria-label="Camera registry pagination">
        <span aria-live="polite">{registry.data.nextCursor ? 'More camera records are available.' : 'End of available camera records.'}</span>
        <button className="button" disabled={filtersPending || !registry.data.nextCursor} onClick={nextPage} type="button">Next page</button>
      </nav>
    </section>
  );
}
