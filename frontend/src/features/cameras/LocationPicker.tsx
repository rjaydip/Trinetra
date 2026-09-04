import { useEffect, useRef } from 'react';
import type { Map as MapLibreMap, Marker as MapLibreMarker, MapMouseEvent, StyleSpecification } from 'maplibre-gl';
import 'maplibre-gl/dist/maplibre-gl.css';

import type { GeoJsonFeatureCollection } from '../../api/models';
import { roundCoordinate } from './cameraVocabulary';

const MAP_STYLE: StyleSpecification = {
  version: 8,
  sources: {
    osm: { type: 'raster', tiles: ['https://tile.openstreetmap.org/{z}/{x}/{y}.png'], tileSize: 256, attribution: '© OpenStreetMap contributors' },
  },
  layers: [{ id: 'osm', type: 'raster', source: 'osm' }],
};

const EMPTY_FEATURES: GeoJsonFeatureCollection = { type: 'FeatureCollection', features: [] };

export interface LocationPickerProps {
  latitude: number | null;
  longitude: number | null;
  azimuth: number | null;
  features?: GeoJsonFeatureCollection;
  azimuthError?: string;
  onLocationChange(latitude: number, longitude: number): void;
  onAzimuthChange(azimuth: number | null): void;
}

function validLocation(latitude: number | null, longitude: number | null): latitude is number {
  return latitude !== null && longitude !== null
    && Number.isFinite(latitude) && Number.isFinite(longitude)
    && latitude >= -90 && latitude <= 90 && longitude >= -180 && longitude <= 180;
}

function toGeoJson(collection: GeoJsonFeatureCollection) {
  return collection as unknown as GeoJSON.FeatureCollection;
}

function roundedAzimuth(value: number) {
  return Number((((value % 360) + 360) % 360).toFixed(3));
}

function azimuthFromDelta(dx: number, dy: number) {
  return roundedAzimuth((Math.atan2(dx, -dy) * 180 / Math.PI + 360) % 360);
}

export function LocationPicker({
  latitude,
  longitude,
  azimuth,
  features = EMPTY_FEATURES,
  azimuthError,
  onLocationChange,
  onAzimuthChange,
}: LocationPickerProps) {
  const container = useRef<HTMLDivElement>(null);
  const map = useRef<MapLibreMap | null>(null);
  const marker = useRef<MapLibreMarker | null>(null);
  const markerElement = useRef<HTMLDivElement | null>(null);
  const featuresRef = useRef(features);
  const azimuthRef = useRef(azimuth);
  const onLocationChangeRef = useRef(onLocationChange);
  const onAzimuthChangeRef = useRef(onAzimuthChange);
  onLocationChangeRef.current = onLocationChange;
  onAzimuthChangeRef.current = onAzimuthChange;
  featuresRef.current = features;
  azimuthRef.current = azimuth;

  useEffect(() => {
    if (!container.current || map.current || import.meta.env.MODE === 'test') return undefined;
    let cancelled = false;
    let instance: MapLibreMap | undefined;
    let pin: MapLibreMarker | undefined;
    let removeWindowListeners: (() => void) | undefined;
    let removeHandleListener: (() => void) | undefined;

    void import('maplibre-gl').then(({ default: maplibregl }) => {
      if (cancelled || !container.current) return;
      const hasLocation = validLocation(latitude, longitude);
      instance = new maplibregl.Map({
        container: container.current,
        style: MAP_STYLE,
        center: hasLocation ? [longitude!, latitude] : [0, 0],
        zoom: hasLocation ? 15 : 1,
        maxBounds: [[-180, -90], [180, 90]],
      });
      map.current = instance;
      instance.addControl(new maplibregl.NavigationControl(), 'top-right');

      const element = document.createElement('div');
      element.className = 'location-picker__marker';
      element.setAttribute('aria-hidden', 'true');
      element.innerHTML = '<span class="location-picker__pin"></span><span class="location-picker__azimuth-arm"><span class="location-picker__azimuth-handle"></span></span>';
      markerElement.current = element;
      const arm = element.querySelector<HTMLElement>('.location-picker__azimuth-arm');
      if (arm) arm.style.transform = `rotate(${azimuthRef.current ?? 0}deg)`;

      const handle = element.querySelector<HTMLElement>('.location-picker__azimuth-handle');
      const startAzimuthDrag = (event: PointerEvent) => {
        event.preventDefault();
        event.stopPropagation();
        const update = (pointerEvent: PointerEvent) => {
          const bounds = element.getBoundingClientRect();
          const dx = pointerEvent.clientX - (bounds.left + bounds.width / 2);
          const dy = pointerEvent.clientY - (bounds.top + bounds.height / 2);
          onAzimuthChangeRef.current(azimuthFromDelta(dx, dy));
        };
        const stop = () => {
          window.removeEventListener('pointermove', update);
          window.removeEventListener('pointerup', stop);
          removeWindowListeners = undefined;
        };
        window.addEventListener('pointermove', update);
        window.addEventListener('pointerup', stop);
        removeWindowListeners?.();
        removeWindowListeners = stop;
        update(event);
      };
      handle?.addEventListener('pointerdown', startAzimuthDrag);
      removeHandleListener = () => handle?.removeEventListener('pointerdown', startAzimuthDrag);

      pin = new maplibregl.Marker({ element, draggable: true });
      marker.current = pin;
      if (hasLocation) pin.setLngLat([longitude!, latitude]).addTo(instance);
      pin.on('dragend', () => {
        const next = pin!.getLngLat();
        onLocationChangeRef.current(roundCoordinate(next.lat), roundCoordinate(next.lng));
      });

      instance.on('load', () => {
        instance!.addSource('context-cameras', { type: 'geojson', data: toGeoJson(featuresRef.current) });
        instance!.addLayer({
          id: 'context-camera-points',
          type: 'circle',
          source: 'context-cameras',
          paint: { 'circle-color': '#52627a', 'circle-opacity': 0.65, 'circle-radius': 4, 'circle-stroke-color': '#ffffff', 'circle-stroke-width': 1 },
        });
      });
      instance.on('click', (event: MapMouseEvent) => {
        onLocationChangeRef.current(roundCoordinate(event.lngLat.lat), roundCoordinate(event.lngLat.lng));
      });
    });

    return () => {
      cancelled = true;
      removeWindowListeners?.();
      removeHandleListener?.();
      pin?.remove();
      instance?.remove();
      marker.current = null;
      markerElement.current = null;
      map.current = null;
    };
  }, []);

  useEffect(() => {
    if (!map.current || !marker.current) return;
    if (!validLocation(latitude, longitude)) {
      marker.current.remove();
      return;
    }
    marker.current.setLngLat([longitude!, latitude]);
    if (!marker.current.getElement().parentElement) marker.current.addTo(map.current);
    map.current.easeTo({ center: [longitude!, latitude] });
  }, [latitude, longitude]);

  useEffect(() => {
    const source = map.current?.getSource('context-cameras');
    if (source && 'setData' in source && typeof source.setData === 'function') source.setData(toGeoJson(features));
  }, [features]);

  useEffect(() => {
    const arm = markerElement.current?.querySelector<HTMLElement>('.location-picker__azimuth-arm');
    if (arm) arm.style.transform = `rotate(${azimuth ?? 0}deg)`;
  }, [azimuth]);

  return <section className="location-picker" aria-labelledby="location-picker-title">
    <div>
      <h3 id="location-picker-title">Map location and direction</h3>
      <p>Select a point on the map or enter coordinates above. Drag the direction handle or enter an azimuth below.</p>
    </div>
    <div className="location-picker__canvas" ref={container} data-testid="location-picker-map">
      {import.meta.env.MODE === 'test' && <>
        <button type="button" onClick={() => onLocationChange(roundCoordinate(19.076012345), roundCoordinate(72.8777))}>Set location to 19.076012345, 72.8777</button>
        <button type="button" onClick={() => onAzimuthChange(azimuthFromDelta(1, 0))}>Point azimuth east</button>
      </>}
    </div>
    <label className="location-picker__azimuth-input">Azimuth
      <input
        type="number"
        min="0"
        max="359.999"
        step="0.001"
        value={azimuth ?? ''}
        aria-describedby={azimuthError ? 'location-picker-azimuth-error' : undefined}
        aria-invalid={Boolean(azimuthError)}
        onChange={(event) => {
          if (event.target.value === '') {
            onAzimuthChange(null);
            return;
          }
          const next = Number(event.target.value);
          if (Number.isFinite(next) && next >= 0 && next <= 359.999) onAzimuthChange(roundedAzimuth(next));
        }}
      />
    </label>
    {azimuthError && <p className="form-error" id="location-picker-azimuth-error" role="alert">{azimuthError}</p>}
  </section>;
}
