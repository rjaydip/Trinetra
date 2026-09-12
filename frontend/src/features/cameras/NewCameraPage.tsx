import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useCallback, useMemo, useState } from 'react';
import { useNavigate } from 'react-router-dom';

import { isApiProblem } from '../../api/client';
import { api } from '../../api/endpoints';
import { CameraForm, type CameraSelectorStates, type SelectorState } from './CameraForm';

function referenceError(error: unknown, fallback: string): string {
  return isApiProblem(error) ? error.detail : fallback;
}

export function NewCameraPage() {
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const [organizationId, setOrganizationId] = useState('');
  const [coordinates, setCoordinates] = useState<{ latitude: number; longitude: number } | null>(null);
  const organizations = useQuery({ queryKey: ['reference', 'organizations'], queryFn: api.reference.organizations });
  const geographicAreas = useQuery({ queryKey: ['reference', 'geographic-areas'], queryFn: () => api.reference.geographicAreas() });
  const vms = useQuery({ queryKey: ['vms'], queryFn: api.vms.list });
  const organizationUnits = useQuery({
    queryKey: ['reference', 'organization-units', organizationId],
    enabled: Boolean(organizationId),
    queryFn: () => api.reference.organizationUnits(organizationId),
  });
  const mapBbox = useMemo(() => {
    if (!coordinates) return null;
    const span = 0.01;
    const west = Math.min(Math.max(coordinates.longitude - span / 2, -180), 180 - span);
    const south = Math.min(Math.max(coordinates.latitude - span / 2, -90), 90 - span);
    return `${west},${south},${west + span},${south + span}`;
  }, [coordinates]);
  const mapContext = useQuery({
    queryKey: ['gis-cameras', 'camera-picker', mapBbox],
    queryFn: () => api.gis.cameras({ bbox: mapBbox! }),
    enabled: mapBbox !== null,
  });
  const handleCoordinatesChange = useCallback((latitude: number | null, longitude: number | null) => {
    setCoordinates((current) => {
      if (latitude === null || longitude === null) return null;
      if (current?.latitude === latitude && current.longitude === longitude) return current;
      return { latitude, longitude };
    });
  }, []);
  const create = useMutation({
    mutationFn: api.cameras.create,
    onSuccess: async () => {
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: ['cameras'] }),
        queryClient.invalidateQueries({ queryKey: ['camera'] }),
        queryClient.invalidateQueries({ queryKey: ['gis-cameras'] }),
      ]);
      navigate('/cameras');
    },
  });
  const organizationsState: SelectorState = organizations.isPending || (organizations.isError && organizations.isFetching)
    ? { state: 'loading' }
    : organizations.isError
      ? { state: 'error', message: referenceError(organizations.error, 'Organizations could not be loaded. Please try again.'), retry: () => { void organizations.refetch(); } }
      : organizations.data?.length
        ? { state: 'ready' }
        : { state: 'empty' };
  const organizationUnitsState: SelectorState = !organizationId
    ? { state: 'idle' }
    : organizationUnits.isPending || (organizationUnits.isError && organizationUnits.isFetching)
      ? { state: 'loading' }
      : organizationUnits.isError
        ? { state: 'error', message: referenceError(organizationUnits.error, 'Organization units could not be loaded. Please try again.'), retry: () => { void organizationUnits.refetch(); } }
        : organizationUnits.data?.length
          ? { state: 'ready' }
          : { state: 'empty' };
  const geographicAreasState: SelectorState = geographicAreas.isPending || (geographicAreas.isError && geographicAreas.isFetching)
    ? { state: 'loading' }
    : geographicAreas.isError
      ? { state: 'error', message: referenceError(geographicAreas.error, 'Geographic areas could not be loaded. Please try again.'), retry: () => { void geographicAreas.refetch(); } }
      : geographicAreas.data?.length
        ? { state: 'ready' }
        : { state: 'empty' };
  const vmsState: SelectorState = vms.isPending || (vms.isError && vms.isFetching)
    ? { state: 'loading' }
    : vms.isError
      ? { state: 'error', message: referenceError(vms.error, 'VMS records could not be loaded. Please try again.'), retry: () => { void vms.refetch(); } }
      : vms.data?.length
        ? { state: 'ready' }
        : { state: 'empty' };
  const selectorStates: CameraSelectorStates = {
    organizations: organizationsState,
    organizationUnits: organizationUnitsState,
    geographicAreas: geographicAreasState,
    vms: vmsState,
  };

  return <section className="onboarding-page" aria-labelledby="new-camera-title"><header><p className="eyebrow">Camera registry</p><h1 id="new-camera-title">Register camera</h1><p>Required fields identify the camera and its physical location.</p></header><CameraForm organizations={organizations.data ?? []} organizationUnits={organizationUnits.data ?? []} geographicAreas={geographicAreas.data ?? []} vms={vms.data ?? []} selectorStates={selectorStates} mapFeatures={mapContext.data} onCoordinatesChange={handleCoordinatesChange} onOrganizationChange={setOrganizationId} onSubmit={async (values) => { await create.mutateAsync(values); }} /></section>;
}
