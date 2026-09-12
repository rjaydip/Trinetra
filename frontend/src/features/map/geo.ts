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
    || west < -180 || east > 180 || south < -90 || north > 90
    || east - west > MAX_GIS_BOUNDS_DEGREES || north - south > MAX_GIS_BOUNDS_DEGREES) return null;

  const query = new URLSearchParams({ bbox: `${west},${south},${east},${north}` });
  if (filters.organizationUnitId) query.set('organizationUnitId', filters.organizationUnitId);
  if (filters.operationalStatus) query.set('operationalStatus', filters.operationalStatus);
  if (filters.maintenanceStatus) query.set('maintenanceStatus', filters.maintenanceStatus);
  if (filters.coverage) query.set('includeSectors', 'true');
  return query;
}

export function initialBoundsFromCameras(cameras: CameraResponse[]): Bounds | null {
  // Target a real record, never the possibly unpopulated midpoint of an estate.
  const target = cameras.find((camera) => Number.isFinite(camera.latitude) && Number.isFinite(camera.longitude)
    && Math.abs(camera.latitude) <= 90 && Math.abs(camera.longitude) <= 180);
  if (!target) return null;
  const span = 0.02;
  // Shift the whole interval at the poles/dateline so it stays nonzero, valid,
  // and contains the target without constructing a wrapping bbox.
  const west = Math.min(Math.max(target.longitude - span / 2, -180), 180 - span);
  const south = Math.min(Math.max(target.latitude - span / 2, -90), 90 - span);
  return [west, south, Math.min(west + span, 180), Math.min(south + span, 90)];
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
