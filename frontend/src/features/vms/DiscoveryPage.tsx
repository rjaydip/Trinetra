import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useMemo, useState, type FormEvent } from 'react';
import { Link, useParams } from 'react-router-dom';

import { errorDetail } from '../../api/client';
import { api } from '../../api/endpoints';
import type {
  BulkImportResult,
  FederatedCameraResponse,
  GeoJsonFeatureCollection,
  GeographicAreaResponse,
  OrganizationResponse,
} from '../../api/models';
import { queryKeys } from '../../api/queryKeys';
import { Button, PageState, StatusBadge } from '../../components/ui';
import { useDocumentTitle } from '../../lib/useDocumentTitle';
import { LocationPicker } from '../cameras/LocationPicker';
import { TreeSelect } from '../cameras/TreeSelect';
import { cameraTypes } from '../cameras/cameraVocabulary';
import { healthStatusTone } from './vmsTone';
import './vms.css';
import {
  createDiscoveredCameraEnrichment,
  toDiscoveredCameraWriteRequest,
  validateDiscoveredCameraEnrichment,
  validateDiscoveredCameraSelectionCount,
  type DiscoveredCameraEnrichment,
  type DiscoveryEnrichmentErrors,
} from './discovery';

function displayName(row: FederatedCameraResponse) {
  return row.name?.trim() || row.nativeCameraId;
}

function validCoordinate(value: string, minimum: number, maximum: number): number | null {
  if (!value.trim()) return null;
  const number = Number(value);
  return Number.isFinite(number) && number >= minimum && number <= maximum ? number : null;
}

function boundedBbox(latitude: number, longitude: number) {
  const span = 0.01;
  const west = Math.min(Math.max(longitude - span / 2, -180), 180 - span);
  const south = Math.min(Math.max(latitude - span / 2, -90), 90 - span);
  return `${west},${south},${west + span},${south + span}`;
}

function FieldError({ id, message }: { id: string; message?: string }) {
  return message ? <p className="form-error" id={id} role="alert">{message}</p> : null;
}

function ReferenceError({ children, retry }: { children: string; retry(): void }) {
  return <div className="selector-status" role="status"><p>{children}</p><button className="button button--secondary" type="button" onClick={retry}>Retry</button></div>;
}

function StatusHistoryPanel({ vmsId, row }: { vmsId: string; row: FederatedCameraResponse }) {
  const history = useQuery({
    queryKey: queryKeys.vms.cameraStatusHistory(vmsId, row.nativeCameraId),
    queryFn: ({ signal }) => api.vms.cameraStatusHistory(vmsId, row.nativeCameraId, undefined, signal),
  });

  if (history.isPending) return <p>Loading status history…</p>;
  if (history.isError) return <p className="form-error">{errorDetail(history.error, 'Status history could not be loaded.')}</p>;
  if (history.data.changes.length === 0) return <p className="admin-empty">No status changes recorded yet.</p>;

  return <div className="camera-table-wrap"><table className="camera-table"><thead><tr>
    <th scope="col">Changed at</th><th scope="col">Health</th><th scope="col">Enabled</th><th scope="col">Recording</th>
  </tr></thead><tbody>{history.data.changes.map((change) => <tr key={change.changedAt}>
    <td>{new Date(change.changedAt).toLocaleString()}</td>
    <td>{change.previousHealth ?? 'Unknown'} → {change.health}</td>
    <td>{change.previousEnabled === null ? 'Unknown' : change.previousEnabled ? 'Enabled' : 'Disabled'} → {change.isEnabled ? 'Enabled' : 'Disabled'}</td>
    <td>{change.previousRecording === null ? 'Unknown' : change.previousRecording ? 'Recording' : 'Not recording'} → {change.isRecording === null ? 'Unknown' : change.isRecording ? 'Recording' : 'Not recording'}</td>
  </tr>)}</tbody></table></div>;
}

interface DiscoveryRowProps {
  vmsId: string;
  row: FederatedCameraResponse;
  enrichment: DiscoveredCameraEnrichment;
  organizationId: string;
  organizations: OrganizationResponse[];
  geographicAreas: GeographicAreaResponse[];
  organizationsReady: boolean;
  geographicAreasReady: boolean;
  selected: boolean;
  expanded: boolean;
  errors: DiscoveryEnrichmentErrors;
  onChange(patch: Partial<DiscoveredCameraEnrichment>): void;
  onOrganizationChange(organizationId: string): void;
  onToggleExpanded(): void;
  onToggleSelected(): void;
}

function DiscoveryRow({
  vmsId,
  row,
  enrichment,
  organizationId,
  organizations,
  geographicAreas,
  organizationsReady,
  geographicAreasReady,
  selected,
  expanded,
  errors,
  onChange,
  onOrganizationChange,
  onToggleExpanded,
  onToggleSelected,
}: DiscoveryRowProps) {
  const name = displayName(row);
  const linked = row.cameraId !== null;
  const organizationUnits = useQuery({
    queryKey: queryKeys.reference.organizationUnits(organizationId),
    queryFn: ({ signal }) => api.reference.organizationUnits(organizationId, signal),
    enabled: Boolean(organizationId),
  });
  const latitude = validCoordinate(enrichment.latitude, -90, 90);
  const longitude = validCoordinate(enrichment.longitude, -180, 180);
  const mapBbox = selected && latitude !== null && longitude !== null ? boundedBbox(latitude, longitude) : null;
  const mapContext = useQuery({
    queryKey: queryKeys.gisCameras.vmsDiscoveryPicker(row.nativeCameraId, mapBbox),
    queryFn: ({ signal }) => api.gis.cameras({ bbox: mapBbox! }, signal),
    enabled: mapBbox !== null,
  });
  const mapFeatures: GeoJsonFeatureCollection | undefined = mapContext.data;
  const errorProps = (field: keyof DiscoveredCameraEnrichment) => ({
    'aria-describedby': errors[field] ? `${row.nativeCameraId}-${field}-error` : undefined,
    'aria-invalid': Boolean(errors[field]),
  });
  const label = (fieldLabel: string) => `${fieldLabel} for ${name}`;
  const update = (field: keyof DiscoveredCameraEnrichment, value: string) => onChange({ [field]: value });
  const optionalNumbers: Array<{ field: keyof Pick<DiscoveredCameraEnrichment, 'altitude' | 'mountingHeight' | 'tilt' | 'horizontalFov' | 'verticalFov' | 'effectiveRange'>; label: string }> = [
    { field: 'altitude', label: 'Altitude' },
    { field: 'mountingHeight', label: 'Mounting height' },
    { field: 'tilt', label: 'Tilt' },
    { field: 'horizontalFov', label: 'Horizontal field of view' },
    { field: 'verticalFov', label: 'Vertical field of view' },
    { field: 'effectiveRange', label: 'Effective range' },
  ];

  return <>
    <tr>
      <td><input aria-label={`Select ${name}`} checked={selected} disabled={linked} onChange={onToggleSelected} type="checkbox" /></td>
      <td><strong>{name}</strong><div>{row.nativeCameraId}</div></td>
      <td>{row.vendorModel ?? 'Not reported'}<div>Firmware: {row.firmware ?? 'Not reported'}</div></td>
      <td><StatusBadge tone={healthStatusTone(row.health)}>{row.health}</StatusBadge><div>{row.isEnabled ? 'Enabled' : 'Disabled'} · {row.isRecording === null ? 'Recording unknown' : row.isRecording ? 'Recording' : 'Not recording'}</div></td>
      <td>{linked ? <span id={`${row.nativeCameraId}-linked-help`}>Already linked; this camera cannot be imported again.</span> : 'Not linked'}</td>
      <td><button aria-controls={`${row.nativeCameraId}-enrichment`} aria-expanded={expanded} className="button button--secondary" disabled={linked} onClick={onToggleExpanded} type="button">{expanded ? 'Hide' : 'Edit'} onboarding details for {name}</button></td>
    </tr>
    {expanded && <tr className="discovery-enrichment-row"><td colSpan={6}>
      <section id={`${row.nativeCameraId}-enrichment`} className="camera-form discovery-enrichment" aria-label={`Onboarding details for ${name}`}>
        <fieldset><legend>Required registry placement</legend>
          <label>{label('Camera code')}<input aria-required="true" value={enrichment.cameraCode} onChange={(event) => update('cameraCode', event.target.value)} {...errorProps('cameraCode')} /></label><FieldError id={`${row.nativeCameraId}-cameraCode-error`} message={errors.cameraCode} />
          <label>{label('Name')}<input aria-required="true" value={enrichment.name} onChange={(event) => update('name', event.target.value)} {...errorProps('name')} /></label><FieldError id={`${row.nativeCameraId}-name-error`} message={errors.name} />
          <label>{label('Organization')}<select disabled={!organizationsReady} value={organizationId} onChange={(event) => onOrganizationChange(event.target.value)}><option value="">Select an organization</option>{organizations.map((organization) => <option key={organization.id} value={organization.id}>{organization.name} ({organization.code})</option>)}</select></label>
          <TreeSelect
            id={`${row.nativeCameraId}-organizationUnitId`}
            label={label('Organization unit')}
            items={organizationUnits.data}
            getParentId={(unit) => unit.parentUnitId}
            value={enrichment.organizationUnitId || undefined}
            onChange={(unitId) => update('organizationUnitId', unitId ?? '')}
            disabled={!organizationId || organizationUnits.isPending || organizationUnits.isError}
            loading={organizationUnits.isPending}
            error={organizationUnits.isError}
            required
            invalid={Boolean(errors.organizationUnitId)}
            describedBy={errors.organizationUnitId ? `${row.nativeCameraId}-organizationUnitId-error` : undefined}
            placeholder="Select an organization unit"
            emptyMessage="This organization has no units."
          /><FieldError id={`${row.nativeCameraId}-organizationUnitId-error`} message={errors.organizationUnitId} />
          {organizationUnits.isError && <ReferenceError retry={() => { void organizationUnits.refetch(); }}>{errorDetail(organizationUnits.error, 'Organization units could not be loaded. Please try again.')}</ReferenceError>}
          <TreeSelect
            id={`${row.nativeCameraId}-geographicAreaId`}
            label={label('Geographic area')}
            items={geographicAreas}
            getParentId={(area) => area.parentAreaId}
            value={enrichment.geographicAreaId || undefined}
            onChange={(areaId) => update('geographicAreaId', areaId ?? '')}
            disabled={!geographicAreasReady}
            required
            invalid={Boolean(errors.geographicAreaId)}
            describedBy={errors.geographicAreaId ? `${row.nativeCameraId}-geographicAreaId-error` : undefined}
            placeholder="Select a geographic area"
            emptyMessage="No geographic areas are available."
          /><FieldError id={`${row.nativeCameraId}-geographicAreaId-error`} message={errors.geographicAreaId} />
          <label>{label('Camera type')}<select aria-required="true" value={enrichment.cameraType} onChange={(event) => update('cameraType', event.target.value)} {...errorProps('cameraType')}><option value="">Select a camera type</option>{cameraTypes.map((type) => <option key={type} value={type}>{type}</option>)}</select></label><FieldError id={`${row.nativeCameraId}-cameraType-error`} message={errors.cameraType} />
          <label>{label('Latitude')}<input aria-required="true" inputMode="decimal" value={enrichment.latitude} onChange={(event) => update('latitude', event.target.value)} {...errorProps('latitude')} /></label><FieldError id={`${row.nativeCameraId}-latitude-error`} message={errors.latitude} />
          <label>{label('Longitude')}<input aria-required="true" inputMode="decimal" value={enrichment.longitude} onChange={(event) => update('longitude', event.target.value)} {...errorProps('longitude')} /></label><FieldError id={`${row.nativeCameraId}-longitude-error`} message={errors.longitude} />
        </fieldset>
        <fieldset><legend>Optional coverage details</legend>
          <LocationPicker
            latitude={latitude}
            longitude={longitude}
            azimuth={validCoordinate(enrichment.azimuth, 0, 359.999)}
            azimuthError={errors.azimuth}
            features={mapFeatures}
            onLocationChange={(nextLatitude, nextLongitude) => onChange({ latitude: String(nextLatitude), longitude: String(nextLongitude) })}
            onAzimuthChange={(nextAzimuth) => update('azimuth', nextAzimuth === null ? '' : String(nextAzimuth))}
          />
          {optionalNumbers.map(({ field, label: fieldLabel }) => <div key={field}><label>{label(fieldLabel)}<input type="number" step="any" value={enrichment[field]} onChange={(event) => update(field, event.target.value)} {...errorProps(field)} /></label><FieldError id={`${row.nativeCameraId}-${field}-error`} message={errors[field]} /></div>)}
        </fieldset>
        <fieldset><legend>Recent status changes</legend>
          <StatusHistoryPanel vmsId={vmsId} row={row} />
        </fieldset>
      </section>
    </td></tr>}
  </>;
}

function ImportResults({ result }: { result: BulkImportResult }) {
  return <section className="import-result" aria-labelledby="discovery-import-result-title">
    <h2 id="discovery-import-result-title">Import results</h2>
    <p role="status">Created: {result.created}. Updated: {result.updated}. Failed: {result.failed}.</p>
    <div className="camera-table-wrap import-preview__table"><table className="camera-table"><thead><tr><th>Row</th><th>Camera code</th><th>Status</th><th>Result</th></tr></thead><tbody>{result.rows.map((row) => <tr key={`${row.index}-${row.cameraCode}`}><td>{row.index + 1}</td><td>{row.cameraCode}</td><td>{row.status}</td><td>{row.error ?? (row.cameraId ? `Camera ID: ${row.cameraId}` : 'Completed')}</td></tr>)}</tbody></table></div>
  </section>;
}

export function DiscoveryPage() {
  const { vmsId = '' } = useParams();
  return <DiscoveryWorkspace key={vmsId} vmsId={vmsId} />;
}

function DiscoveryWorkspace({ vmsId }: { vmsId: string }) {
  const queryClient = useQueryClient();
  const target = useQuery({ queryKey: queryKeys.vms.detail(vmsId), queryFn: ({ signal }) => api.vms.get(vmsId, signal), enabled: Boolean(vmsId) });
  useDocumentTitle(target.data ? `Import cameras from ${target.data.displayName}` : 'VMS discovery');
  const cameras = useQuery({ queryKey: queryKeys.vms.discoveredCameras(vmsId), queryFn: ({ signal }) => api.vms.discoveredCameras(vmsId, signal), enabled: Boolean(vmsId) });
  const organizations = useQuery({ queryKey: queryKeys.reference.organizations, queryFn: ({ signal }) => api.reference.organizations(signal) });
  const geographicAreas = useQuery({ queryKey: queryKeys.reference.geographicAreas, queryFn: ({ signal }) => api.reference.geographicAreas(undefined, signal) });
  const [selected, setSelected] = useState<Set<string>>(() => new Set());
  const [expanded, setExpanded] = useState<Set<string>>(() => new Set());
  const [enrichments, setEnrichments] = useState<Record<string, DiscoveredCameraEnrichment>>({});
  const [organizationIds, setOrganizationIds] = useState<Record<string, string>>({});
  const [errors, setErrors] = useState<Record<string, DiscoveryEnrichmentErrors>>({});
  const [selectionError, setSelectionError] = useState('');
  const [result, setResult] = useState<BulkImportResult | null>(null);
  const importMutation = useMutation({
    mutationFn: api.cameras.bulkImport,
    onSuccess: async (nextResult) => {
      setResult(nextResult);
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: queryKeys.cameras.all }),
        queryClient.invalidateQueries({ queryKey: queryKeys.camera.all }),
        queryClient.invalidateQueries({ queryKey: queryKeys.gisCameras.all }),
      ]);
    },
  });
  const rowsById = useMemo(() => new Map((cameras.data ?? []).map((row) => [row.nativeCameraId, row])), [cameras.data]);
  const importableIds = useMemo(() => (cameras.data ?? []).filter((row) => row.cameraId === null).map((row) => row.nativeCameraId), [cameras.data]);

  if (target.isPending || cameras.isPending) return <PageState title="Loading discovered cameras">Retrieving the VMS inventory…</PageState>;
  if (target.isError) return <><PageState title="Couldn’t load VMS">{errorDetail(target.error, 'The selected VMS could not be loaded.')}</PageState><button className="button" type="button" onClick={() => target.refetch()}>Try again</button></>;
  if (cameras.isError) return <><PageState title="Couldn’t load discovered cameras">{errorDetail(cameras.error, 'The discovered camera inventory could not be loaded.')}</PageState><button className="button" type="button" onClick={() => cameras.refetch()}>Try again</button></>;

  const organizationsReady = !organizations.isPending && !organizations.isError && Boolean(organizations.data?.length);
  const geographicAreasReady = !geographicAreas.isPending && !geographicAreas.isError && Boolean(geographicAreas.data?.length);
  const enrichmentFor = (row: FederatedCameraResponse) => enrichments[row.nativeCameraId]
    ?? createDiscoveredCameraEnrichment(row, target.data.id, target.data.code);
  const updateEnrichment = (row: FederatedCameraResponse, patch: Partial<DiscoveredCameraEnrichment>) => {
    setEnrichments((current) => ({ ...current, [row.nativeCameraId]: { ...(current[row.nativeCameraId] ?? createDiscoveredCameraEnrichment(row, target.data.id, target.data.code)), ...patch } }));
    setErrors((current) => current[row.nativeCameraId] ? { ...current, [row.nativeCameraId]: {} } : current);
    setResult(null);
  };
  const toggleSelected = (row: FederatedCameraResponse) => {
    if (row.cameraId !== null) return;
    setSelected((current) => {
      const next = new Set(current);
      if (next.has(row.nativeCameraId)) next.delete(row.nativeCameraId);
      else next.add(row.nativeCameraId);
      return next;
    });
    if (!selected.has(row.nativeCameraId)) setExpanded((current) => new Set(current).add(row.nativeCameraId));
    setSelectionError('');
    setResult(null);
  };
  const submit = async (event: FormEvent) => {
    event.preventDefault();
    setSelectionError('');
    setResult(null);
    if (selected.size === 0) {
      setSelectionError('Select at least one discovered camera to import.');
      return;
    }
    const selectionLimitError = validateDiscoveredCameraSelectionCount(selected.size);
    if (selectionLimitError) {
      setSelectionError(selectionLimitError);
      return;
    }
    if ([...selected].some((nativeCameraId) => rowsById.get(nativeCameraId)?.cameraId !== null)) {
      setSelectionError('Already linked cameras cannot be imported again. Refresh the selection and try again.');
      return;
    }
    const nextErrors: Record<string, DiscoveryEnrichmentErrors> = {};
    selected.forEach((nativeCameraId) => {
      const row = rowsById.get(nativeCameraId);
      if (!row) return;
      const rowErrors = validateDiscoveredCameraEnrichment(enrichmentFor(row), row);
      if (Object.keys(rowErrors).length) nextErrors[nativeCameraId] = rowErrors;
    });
    setErrors(nextErrors);
    if (Object.keys(nextErrors).length) {
      setExpanded((current) => new Set([...current, ...Object.keys(nextErrors)]));
      return;
    }
    const items = [...selected].flatMap((nativeCameraId) => {
      const row = rowsById.get(nativeCameraId);
      return row ? [toDiscoveredCameraWriteRequest(row, enrichmentFor(row))] : [];
    });
    try {
      await importMutation.mutateAsync({ mode: 'upsert', items });
    } catch {
      // The mutation state renders the normalized API problem while preserving row edits.
    }
  };

  return <section className="onboarding-page discovery-page" aria-labelledby="discovery-page-title">
    <Link className="back-link" to={`/vms/${target.data.id}`}>Back to {target.data.displayName}</Link>
    <header><p className="eyebrow">VMS discovery</p><h1 id="discovery-page-title">Import cameras from {target.data.displayName}</h1><p>Select cameras and add the registry placement needed to import them. VMS facts remain read-only.</p></header>
    {organizations.isError && <ReferenceError retry={() => { void organizations.refetch(); }}>{errorDetail(organizations.error, 'Organizations could not be loaded. Please try again.')}</ReferenceError>}
    {geographicAreas.isError && <ReferenceError retry={() => { void geographicAreas.refetch(); }}>{errorDetail(geographicAreas.error, 'Geographic areas could not be loaded. Please try again.')}</ReferenceError>}
    {cameras.data.length === 0 ? <PageState title="No discovered cameras">This VMS has not reported any cameras yet.</PageState> : <form className="discovery-form" onSubmit={(event) => { void submit(event); }}>
      <label className="checkbox-label"><input
        aria-label="Select all importable cameras"
        checked={importableIds.length > 0 && importableIds.every((nativeCameraId) => selected.has(nativeCameraId))}
        onChange={(event) => {
          setSelected((current) => {
            if (event.target.checked) return new Set(importableIds);
            const next = new Set(current);
            importableIds.forEach((nativeCameraId) => next.delete(nativeCameraId));
            return next;
          });
          setSelectionError('');
          setResult(null);
        }}
        type="checkbox"
      /> Select all importable cameras</label>
      <p>{selected.size} camera{selected.size === 1 ? '' : 's'} selected.</p>
      {target.data.expectedCameraCount !== null && target.data.expectedCameraCount !== undefined && target.data.expectedCameraCount !== cameras.data.length && <p role="status">
        Expected {target.data.expectedCameraCount} camera{target.data.expectedCameraCount === 1 ? '' : 's'} for this VMS, but {cameras.data.length} {cameras.data.length === 1 ? 'was' : 'were'} reported. <Link to="/cameras/reconciliation">Review unreconciled cameras</Link>.
      </p>}
      <div className="camera-table-wrap"><table className="camera-table discovery-table"><caption>{cameras.data.length} camera{cameras.data.length === 1 ? '' : 's'} reported by {target.data.displayName}</caption><thead><tr><th scope="col">Select</th><th scope="col">Camera</th><th scope="col">Vendor facts</th><th scope="col">State</th><th scope="col">Registry</th><th scope="col">Onboarding</th></tr></thead><tbody>{cameras.data.map((row) => <DiscoveryRow
        key={row.nativeCameraId}
        vmsId={target.data.id}
        row={row}
        enrichment={enrichmentFor(row)}
        organizationId={organizationIds[row.nativeCameraId] ?? ''}
        organizations={organizations.data ?? []}
        geographicAreas={geographicAreas.data ?? []}
        organizationsReady={organizationsReady}
        geographicAreasReady={geographicAreasReady}
        selected={selected.has(row.nativeCameraId)}
        expanded={expanded.has(row.nativeCameraId)}
        errors={errors[row.nativeCameraId] ?? {}}
        onChange={(patch) => updateEnrichment(row, patch)}
        onOrganizationChange={(organizationId) => {
          setOrganizationIds((current) => ({ ...current, [row.nativeCameraId]: organizationId }));
          updateEnrichment(row, { organizationUnitId: '' });
        }}
        onToggleExpanded={() => setExpanded((current) => {
          const next = new Set(current);
          if (next.has(row.nativeCameraId)) next.delete(row.nativeCameraId);
          else next.add(row.nativeCameraId);
          return next;
        })}
        onToggleSelected={() => toggleSelected(row)}
      />)}</tbody></table></div>
      {selectionError && <p className="form-error" role="alert">{selectionError}</p>}
      {importMutation.isError && <p className="form-error" role="alert">{errorDetail(importMutation.error, 'Unable to import the selected cameras. Please try again.')}</p>}
      <Button disabled={importMutation.isPending} type="submit">{importMutation.isPending ? 'Importing selected cameras…' : 'Import selected'}</Button>
    </form>}
    {result && <ImportResults result={result} />}
  </section>;
}
