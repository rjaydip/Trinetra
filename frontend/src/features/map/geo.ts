import type { CameraResponse, GeoJsonFeature, GeoJsonFeatureCollection } from '../../api/models';

export type Bounds = readonly [west: number, south: number, east: number, north: number];

export interface MapFilters {
  organizationUnitId?: string;
  operationalStatus?: string;
  maintenanceStatus?: string;
  coverage?: boolean;
  cameraType?: string;
  connectivityStatus?: string;
}

export const MAX_GIS_BOUNDS_DEGREES = 2;

export function buildMapRequest(bounds: Bounds, filters: MapFilters): URLSearchParams | null {
  const [west, south, east, north] = bounds;
  if (!Number.isFinite(west) || !Number.isFinite(south) || !Number.isFinite(east) || !Number.isFinite(north)
    || west >= east || south >= north
    || east - west > MAX_GIS_BOUNDS_DEGREES || north - south > MAX_GIS_BOUNDS_DEGREES) return null;

  const query = new URLSearchParams({ bbox: `${west},${south},${east},${north}` });
  if (filters.organizationUnitId) query.set('organizationUnitId', filters.organizationUnitId);
  if (filters.operationalStatus) query.set('operationalStatus', filters.operationalStatus);
  if (filters.maintenanceStatus) query.set('maintenanceStatus', filters.maintenanceStatus);
  if (filters.coverage) query.set('includeSectors', 'true');
  return query;
}

export function initialBoundsFromCameras(cameras: CameraResponse[]): Bounds | null {
  const coordinates = cameras.filter((camera) => Number.isFinite(camera.latitude) && Number.isFinite(camera.longitude));
  if (!coordinates.length) return null;

  const west = Math.min(...coordinates.map((camera) => camera.longitude));
  const east = Math.max(...coordinates.map((camera) => camera.longitude));
  const south = Math.min(...coordinates.map((camera) => camera.latitude));
  const north = Math.max(...coordinates.map((camera) => camera.latitude));
  const centerLongitude = (west + east) / 2;
  const centerLatitude = (south + north) / 2;
  const longitudeSpan = Math.min(Math.max(east - west, 0.02), 1.8);
  const latitudeSpan = Math.min(Math.max(north - south, 0.02), 1.8);

  return [
    centerLongitude - longitudeSpan / 2,
    centerLatitude - latitudeSpan / 2,
    centerLongitude + longitudeSpan / 2,
    centerLatitude + latitudeSpan / 2,
  ];
}

function property(feature: GeoJsonFeature, name: string): string | undefined {
  const value = feature.properties[name];
  return typeof value === 'string' ? value : undefined;
}

export function filterMapFeatures(collection: GeoJsonFeatureCollection, filters: MapFilters): GeoJsonFeatureCollection {
  const matches = (feature: GeoJsonFeature) =>
    (!filters.cameraType || property(feature, 'cameraType') === filters.cameraType)
    && (!filters.connectivityStatus || property(feature, 'connectivityStatus') === filters.connectivityStatus);

  return { ...collection, features: collection.features.filter(matches) };
}

export function cameraIdForFeature(feature: GeoJsonFeature): string | null {
  return property(feature, 'cameraId') ?? null;
}

export function coverageFeatures(collection: GeoJsonFeatureCollection): GeoJsonFeatureCollection {
  return {
    type: 'FeatureCollection',
    features: collection.features.flatMap((feature) => {
      const coordinates = feature.properties.coverageSector;
      return Array.isArray(coordinates)
        ? [{ type: 'Feature' as const, geometry: { type: 'Polygon' as const, coordinates }, properties: feature.properties }]
        : [];
    }),
  };
}
