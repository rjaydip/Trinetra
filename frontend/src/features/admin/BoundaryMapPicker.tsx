import { useEffect, useRef, useState } from 'react';
import type { Map as MapLibreMap, MapMouseEvent, StyleSpecification } from 'maplibre-gl';
import 'maplibre-gl/dist/maplibre-gl.css';

import '../cameras/cameras.css';

const MAP_STYLE: StyleSpecification = {
  version: 8,
  sources: {
    osm: { type: 'raster', tiles: ['https://tile.openstreetmap.org/{z}/{x}/{y}.png'], tileSize: 256, attribution: '© OpenStreetMap contributors' },
  },
  layers: [{ id: 'osm', type: 'raster', source: 'osm' }],
};

/** Center of India, used when nothing else is known yet — matches `LocationPicker`'s own choice
 * for the same reason: every deployment this registry serves is in India. */
const INDIA_CENTER: [number, number] = [78.9629, 20.5937];
const INDIA_ZOOM = 4;

const DRAFT_SOURCE_ID = 'boundary-draft';

function ring(points: [number, number][]): [number, number][] {
  if (points.length < 3) return points;
  const first = points[0];
  const last = points[points.length - 1];
  return first[0] === last[0] && first[1] === last[1] ? points : [...points, first];
}

function toGeoJsonPolygon(points: [number, number][]): string {
  return JSON.stringify({ type: 'Polygon', coordinates: [ring(points)] });
}

/** Draft-geometry map source, redrawn on every point change — a point layer for placed vertices,
 * a line layer previewing the ring (open while drawing, closed once there are ≥3 points), and a
 * fill layer so the enclosed area is visibly a polygon, not just an outline. */
function draftGeoJson(points: [number, number][]): GeoJSON.FeatureCollection {
  const closedRing = ring(points);
  const features: GeoJSON.Feature[] = [
    { type: 'Feature', geometry: { type: 'MultiPoint', coordinates: points }, properties: {} },
  ];
  if (points.length >= 2) {
    features.push({ type: 'Feature', geometry: { type: 'LineString', coordinates: closedRing }, properties: {} });
  }
  if (points.length >= 3) {
    features.push({ type: 'Feature', geometry: { type: 'Polygon', coordinates: [closedRing] }, properties: {} });
  }
  return { type: 'FeatureCollection', features };
}

/**
 * Click-to-place polygon drawing for a boundary — every click adds a vertex; "Undo last point"
 * removes one; "Clear" starts over. Emits the drawn ring as a GeoJSON `Polygon` string
 * (`onChange`) once there are at least 3 points, `null` otherwise. Deliberately custom rather than
 * a drawing library (`@mapbox/mapbox-gl-draw` et al.): a boundary import is an occasional admin
 * action, not a high-frequency editing workflow, and click-to-add/undo/clear covers it without a
 * new dependency.
 *
 * Only ever mounted for "Draw on map" mode — `BoundaryImportPage` mounts/unmounts this component
 * as a whole when switching to/from "Paste GeoJSON/WKT" rather than keeping one map instance
 * alive across both modes and toggling a read-only preview inside it. That live-preview version
 * was tried first and reverted: switching modes changes the sibling controls' height, and
 * getting MapLibre to reliably notice its container was resized by that (rather than rendering
 * against a stale canvas size — visible as the map appearing to jump to the top-left corner) held
 * up under `ResizeObserver` + a double-`requestAnimationFrame` `resize()` call but never reliably
 * enough to ship. A fresh mount per mode sidesteps the whole class of bug at the cost of a losing
 * a nice-to-have (verifying a pasted boundary on the map before saving).
 */
export function BoundaryMapPicker({ onChange, center }: {
  onChange(geoJson: string | null): void;
  center?: [number, number];
}) {
  const container = useRef<HTMLDivElement>(null);
  const map = useRef<MapLibreMap | null>(null);
  const [points, setPoints] = useState<[number, number][]>([]);
  const pointsRef = useRef(points);
  pointsRef.current = points;
  const onChangeRef = useRef(onChange);
  onChangeRef.current = onChange;

  useEffect(() => {
    if (!container.current || map.current || import.meta.env.MODE === 'test') return undefined;
    let cancelled = false;
    let instance: MapLibreMap;

    void import('maplibre-gl').then(({ default: maplibregl }) => {
      if (cancelled || !container.current) return;
      instance = new maplibregl.Map({
        container: container.current,
        style: MAP_STYLE,
        center: center ?? INDIA_CENTER,
        zoom: center ? 11 : INDIA_ZOOM,
        maxBounds: [[-179.9, -89.9], [179.9, 89.9]],
      });
      map.current = instance;
      instance.addControl(new maplibregl.NavigationControl(), 'top-right');

      instance.on('load', () => {
        instance.addSource(DRAFT_SOURCE_ID, { type: 'geojson', data: draftGeoJson(pointsRef.current) });
        instance.addLayer({ id: `${DRAFT_SOURCE_ID}-fill`, type: 'fill', source: DRAFT_SOURCE_ID, filter: ['==', ['geometry-type'], 'Polygon'], paint: { 'fill-color': '#2f6fed', 'fill-opacity': .2 } });
        instance.addLayer({ id: `${DRAFT_SOURCE_ID}-line`, type: 'line', source: DRAFT_SOURCE_ID, filter: ['==', ['geometry-type'], 'LineString'], paint: { 'line-color': '#2f6fed', 'line-width': 2 } });
        instance.addLayer({ id: `${DRAFT_SOURCE_ID}-points`, type: 'circle', source: DRAFT_SOURCE_ID, filter: ['==', ['geometry-type'], 'MultiPoint'], paint: { 'circle-color': '#2f6fed', 'circle-radius': 5, 'circle-stroke-color': '#ffffff', 'circle-stroke-width': 1 } });
      });

      instance.on('click', (event: MapMouseEvent) => {
        setPoints((current) => [...current, [event.lngLat.lng, event.lngLat.lat]]);
      });
    });

    return () => {
      cancelled = true;
      instance?.remove();
      map.current = null;
    };
  }, [center]);

  useEffect(() => {
    const source = map.current?.getSource(DRAFT_SOURCE_ID);
    if (source && 'setData' in source) (source as { setData(data: GeoJSON.FeatureCollection): void }).setData(draftGeoJson(points));
    onChangeRef.current(points.length >= 3 ? toGeoJsonPolygon(points) : null);
  }, [points]);

  return <div className="boundary-map-picker">
    <div ref={container} className="boundary-map-picker__map" role="application" aria-label="Boundary drawing map — click to place a vertex" />
    <div className="boundary-map-picker__controls">
      <p className="field-help">Click the map to place vertices, in order around the boundary. {points.length} point{points.length === 1 ? '' : 's'} placed{points.length > 0 && points.length < 3 ? ` — at least 3 needed` : ''}.</p>
      <div className="boundary-map-picker__buttons">
        <button className="button button--secondary" disabled={points.length === 0} onClick={() => setPoints((current) => current.slice(0, -1))} type="button">Undo last point</button>
        <button className="button button--secondary" disabled={points.length === 0} onClick={() => setPoints([])} type="button">Clear</button>
      </div>
    </div>
  </div>;
}
