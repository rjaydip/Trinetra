import { useEffect, useRef } from 'react';
import type { Map as MapLibreMap, MapLayerMouseEvent, StyleSpecification } from 'maplibre-gl';
import 'maplibre-gl/dist/maplibre-gl.css';

import type { GeoJsonFeatureCollection } from '../../api/models';
import { cameraIdForFeature, coverageFeatures, type Bounds } from './geo';

const MAP_STYLE: StyleSpecification = {
  version: 8,
  sources: {
    osm: { type: 'raster', tiles: ['https://tile.openstreetmap.org/{z}/{x}/{y}.png'], tileSize: 256, attribution: '© OpenStreetMap contributors' },
  },
  layers: [{ id: 'osm', type: 'raster', source: 'osm' }],
};

function toGeoJson(collection: GeoJsonFeatureCollection) {
  return collection as unknown as GeoJSON.FeatureCollection;
}

export function CameraMap({ bounds, features, onBoundsChange, onSelect }: {
  bounds: Bounds;
  features: GeoJsonFeatureCollection;
  onBoundsChange(bounds: Bounds): void;
  onSelect(cameraId: string): void;
}) {
  const container = useRef<HTMLDivElement>(null);
  const map = useRef<MapLibreMap | null>(null);
  const featuresRef = useRef(features);
  const onBoundsChangeRef = useRef(onBoundsChange);
  const onSelectRef = useRef(onSelect);
  featuresRef.current = features;
  onBoundsChangeRef.current = onBoundsChange;
  onSelectRef.current = onSelect;

  useEffect(() => {
    if (!container.current || map.current || navigator.userAgent.includes('jsdom')) return undefined;
    let timer: number | undefined;
    let cancelled = false;
    let instance: any;

    void import('maplibre-gl').then(({ default: maplibregl }) => {
      if (cancelled || !container.current) return;
      instance = new maplibregl.Map({ container: container.current, style: MAP_STYLE, bounds: [...bounds], maxBounds: [[-180, -90], [180, 90]], fitBoundsOptions: { padding: 32 } });
      map.current = instance;
      instance.addControl(new maplibregl.NavigationControl(), 'top-right');
      instance.on('load', () => {
      instance.addSource('cameras', { type: 'geojson', data: toGeoJson(featuresRef.current), cluster: true, clusterMaxZoom: 14, clusterRadius: 45 });
      instance.addSource('coverage', { type: 'geojson', data: toGeoJson(coverageFeatures(featuresRef.current)) });
      instance.addLayer({ id: 'coverage-fill', type: 'fill', source: 'coverage', paint: { 'fill-color': '#075985', 'fill-opacity': 0.18 } });
      instance.addLayer({ id: 'clusters', type: 'circle', source: 'cameras', filter: ['has', 'point_count'], paint: { 'circle-color': '#075985', 'circle-radius': ['step', ['get', 'point_count'], 18, 25, 24, 100, 30] } });
      instance.addLayer({ id: 'cluster-count', type: 'symbol', source: 'cameras', filter: ['has', 'point_count'], layout: { 'text-field': '{point_count_abbreviated}', 'text-size': 12 }, paint: { 'text-color': '#ffffff' } });
      instance.addLayer({ id: 'camera-points', type: 'circle', source: 'cameras', filter: ['!', ['has', 'point_count']], paint: { 'circle-color': '#b91c1c', 'circle-radius': 7, 'circle-stroke-color': '#ffffff', 'circle-stroke-width': 2 } });
      instance.on('click', 'camera-points', (event: MapLayerMouseEvent) => {
        const feature = event.features?.[0] as unknown as GeoJsonFeatureCollection['features'][number] | undefined;
        const cameraId = feature && cameraIdForFeature(feature);
        if (cameraId) onSelectRef.current(cameraId);
      });
      });
      instance.on('moveend', () => {
        window.clearTimeout(timer);
        timer = window.setTimeout(() => {
          const next = instance.getBounds();
          onBoundsChangeRef.current([next.getWest(), next.getSouth(), next.getEast(), next.getNorth()]);
        }, 300);
      });
    });

    return () => { cancelled = true; window.clearTimeout(timer); instance?.remove(); map.current = null; };
  }, []);

  useEffect(() => {
    const source = map.current?.getSource('cameras');
    if (source && 'setData' in source && typeof source.setData === 'function') source.setData(toGeoJson(features));
    const coverage = map.current?.getSource('coverage');
    if (coverage && 'setData' in coverage && typeof coverage.setData === 'function') coverage.setData(toGeoJson(coverageFeatures(features)));
  }, [features]);

  const selections = features.features.flatMap((feature) => {
    const cameraId = cameraIdForFeature(feature);
    if (!cameraId) return [];
    const name = typeof feature.properties.name === 'string'
      ? feature.properties.name
      : typeof feature.properties.cameraCode === 'string' ? feature.properties.cameraCode : cameraId;
    return [{ cameraId, name }];
  });

  return <>
    <div className="map-canvas" data-testid="camera-map" ref={container}>
      <p className="map-canvas__fallback">Interactive camera map. {features.features.length} visible camera{features.features.length === 1 ? '' : 's'}.</p>
    </div>
    <section aria-label="Visible camera selection" className="map-camera-selection">
      <h2>Visible cameras</h2>
      {selections.length ? <ul>{selections.map(({ cameraId, name }) => <li key={cameraId}>
        <button type="button" onClick={() => onSelect(cameraId)}>Open camera {name}</button>
      </li>)}</ul> : <p>No individual cameras are visible at this zoom level.</p>}
    </section>
  </>;
}
