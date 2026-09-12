import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useMemo, useState, type FormEvent } from 'react';
import { Link, useParams } from 'react-router-dom';

import { isApiProblem } from '../../api/client';
import { api } from '../../api/endpoints';
import type {
  BulkImportResult,
  FederatedCameraResponse,
  GeoJsonFeatureCollection,
  OrganizationResponse,
  OrganizationUnitResponse,
  SiteResponse,
} from '../../api/models';
import { Button, PageState, StatusBadge } from '../../components/ui';
import { LocationPicker } from '../cameras/LocationPicker';
import { cameraTypes } from '../cameras/cameraVocabulary';
import {
  createDiscoveredCameraEnrichment,
  toDiscoveredCameraWriteRequest,
  validateDiscoveredCameraEnrichment,
  validateDiscoveredCameraSelectionCount,
  type DiscoveredCameraEnrichment,
  type DiscoveryEnrichmentErrors,
} from './discovery';

function errorDetail(error: unknown, fallback: string) {
  return isApiProblem(error) ? error.detail : fallback;
}

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

interface DiscoveryRowProps {
  row: FederatedCameraResponse;
  enrichment: DiscoveredCameraEnrichment;
  organizationId: string;
  organizations: OrganizationResponse[];
  sites: SiteResponse[];
  organizationsReady: boolean;
  sitesReady: boolean;
  selected: boolean;
  expanded: boolean;
  errors: DiscoveryEnrichmentErrors;
  onChange(patch: Partial<DiscoveredCameraEnrichment>): void;
  onOrganizationChange(organizationId: string): void;
  onToggleExpanded(): void;
  onToggleSelected(): void;
}

function DiscoveryRow({
  row,
  enrichment,
  organizationId,
  organizations,
  sites,
  organizationsReady,
  sitesReady,
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
    queryKey: ['reference', 'organization-units', organizationId],
    queryFn: () => api.reference.organizationUnits(organizationId),
    enabled: Boolean(organizationId),
  });
  const latitude = validCoordinate(enrichment.latitude, -90, 90);
  const longitude = validCoordinate(enrichment.longitude, -180, 180);
  const mapBbox = selected && latitude !== null && longitude !== null ? boundedBbox(latitude, longitude) : null;
  const mapContext = useQuery({
    queryKey: ['gis-cameras', 'vms-discovery-picker', row.nativeCameraId, mapBbox],
    queryFn: () => api.gis.cameras({ bbox: mapBbox! }),
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
      <td><StatusBadge tone={row.health.toLowerCase() === 'online' ? 'success' : 'warning'}>{row.health}</StatusBadge><div>{row.isEnabled ? 'Enabled' : 'Disabled'} · {row.isRecording === null ? 'Recording unknown' : row.isRecording ? 'Recording' : 'Not recording'}</div></td>
      <td>{linked ? <span id={`${row.nativeCameraId}-linked-help`}>Already linked; this camera cannot be imported again.</span> : 'Not linked'}</td>
      <td><button aria-controls={`${row.nativeCameraId}-enrichment`} aria-expanded={expanded} className="button button--secondary" disabled={linked} onClick={onToggleExpanded} type="button">{expanded ? 'Hide' : 'Edit'} onboarding details for {name}</button></td>
    </tr>
    {expanded && <tr className="discovery-enrichment-row"><td colSpan={6}>
      <section id={`${row.nativeCameraId}-enrichment`} className="camera-form discovery-enrichment" aria-label={`Onboarding details for ${name}`}>
        <fieldset><legend>Required registry placement</legend>
          <label>{label('Camera code')}<input aria-required="true" value={enrichment.cameraCode} onChange={(event) => update('cameraCode', event.target.value)} {...errorProps('cameraCode')} /></label><FieldError id={`${row.nativeCameraId}-cameraCode-error`} message={errors.cameraCode} />
          <label>{label('Name')}<input aria-required="true" value={enrichment.name} onChange={(event) => update('name', event.target.value)} {...errorProps('name')} /></label><FieldError id={`${row.nativeCameraId}-name-error`} message={errors.name} />
          <label>{label('Organization')}<select disabled={!organizationsReady} value={organizationId} onChange={(event) => onOrganizationChange(event.target.value)}><option value="">Select an organization</option>{organizations.map((organization) => <option key={organization.id} value={organization.id}>{organization.name} ({organization.code})</option>)}</select></label>
          <label>{label('Organization unit')}<select aria-required="true" disabled={!organizationId || organizationUnits.isPending || organizationUnits.isError} value={enrichment.organizationUnitId} onChange={(event) => update('organizationUnitId', event.target.value)} {...errorProps('organizationUnitId')}><option value="">Select an organization unit</option>{(organizationUnits.data ?? []).map((unit: OrganizationUnitResponse) => <option key={unit.id} value={unit.id}>{unit.name} ({unit.code})</option>)}</select></label><FieldError id={`${row.nativeCameraId}-organizationUnitId-error`} message={errors.organizationUnitId} />
          {organizationUnits.isError && <ReferenceError retry={() => { void organizationUnits.refetch(); }}>{errorDetail(organizationUnits.error, 'Organization units could not be loaded. Please try again.')}</ReferenceError>}
          <label>{label('Site')}<select aria-required="true" disabled={!sitesReady} value={enrichment.siteId} onChange={(event) => update('siteId', event.target.value)} {...errorProps('siteId')}><option value="">Select a site</option>{sites.map((site) => <option key={site.id} value={site.id}>{site.name} ({site.code})</option>)}</select></label><FieldError id={`${row.nativeCameraId}-siteId-error`} message={errors.siteId} />
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
      </section>
    </td></tr>}
  </>;
}

function ImportResults({ result }: { result: BulkImportResult }) {
  return <section className="import-result" aria-labelledby="discovery-import-result-title" role="region">
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
  const target = useQuery({ queryKey: ['vms', vmsId], queryFn: () => api.vms.get(vmsId), enabled: Boolean(vmsId) });
  const cameras = useQuery({ queryKey: ['vms', vmsId, 'discovered-cameras'], queryFn: () => api.vms.discoveredCameras(vmsId), enabled: Boolean(vmsId) });
  const organizations = useQuery({ queryKey: ['reference', 'organizations'], queryFn: api.reference.organizations });
  const sites = useQuery({ queryKey: ['reference', 'sites'], queryFn: () => api.reference.sites() });
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
        queryClient.invalidateQueries({ queryKey: ['cameras'] }),
        queryClient.invalidateQueries({ queryKey: ['camera'] }),
        queryClient.invalidateQueries({ queryKey: ['gis-cameras'] }),
      ]);
    },
  });
  const rowsById = useMemo(() => new Map((cameras.data ?? []).map((row) => [row.nativeCameraId, row])), [cameras.data]);
  const importableIds = useMemo(() => (cameras.data ?? []).filter((row) => row.cameraId === null).map((row) => row.nativeCameraId), [cameras.data]);

  if (target.isPending || cameras.isPending) return <PageState title="Loading discovered cameras">Retrieving the VMS inventory…</PageState>;
  if (target.isError) return <><PageState title="Couldn’t load VMS">{errorDetail(target.error, 'The selected VMS could not be loaded.')}</PageState><button className="button" type="button" onClick={() => target.refetch()}>Try again</button></>;
  if (cameras.isError) return <><PageState title="Couldn’t load discovered cameras">{errorDetail(cameras.error, 'The discovered camera inventory could not be loaded.')}</PageState><button className="button" type="button" onClick={() => cameras.refetch()}>Try again</button></>;

  const organizationsReady = !organizations.isPending && !organizations.isError && Boolean(organizations.data?.length);
  const sitesReady = !sites.isPending && !sites.isError && Boolean(sites.data?.length);
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
    if (!selected.has(row.nativeCameraId)) setExpanded(new Set([row.nativeCameraId]));
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
      setExpanded(new Set([Object.keys(nextErrors)[0]]));
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
    {sites.isError && <ReferenceError retry={() => { void sites.refetch(); }}>{errorDetail(sites.error, 'Sites could not be loaded. Please try again.')}</ReferenceError>}
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
      <div className="camera-table-wrap"><table className="camera-table discovery-table"><caption>{cameras.data.length} camera{cameras.data.length === 1 ? '' : 's'} reported by {target.data.displayName}</caption><thead><tr><th scope="col">Select</th><th scope="col">Camera</th><th scope="col">Vendor facts</th><th scope="col">State</th><th scope="col">Registry</th><th scope="col">Onboarding</th></tr></thead><tbody>{cameras.data.map((row) => <DiscoveryRow
        key={row.nativeCameraId}
        row={row}
        enrichment={enrichmentFor(row)}
        organizationId={organizationIds[row.nativeCameraId] ?? ''}
        organizations={organizations.data ?? []}
        sites={sites.data ?? []}
        organizationsReady={organizationsReady}
        sitesReady={sitesReady}
        selected={selected.has(row.nativeCameraId)}
        expanded={expanded.has(row.nativeCameraId)}
        errors={errors[row.nativeCameraId] ?? {}}
        onChange={(patch) => updateEnrichment(row, patch)}
        onOrganizationChange={(organizationId) => {
          setOrganizationIds((current) => ({ ...current, [row.nativeCameraId]: organizationId }));
          updateEnrichment(row, { organizationUnitId: '' });
        }}
        onToggleExpanded={() => setExpanded((current) => {
          return current.has(row.nativeCameraId) ? new Set() : new Set([row.nativeCameraId]);
        })}
        onToggleSelected={() => toggleSelected(row)}
      />)}</tbody></table></div>
      <p>{selected.size} camera{selected.size === 1 ? '' : 's'} selected.</p>
      {selectionError && <p className="form-error" role="alert">{selectionError}</p>}
      {importMutation.isError && <p className="form-error" role="alert">{errorDetail(importMutation.error, 'Unable to import the selected cameras. Please try again.')}</p>}
      <Button disabled={importMutation.isPending} type="submit">{importMutation.isPending ? 'Importing selected cameras…' : 'Import selected'}</Button>
    </form>}
    {result && <ImportResults result={result} />}
  </section>;
}
