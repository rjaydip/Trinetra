import { useQuery } from '@tanstack/react-query';
import { useState, type FormEvent } from 'react';

import type { Bounds } from './geo';
import { MAX_GIS_BOUNDS_DEGREES } from './geo';

interface NominatimResult {
  place_id: number;
  display_name: string;
  lat: string;
  lon: string;
  /** `[south, north, west, east]`, all as strings — Nominatim's own field order, not this app's
   * `Bounds` tuple order. */
  boundingbox: [string, string, string, string];
}

/** Keeps a result's own bounding box, clamped to a size the GIS feed can actually query
 * (`MAX_GIS_BOUNDS_DEGREES` — see `buildMapRequest`) and never smaller than a sane minimum, so a
 * point address (whose `boundingbox` is a few meters wide) still lands at a useful zoom level and
 * a whole country (whose `boundingbox` is far wider than the GIS feed's own limit) doesn't just
 * silently return "zoom in" instead of ever showing a camera. */
function boundsFromResult(result: NominatimResult): Bounds {
  const south = Number(result.boundingbox[0]);
  const north = Number(result.boundingbox[1]);
  const west = Number(result.boundingbox[2]);
  const east = Number(result.boundingbox[3]);

  const centerLat = Number(result.lat);
  const centerLon = Number(result.lon);
  const minSpan = 0.01;
  const maxSpan = MAX_GIS_BOUNDS_DEGREES * 0.9;

  const latSpan = Math.min(Math.max(north - south, minSpan), maxSpan);
  const lonSpan = Math.min(Math.max(east - west, minSpan), maxSpan);

  return [
    Math.max(centerLon - lonSpan / 2, -180),
    Math.max(centerLat - latSpan / 2, -90),
    Math.min(centerLon + lonSpan / 2, 180),
    Math.min(centerLat + latSpan / 2, 90),
  ];
}

/**
 * Searches a place or address (city, landmark, street) and pans the map there — distinct from
 * `MapSearchBox`, which searches *registered cameras* by name/code. Backed by OpenStreetMap's
 * public Nominatim API directly from the browser: no API key, but its usage policy caps this at
 * roughly one request per second, so a result only ever fires on explicit submit, never on every
 * keystroke.
 */
export function LocationSearchBox({ onLocate }: { onLocate(bounds: Bounds): void }) {
  const [draft, setDraft] = useState('');
  const [query, setQuery] = useState('');
  const results = useQuery({
    queryKey: ['location-search', query],
    queryFn: async ({ signal }) => {
      const url = new URL('https://nominatim.openstreetmap.org/search');
      url.searchParams.set('format', 'json');
      url.searchParams.set('q', query);
      url.searchParams.set('limit', '5');
      const response = await fetch(url, { signal });
      if (!response.ok) throw new Error(`Location search failed with status ${response.status}`);
      return (await response.json()) as NominatimResult[];
    },
    enabled: Boolean(query),
  });

  function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const next = draft.trim();
    if (next === query && next) void results.refetch();
    setQuery(next);
  }

  return <section className="map-search" aria-labelledby="location-search-heading">
    <h3 id="location-search-heading">Go to a location</h3>
    <form className="map-search__form" onSubmit={submit} role="search">
      <label>Search a place or address<input type="search" value={draft} onChange={(event) => setDraft(event.target.value)} aria-describedby="location-search-help" /></label>
      <p id="location-search-help">Picking a result pans and zooms the map there. Location data © <a href="https://www.openstreetmap.org/copyright" target="_blank" rel="noreferrer">OpenStreetMap</a> contributors.</p>
      <button className="button button--secondary" type="submit">Search location</button>
    </form>
    {query && <div aria-live="polite" aria-busy={results.isFetching}>
      {results.isPending ? <p>Searching…</p>
        : results.isError ? <div role="alert"><p>Could not search locations.</p><button className="button" type="button" onClick={() => results.refetch()}>Retry search</button></div>
          : results.data.length === 0 ? <p>No locations match this search.</p>
            : <ul className="map-search__results">{results.data.map((result) => (
              <li key={result.place_id}>
                <button className="map-search__result" type="button" onClick={() => onLocate(boundsFromResult(result))}>
                  {result.display_name}
                </button>
              </li>
            ))}</ul>}
    </div>}
  </section>;
}
