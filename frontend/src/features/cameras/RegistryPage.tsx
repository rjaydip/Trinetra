import { useQuery } from '@tanstack/react-query';
import { useEffect, useState } from 'react';
import { Link, useSearchParams } from 'react-router-dom';

import type { CameraResponse } from '../../api/models';
import { api } from '../../api/endpoints';
import { isApiProblem } from '../../api/client';
import { queryKeys } from '../../api/queryKeys';
import { useAuth } from '../../auth/AuthProvider';
import { hasPermission } from '../../auth/permissions';
import { PageState } from '../../components/ui';
import { downloadText } from '../../lib/downloadText';
import { useDocumentTitle } from '../../lib/useDocumentTitle';
import { CameraCards } from './CameraCards';
import { CameraFilters, type RegistryFilters } from './CameraFilters';
import { CameraTable } from './CameraTable';
import { exportCamerasToCsv, loadImportReferenceData } from './import';
import './cameras.css';

/** Hard ceiling on a single export — a safety valve against an unbounded fetch loop at the
 * 80,000-camera production target, not a limit anyone doing a normal filtered export should ever
 * hit. Comfortably above the whole first-phase rollout (100+ cameras) with a lot of headroom. */
const MaxExportRows = 20_000;
const ExportPageSize = 200;

/** Every camera matching `filters` (not just the current page), paging through the registry's
 * own cursor-based `GET /cameras` until it runs out or hits `MaxExportRows`. */
async function fetchAllMatching(filters: RegistryFilters): Promise<CameraResponse[]> {
  const items: CameraResponse[] = [];
  let cursor: string | undefined;
  do {
    const page = await api.cameras.list({ ...filters, cursor, limit: ExportPageSize });
    items.push(...page.items);
    cursor = page.nextCursor ?? undefined;
  } while (cursor && items.length < MaxExportRows);
  return items;
}

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
  useDocumentTitle('Camera registry');
  const { session } = useAuth();
  const [search, setSearch] = useSearchParams();
  const committedFilters = filtersFromSearch(search);
  const [draftFilters, setDraftFilters] = useState<RegistryFilters>(() => committedFilters);
  const committedSignature = filterSignature(committedFilters);
  const draftSignature = filterSignature(draftFilters);
  // Cursor pagination has no "page N" concept the server can jump back to — this stack of prior
  // cursor values is the only way to offer a Previous button without an offset-based backend
  // change (cursor pagination here is scale-motivated: an OFFSET that grows with page depth
  // doesn't hold up at the 80k-camera target). Cleared whenever committed filters change, same
  // as the cursor param itself.
  const [cursorStack, setCursorStack] = useState<string[]>([]);
  const [exporting, setExporting] = useState(false);
  const [exportError, setExportError] = useState<string | null>(null);

  useEffect(() => {
    if (draftSignature !== committedSignature) setDraftFilters(committedFilters);
  }, [search.toString()]);

  useEffect(() => {
    setCursorStack([]);
  }, [committedSignature]);

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
  const registry = useQuery({ queryKey: queryKeys.cameras.registry(search.toString()), queryFn: ({ signal }) => api.cameras.list(request, signal) });
  const filtersPending = draftSignature !== committedSignature;

  function setFilter(name: keyof RegistryFilters, value: string | boolean | undefined) {
    setDraftFilters((previous) => ({ ...previous, [name]: value }));
  }

  function clearFilters() {
    setDraftFilters({});
  }

  /** Exports every camera matching the currently *committed* filters (not just this page) as
   * CSV, in the same organization-unit/geographic-area-by-name shape the CSV bulk importer reads
   * back — RFP "role-based search/filter/export": scoped by whatever this page's own search/
   * filter/permissions already produced, no separate export permission or endpoint needed. */
  async function exportCsv() {
    setExportError(null);
    setExporting(true);
    try {
      const [cameras, reference] = await Promise.all([
        fetchAllMatching(committedFilters),
        loadImportReferenceData(),
      ]);
      if (cameras.length === 0) {
        setExportError('No cameras match the current filters — nothing to export.');
        return;
      }
      downloadText(exportCamerasToCsv(cameras, reference), 'text/csv', 'trinetra-camera-registry-export.csv');
    } catch (reason) {
      setExportError(isApiProblem(reason) ? reason.detail : 'Could not export the camera registry.');
    } finally {
      setExporting(false);
    }
  }

  function nextPage() {
    if (filtersPending || !registry.data?.nextCursor) return;
    setCursorStack((stack) => [...stack, search.get('cursor') ?? '']);
    const updated = new URLSearchParams(search);
    updated.set('cursor', registry.data.nextCursor);
    setSearch(updated);
  }

  function previousPage() {
    if (filtersPending || cursorStack.length === 0) return;
    const priorCursor = cursorStack.at(-1)!;
    setCursorStack((stack) => stack.slice(0, -1));
    const updated = new URLSearchParams(search);
    if (priorCursor) updated.set('cursor', priorCursor);
    else updated.delete('cursor');
    setSearch(updated);
  }

  return (
    <section className="registry-page" aria-labelledby="camera-registry-title">
      <header className="registry-page__header"><div><p className="eyebrow">Live registry</p><h1 id="camera-registry-title">Camera registry</h1></div><div className="registry-page__actions">{hasPermission(session, 'camera.read') && <button className="button button--secondary" disabled={exporting} onClick={() => { void exportCsv(); }} type="button">{exporting ? 'Exporting…' : 'Export CSV'}</button>}{hasPermission(session, 'camera.reconcile') && <Link className="button button--secondary" to="/cameras/reconciliation">Reconcile</Link>}{hasPermission(session, 'camera.import') && <Link className="button button--secondary" to="/cameras/import">Bulk import</Link>}{hasPermission(session, 'camera.create') && <Link className="button" to="/cameras/new">Register camera</Link>}</div></header>
      {exportError && <p className="form-error" role="alert">{exportError}</p>}
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
        <div className="registry-pagination__buttons">
          <button className="button button--secondary" disabled={filtersPending || registry.isFetching || cursorStack.length === 0} onClick={previousPage} type="button">Previous</button>
          <button className="button" disabled={filtersPending || registry.isFetching || !registry.isSuccess || !registry.data?.nextCursor} onClick={nextPage} type="button">Next</button>
        </div>
      </nav>
    </section>
  );
}
