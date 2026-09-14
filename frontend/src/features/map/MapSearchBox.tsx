import { useQuery } from '@tanstack/react-query';
import { useState, type FormEvent } from 'react';

import { isApiProblem } from '../../api/client';
import { api } from '../../api/endpoints';
import type { CameraResponse } from '../../api/models';
import { queryKeys } from '../../api/queryKeys';
import { initialBoundsFromCameras, type Bounds } from './geo';

/**
 * Search box for the map itself — distinct from `DashboardSearch`, which deliberately does not
 * touch the map (its own help text says so; it's a plain registry lookup). Picking a result here
 * pans/zooms the map to that camera and opens its detail drawer, the way clicking a marker does.
 */
export function MapSearchBox({ onLocate }: { onLocate(bounds: Bounds, cameraId: string): void }) {
  const [draft, setDraft] = useState('');
  const [query, setQuery] = useState('');
  const results = useQuery({
    queryKey: queryKeys.cameras.dashboardSearch(query),
    queryFn: ({ signal }) => api.cameras.list({ q: query, limit: 8 }, signal),
    enabled: Boolean(query),
  });

  function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const next = draft.trim();
    if (next === query && next) void results.refetch();
    setQuery(next);
  }

  function locate(camera: CameraResponse) {
    const bounds = initialBoundsFromCameras([camera]);
    if (bounds) onLocate(bounds, camera.id);
  }

  return <section className="map-search" aria-labelledby="map-search-heading">
    <h3 id="map-search-heading">Find on map</h3>
    <form className="map-search__form" onSubmit={submit} role="search">
      <label>Jump to a camera<input type="search" value={draft} onChange={(event) => setDraft(event.target.value)} aria-describedby="map-search-help" /></label>
      <p id="map-search-help">Picking a result pans the map to that camera and opens its details.</p>
      <button className="button button--secondary" type="submit">Search map</button>
    </form>
    {query && <div aria-live="polite" aria-busy={results.isFetching}>
      {results.isPending ? <p>Searching…</p>
        : results.isError ? <div role="alert"><p>{isApiProblem(results.error) ? results.error.detail : 'Could not search cameras.'}</p><button className="button" type="button" onClick={() => results.refetch()}>Retry search</button></div>
          : results.data.items.length === 0 ? <p>No cameras match this search.</p>
            : <ul className="map-search__results">{results.data.items.map((camera) => (
              <li key={camera.id}>
                <button className="map-search__result" type="button" onClick={() => locate(camera)}>
                  {camera.name} · {camera.cameraCode}
                </button>
              </li>
            ))}</ul>}
    </div>}
  </section>;
}
