import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useCallback, useEffect, useMemo, useState } from 'react';
import { useNavigate, useParams } from 'react-router-dom';

import { isApiProblem } from '../../api/client';
import { api } from '../../api/endpoints';
import type { CameraPatchRequest, CameraResponse } from '../../api/models';
import { queryKeys } from '../../api/queryKeys';
import { PageState } from '../../components/ui';
import { useDocumentTitle } from '../../lib/useDocumentTitle';
import { CameraForm, type CameraFormValues, type CameraSelectorStates, type SelectorState } from './CameraForm';
import './cameras.css';

function referenceError(error: unknown, fallback: string): string {
  return isApiProblem(error) ? error.detail : fallback;
}

function optionalString(value: string | null | undefined): string {
  return value ?? '';
}

function optionalNumberString(value: number | null | undefined): string {
  return value === null || value === undefined ? '' : String(value);
}

function toCameraFormValues(camera: CameraResponse): CameraFormValues {
  return {
    cameraCode: camera.cameraCode,
    name: camera.name,
    organizationId: '', // resolved separately from organizationUnitId to pre-scope the unit picker.
    organizationUnitId: camera.organizationUnitId,
    geographicAreaId: camera.geographicAreaId,
    cameraType: camera.cameraType,
    latitude: String(camera.latitude),
    longitude: String(camera.longitude),
    manufacturer: optionalString(camera.manufacturer),
    model: optionalString(camera.model),
    serialNumber: optionalString(camera.serialNumber),
    altitude: optionalNumberString(camera.altitude),
    mountingHeight: optionalNumberString(camera.mountingHeight),
    azimuth: optionalNumberString(camera.azimuth),
    tilt: optionalNumberString(camera.tilt),
    horizontalFov: optionalNumberString(camera.horizontalFov),
    verticalFov: optionalNumberString(camera.verticalFov),
    effectiveRange: optionalNumberString(camera.effectiveRange),
    ipAddress: optionalString(camera.ipAddress),
    port: optionalNumberString(camera.port),
    protocol: optionalString(camera.protocol),
    username: '', // credentials are write-only — never round-tripped back into the form.
    password: '',
    recordEvents: camera.recordEvents ?? true,
    vmsId: optionalString(camera.vmsId),
    streamReference: optionalString(camera.streamReference),
    streamPreference: camera.streamPreference ?? 'RTSP',
    nativeHlsUrl: optionalString(camera.nativeHlsUrl),
    nativeWebrtcUrl: optionalString(camera.nativeWebrtcUrl),
    installationDate: optionalString(camera.installationDate),
    operationalStatus: camera.operationalStatus,
    connectivityStatus: camera.connectivityStatus,
    maintenanceStatus: camera.maintenanceStatus,
  };
}

export function EditCameraPage() {
  const { cameraId } = useParams();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const camera = useQuery({ queryKey: queryKeys.camera.detail(cameraId!), queryFn: ({ signal }) => api.cameras.get(cameraId!, signal), enabled: Boolean(cameraId) });
  useDocumentTitle(camera.data ? `Edit ${camera.data.cameraCode}` : 'Edit camera');

  const [organizationId, setOrganizationId] = useState('');
  const [coordinates, setCoordinates] = useState<{ latitude: number; longitude: number } | null>(null);

  // The camera's saved organization unit may belong to an organization not yet selected above —
  // resolve it once so the organization step (and the unit tree under it) starts pre-filled
  // instead of forcing a re-pick of something already chosen. Same pattern as `OrgGeoDesignationFields`
  // in UsersPage.tsx for the identical "pre-select the cascading picker's parent" problem.
  const currentUnit = useQuery({
    queryKey: queryKeys.reference.organizationUnit(camera.data?.organizationUnitId ?? ''),
    queryFn: ({ signal }) => api.reference.organizationUnit(camera.data!.organizationUnitId, signal),
    enabled: Boolean(camera.data?.organizationUnitId) && !organizationId,
  });
  useEffect(() => {
    if (currentUnit.data && !organizationId) setOrganizationId(currentUnit.data.organizationId);
  }, [currentUnit.data, organizationId]);

  useEffect(() => {
    if (camera.data) setCoordinates({ latitude: camera.data.latitude, longitude: camera.data.longitude });
  }, [camera.data]);

  const organizations = useQuery({ queryKey: queryKeys.reference.organizations, queryFn: ({ signal }) => api.reference.organizations(signal) });
  const geographicAreas = useQuery({ queryKey: queryKeys.reference.geographicAreas, queryFn: ({ signal }) => api.reference.geographicAreas(undefined, signal) });
  const vms = useQuery({ queryKey: queryKeys.vms.all, queryFn: ({ signal }) => api.vms.list(signal) });
  const organizationUnits = useQuery({
    queryKey: queryKeys.reference.organizationUnits(organizationId),
    enabled: Boolean(organizationId),
    queryFn: ({ signal }) => api.reference.organizationUnits(organizationId, signal),
  });
  const mapBbox = useMemo(() => {
    if (!coordinates) return null;
    const span = 0.01;
    const west = Math.min(Math.max(coordinates.longitude - span / 2, -180), 180 - span);
    const south = Math.min(Math.max(coordinates.latitude - span / 2, -90), 90 - span);
    return `${west},${south},${west + span},${south + span}`;
  }, [coordinates]);
  const mapContext = useQuery({
    queryKey: queryKeys.gisCameras.cameraPicker(mapBbox),
    queryFn: ({ signal }) => api.gis.cameras({ bbox: mapBbox! }, signal),
    enabled: mapBbox !== null,
  });
  const handleCoordinatesChange = useCallback((latitude: number | null, longitude: number | null) => {
    setCoordinates((current) => {
      if (latitude === null || longitude === null) return null;
      if (current?.latitude === latitude && current.longitude === longitude) return current;
      return { latitude, longitude };
    });
  }, []);
  const update = useMutation({
    mutationFn: async ({ id, request }: { id: string; request: CameraPatchRequest }) => api.cameras.update(id, request),
    onSuccess: async (_data, variables) => {
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: queryKeys.cameras.all }),
        queryClient.invalidateQueries({ queryKey: queryKeys.camera.all }),
        queryClient.invalidateQueries({ queryKey: queryKeys.gisCameras.all }),
      ]);
      navigate(`/cameras/${variables.id}`);
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

  if (camera.isPending) return <PageState title="Loading camera details">Retrieving the current registry record…</PageState>;
  if (camera.isError || !camera.data) return <PageState title="Couldn&apos;t load camera details">Return to the registry and select a camera again.</PageState>;
  // `CameraForm` seeds react-hook-form's state once, from `defaultValues` at mount — it isn't
  // reactive to `initialValues` changing later. Hold the form off screen until the owning
  // organization resolves so it mounts with the correct organization already selected, instead of
  // remounting (and discarding in-progress edits) every time `organizationId` changes afterwards.
  if (!organizationId) return <PageState title="Loading camera details">Retrieving the current registry record…</PageState>;

  const initialValues: CameraFormValues = { ...toCameraFormValues(camera.data), organizationId };

  return <section className="onboarding-page" aria-labelledby="edit-camera-title"><header><p className="eyebrow">{camera.data.cameraCode}</p><h1 id="edit-camera-title">Edit camera</h1><p>Update this camera's details. Its camera code cannot be changed.</p></header><CameraForm organizations={organizations.data ?? []} organizationUnits={organizationUnits.data ?? []} geographicAreas={geographicAreas.data ?? []} vms={vms.data ?? []} selectorStates={selectorStates} mapFeatures={mapContext.data} initialValues={initialValues} initialCredentialReference={camera.data.credentialReference ?? undefined} cameraId={camera.data.id} onCoordinatesChange={handleCoordinatesChange} onOrganizationChange={setOrganizationId} onSubmit={async (id, request) => { await update.mutateAsync({ id, request }); }} /></section>;
}
