import { useQuery } from '@tanstack/react-query';
import { useEffect, useMemo, useState } from 'react';

import { api } from '../../api/endpoints';
import { PageState } from '../../components/ui';
import { CameraDetailDrawer } from './CameraDetailDrawer';
import { CameraMap } from './CameraMap';
import { MapFilters } from './MapFilters';
import { buildMapRequest, filterMapFeatures, initialBoundsFromCameras, type Bounds, type MapFilters as MapFiltersValue } from './geo';

export function MapPage() {
  const [filters, setFilters] = useState<MapFiltersValue>({});
  const [bounds, setBounds] = useState<Bounds | null>(null);
  const [cameraId, setCameraId] = useState<string | null>(null);
  const registry = useQuery({ queryKey: ['cameras', 'map-bootstrap'], queryFn: () => api.cameras.list({ limit: 100 }) });
  const initialBounds = useMemo(() => registry.data && initialBoundsFromCameras(registry.data.items), [registry.data]);

  useEffect(() => { if (initialBounds && !bounds) setBounds(initialBounds); }, [bounds, initialBounds]);

  const request = bounds && buildMapRequest(bounds, filters);
  const map = useQuery({
    queryKey: ['gis-cameras', request?.toString()],
    queryFn: () => api.gis.cameras({
      bbox: request!.get('bbox')!,
      includeSectors: request!.get('includeSectors') === 'true' || undefined,
      organizationUnitId: request!.get('organizationUnitId') ?? undefined,
      operationalStatus: request!.get('operationalStatus') ?? undefined,
      maintenanceStatus: request!.get('maintenanceStatus') ?? undefined,
    }),
    enabled: request !== null,
  });
  const features = useMemo(() => map.data ? filterMapFeatures(map.data, filters) : { type: 'FeatureCollection' as const, features: [] }, [filters, map.data]);

  if (registry.isPending) return <PageState title="Loading camera map">Finding live registry coordinates…</PageState>;
  if (registry.isError) return <PageState title="Couldn&apos;t load map setup">Refresh the page to retrieve current camera coordinates.</PageState>;
  if (!initialBounds) return <PageState title="No camera coordinates available">Add coordinates to a camera before using the map.</PageState>;

  return (
    <section className="map-page" aria-labelledby="camera-map-title">
      <header className="map-page__header">
        <div><p className="eyebrow">Live registry view</p><h1 id="camera-map-title">Camera map</h1></div>
        {filters.coverage && <p className="coverage-notice">Coverage sectors are estimated planning aids; terrain and obstructions are not modelled.</p>}
      </header>
      <MapFilters filters={filters} onChange={setFilters} />
      {map.isError && <div className="map-error" role="alert"><strong>Couldn&apos;t load map cameras.</strong> <button className="button" type="button" onClick={() => map.refetch()}>Try again</button></div>}
      {bounds && <CameraMap bounds={bounds} features={features} onBoundsChange={setBounds} onSelect={setCameraId} />}
      <p className="map-legend" aria-label="Map legend">Clusters group nearby cameras. Select an individual camera marker to view its registry details.</p>
      <CameraDetailDrawer cameraId={cameraId} onClose={() => setCameraId(null)} />
    </section>
  );
}
