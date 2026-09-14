import { useQuery } from '@tanstack/react-query';
import { useEffect, useMemo, useState } from 'react';

import './map.css';
import { api } from '../../api/endpoints';
import { queryKeys } from '../../api/queryKeys';
import { PageState } from '../../components/ui';
import { useDocumentTitle } from '../../lib/useDocumentTitle';
import { AttentionList } from './AttentionList';
import { CameraDetailDrawer } from './CameraDetailDrawer';
import { CameraMap } from './CameraMap';
import { MapFilters } from './MapFilters';
import { DashboardSearch, FleetStatusStrip } from './DashboardData';
import { FleetStatusFooter } from './FleetStatusFooter';
import { buildMapRequest, filterMapFeatures, initialBoundsFromCameras, type Bounds, type MapFilters as MapFiltersValue } from './geo';

export function MapPage() {
  useDocumentTitle('Dashboard');
  const [filters, setFilters] = useState<MapFiltersValue>({});
  const [bounds, setBounds] = useState<Bounds | null>(null);
  const [cameraId, setCameraId] = useState<string | null>(null);
  const [mapOpen, setMapOpen] = useState(false);
  const registry = useQuery({ queryKey: queryKeys.cameras.mapBootstrap, queryFn: ({ signal }) => api.cameras.list({ limit: 100 }, signal), enabled: mapOpen });
  const initialBounds = useMemo(() => registry.data && initialBoundsFromCameras(registry.data.items), [registry.data]);

  useEffect(() => { if (initialBounds && !bounds) setBounds(initialBounds); }, [bounds, initialBounds]);

  const request = bounds && buildMapRequest(bounds, filters);
  const map = useQuery({
    queryKey: queryKeys.gisCameras.feed(request?.toString()),
    queryFn: ({ signal }) => api.gis.cameras({
      bbox: request!.get('bbox')!,
      includeSectors: request!.get('includeSectors') === 'true' || undefined,
      organizationUnitId: request!.get('organizationUnitId') ?? undefined,
      operationalStatus: request!.get('operationalStatus') ?? undefined,
      maintenanceStatus: request!.get('maintenanceStatus') ?? undefined,
    }, signal),
    enabled: request !== null,
  });
  const features = useMemo(() => map.data ? filterMapFeatures(map.data, filters) : { type: 'FeatureCollection' as const, features: [] }, [filters, map.data]);

  return (
    <section className="map-page" aria-labelledby="dashboard-title">
      <header className="map-page__header">
        <div><p className="eyebrow">Operations dashboard</p><h1 id="dashboard-title">Dashboard</h1></div>
      </header>
      <FleetStatusStrip />
      <AttentionList />
      <FleetStatusFooter />
      <section className="map-page__map-section" aria-labelledby="camera-map-title">
        <header className="map-page__map-header">
          <h2 id="camera-map-title">Camera map</h2>
          <button className="button button--secondary" type="button" aria-expanded={mapOpen} onClick={() => setMapOpen((open) => !open)}>
            {mapOpen ? 'Hide map' : 'Show full map'}
          </button>
        </header>
        <DashboardSearch />
        {mapOpen && <>
          {filters.coverage && <p className="coverage-notice">Coverage sectors are estimated planning aids; terrain and obstructions are not modelled.</p>}
          {registry.isPending ? <PageState title="Loading camera map">Finding live registry coordinates…</PageState>
            : registry.isError ? <div><PageState title="Couldn&apos;t load map setup">Try again to retrieve current camera coordinates.</PageState><button className="button" type="button" onClick={() => registry.refetch()}>Retry map setup</button></div>
              : !initialBounds ? <PageState title="No camera coordinates available">Add coordinates to a camera before using the map.</PageState>
                : <>
                  <MapFilters filters={filters} onChange={setFilters} />
                  {bounds && !request && <p className="map-page__hint" role="status">Zoom in to an area no wider or taller than 2°. Camera requests are paused at this zoom level.</p>}
                  {request && map.isPending && <p role="status">Loading cameras in this map area…</p>}
                  {map.isError && <div className="map-error" role="alert"><strong>Couldn&apos;t load map cameras.</strong> <button className="button" type="button" onClick={() => map.refetch()}>Try again</button></div>}
                  {bounds && <CameraMap bounds={bounds} features={features} dataLoaded={Boolean(request) && map.isSuccess} onBoundsChange={setBounds} onSelect={setCameraId} />}
                  <p className="map-legend" aria-label="Map legend">Clusters group nearby cameras. Select an individual camera marker to view its registry details.</p>
                </>}
        </>}
      </section>
      <CameraDetailDrawer cameraId={cameraId} onClose={() => setCameraId(null)} />
    </section>
  );
}
