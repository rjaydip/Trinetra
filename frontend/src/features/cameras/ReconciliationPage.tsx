import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Fragment, useState } from 'react';

import { errorDetail } from '../../api/client';
import { api } from '../../api/endpoints';
import type { CreateFromFederatedRequest, UnreconciledCameraResponse } from '../../api/models';
import { queryKeys } from '../../api/queryKeys';
import { useAuth } from '../../auth/AuthProvider';
import { hasPermission } from '../../auth/permissions';
import { Button, PageState } from '../../components/ui';
import { cameraTypes, roundCoordinate } from './cameraVocabulary';

function displayName(row: UnreconciledCameraResponse) {
  return row.name?.trim() || row.nativeCameraId;
}

function rowKey(row: UnreconciledCameraResponse) {
  return `${row.targetId}:${row.nativeCameraId}`;
}

function FieldError({ id, message }: { id: string; message?: string }) {
  return message ? <p className="form-error" id={id} role="alert">{message}</p> : null;
}

/** Look up an existing registry camera by code or name and link the VMS row to it directly. */
function LinkExistingForm({ row, onSuccess }: { row: UnreconciledCameraResponse; onSuccess(): Promise<unknown> | void }) {
  const [query, setQuery] = useState('');
  const results = useQuery({
    queryKey: queryKeys.cameras.search(query),
    queryFn: () => api.cameras.list({ q: query, limit: 5 }),
    enabled: query.trim().length >= 2,
  });
  const mutation = useMutation({
    mutationFn: (cameraId: string) => api.reconciliation.reconcile(cameraId, {
      targetId: row.targetId, nativeCameraId: row.nativeCameraId, adoptStreamReference: true, adoptVmsId: true,
    }),
    onSuccess,
  });

  return <div className="reconcile-form">
    <label>Search registry cameras by code or name
      <input onChange={(event) => setQuery(event.target.value)} placeholder="CAM-..." value={query} />
    </label>
    {mutation.isError && <p className="form-error" role="alert">{errorDetail(mutation.error, 'The camera could not be linked.')}</p>}
    {results.isFetching && <p>Searching…</p>}
    {query.trim().length >= 2 && !results.isFetching && (results.data?.items.length ?? 0) === 0 && (
      <p className="admin-empty">No registry cameras match that search.</p>
    )}
    {(results.data?.items.length ?? 0) > 0 && <ul className="admin-record-list">
      {results.data!.items.map((camera) => <li key={camera.id}>
        <div><strong>{camera.name}</strong><span>{camera.cameraCode}</span></div>
        <Button disabled={mutation.isPending} type="button" onClick={() => mutation.mutate(camera.id)}>Link</Button>
      </li>)}
    </ul>}
  </div>;
}

interface CreateFormValues {
  cameraCode: string;
  name: string;
  organizationUnitId: string;
  geographicAreaId: string;
  cameraType: string;
  latitude: string;
  longitude: string;
  azimuth: string;
  horizontalFov: string;
  effectiveRange: string;
}

type CreateFormErrors = Partial<Record<keyof CreateFormValues, string>>;

function initialCreateValues(row: UnreconciledCameraResponse): CreateFormValues {
  return {
    cameraCode: '',
    name: row.name?.trim() ?? '',
    organizationUnitId: '',
    geographicAreaId: row.geographicAreaId ?? '',
    cameraType: '',
    latitude: row.latitude !== null ? String(row.latitude) : '',
    longitude: row.longitude !== null ? String(row.longitude) : '',
    azimuth: '',
    horizontalFov: '',
    effectiveRange: '',
  };
}

function validateCreateValues(values: CreateFormValues): CreateFormErrors {
  const errors: CreateFormErrors = {};
  const code = values.cameraCode.trim();
  if (!code) errors.cameraCode = 'Camera code is required.';
  else if (code.length > 100) errors.cameraCode = 'Camera code must be at most 100 characters.';
  if (!cameraTypes.includes(values.cameraType as (typeof cameraTypes)[number])) errors.cameraType = 'Camera type is required.';
  if (!values.geographicAreaId.trim()) errors.geographicAreaId = 'Geographic area is required: the VMS row has none.';
  const latitude = Number(values.latitude);
  if (!values.latitude.trim() || !Number.isFinite(latitude) || latitude < -90 || latitude > 90) {
    errors.latitude = 'Latitude must be between -90 and 90.';
  }
  const longitude = Number(values.longitude);
  if (!values.longitude.trim() || !Number.isFinite(longitude) || longitude < -180 || longitude > 180) {
    errors.longitude = 'Longitude must be between -180 and 180.';
  }
  return errors;
}

function toCreateFromFederatedRequest(row: UnreconciledCameraResponse, values: CreateFormValues): CreateFromFederatedRequest {
  const request: CreateFromFederatedRequest = {
    targetId: row.targetId,
    nativeCameraId: row.nativeCameraId,
    cameraCode: values.cameraCode.trim(),
    cameraType: values.cameraType,
    latitude: roundCoordinate(Number(values.latitude)),
    longitude: roundCoordinate(Number(values.longitude)),
    adoptStreamReference: true,
    adoptVmsId: true,
  };
  if (values.name.trim()) request.name = values.name.trim();
  if (values.organizationUnitId.trim()) request.organizationUnitId = values.organizationUnitId.trim();
  if (values.geographicAreaId.trim()) request.geographicAreaId = values.geographicAreaId.trim();
  if (values.azimuth.trim()) request.azimuth = Number(values.azimuth);
  if (values.horizontalFov.trim()) request.horizontalFov = Number(values.horizontalFov);
  if (values.effectiveRange.trim()) request.effectiveRange = Number(values.effectiveRange);
  return request;
}

/** Register a registry camera from the VMS row's own facts and link the two in one call. */
function CreateFromFederatedForm({ row, onSuccess }: { row: UnreconciledCameraResponse; onSuccess(): Promise<unknown> | void }) {
  const [values, setValues] = useState<CreateFormValues>(() => initialCreateValues(row));
  const [errors, setErrors] = useState<CreateFormErrors>({});
  const mutation = useMutation({
    mutationFn: (body: CreateFromFederatedRequest) => api.reconciliation.createFromFederated(body),
    onSuccess,
  });
  const update = (field: keyof CreateFormValues, value: string) => {
    setValues((current) => ({ ...current, [field]: value }));
    setErrors((current) => (current[field] ? { ...current, [field]: undefined } : current));
  };

  function submit() {
    const nextErrors = validateCreateValues(values);
    setErrors(nextErrors);
    if (Object.keys(nextErrors).length) return;
    mutation.mutate(toCreateFromFederatedRequest(row, values));
  }

  return <div className="reconcile-form">
    <label>Camera code<span aria-hidden="true"> *</span>
      <input aria-required="true" value={values.cameraCode} onChange={(event) => update('cameraCode', event.target.value)} />
    </label><FieldError id={`${rowKey(row)}-cameraCode-error`} message={errors.cameraCode} />
    <label>Name<input value={values.name} onChange={(event) => update('name', event.target.value)} /></label>
    <label>Organization unit ID<input value={values.organizationUnitId} onChange={(event) => update('organizationUnitId', event.target.value)} placeholder="Defaults to the VMS-reported unit" /></label>
    <label>Geographic area ID<span aria-hidden="true"> *</span>
      <input aria-required="true" value={values.geographicAreaId} onChange={(event) => update('geographicAreaId', event.target.value)} />
    </label><FieldError id={`${rowKey(row)}-geographicAreaId-error`} message={errors.geographicAreaId} />
    <label>Camera type<span aria-hidden="true"> *</span>
      <select aria-required="true" value={values.cameraType} onChange={(event) => update('cameraType', event.target.value)}>
        <option value="">Select a camera type</option>
        {cameraTypes.map((type) => <option key={type} value={type}>{type}</option>)}
      </select>
    </label><FieldError id={`${rowKey(row)}-cameraType-error`} message={errors.cameraType} />
    <label>Latitude<span aria-hidden="true"> *</span>
      <input aria-required="true" inputMode="decimal" value={values.latitude} onChange={(event) => update('latitude', event.target.value)} />
    </label><FieldError id={`${rowKey(row)}-latitude-error`} message={errors.latitude} />
    <label>Longitude<span aria-hidden="true"> *</span>
      <input aria-required="true" inputMode="decimal" value={values.longitude} onChange={(event) => update('longitude', event.target.value)} />
    </label><FieldError id={`${rowKey(row)}-longitude-error`} message={errors.longitude} />
    <label>Azimuth<input inputMode="decimal" value={values.azimuth} onChange={(event) => update('azimuth', event.target.value)} /></label>
    <label>Horizontal field of view<input inputMode="decimal" value={values.horizontalFov} onChange={(event) => update('horizontalFov', event.target.value)} /></label>
    <label>Effective range<input inputMode="decimal" value={values.effectiveRange} onChange={(event) => update('effectiveRange', event.target.value)} /></label>
    {mutation.isError && <p className="form-error" role="alert">{errorDetail(mutation.error, 'The camera could not be registered.')}</p>}
    <Button disabled={mutation.isPending} type="button" onClick={submit}>{mutation.isPending ? 'Registering…' : 'Register and link'}</Button>
  </div>;
}

export function ReconciliationPage() {
  const { session } = useAuth();
  const queryClient = useQueryClient();
  const canCreate = hasPermission(session, 'camera.create');
  const [targetId, setTargetId] = useState('');
  const [expandedRow, setExpandedRow] = useState('');
  const [mode, setMode] = useState<'link' | 'create' | ''>('');

  const targets = useQuery({ queryKey: queryKeys.vms.all, queryFn: api.vms.list });
  const unreconciled = useQuery({
    queryKey: queryKeys.reconciliation.unreconciled(targetId),
    queryFn: () => api.reconciliation.unreconciled({ targetId: targetId || undefined }),
  });

  async function refresh() {
    setExpandedRow('');
    setMode('');
    await queryClient.invalidateQueries({ queryKey: queryKeys.reconciliation.unreconciledAll });
  }

  function toggle(row: UnreconciledCameraResponse, nextMode: 'link' | 'create') {
    const key = rowKey(row);
    if (expandedRow === key && mode === nextMode) {
      setExpandedRow('');
      setMode('');
    } else {
      setExpandedRow(key);
      setMode(nextMode);
    }
  }

  if (targets.isPending || unreconciled.isPending) {
    return <PageState title="Loading reconciliation backlog">Retrieving unmatched cameras…</PageState>;
  }
  if (unreconciled.isError) {
    return <><PageState title="Couldn&apos;t load the reconciliation backlog">{errorDetail(unreconciled.error, 'The reconciliation backlog could not be loaded.')}</PageState><button className="button" type="button" onClick={() => unreconciled.refetch()}>Try again</button></>;
  }

  const items = unreconciled.data.items;

  return <section className="reconciliation-page" aria-labelledby="reconciliation-title">
    <header>
      <p className="eyebrow">Camera registry</p>
      <h1 id="reconciliation-title">Reconciliation backlog</h1>
      <p>Cameras a VMS reports that the registry has never matched to a record.</p>
    </header>
    <label>Filter by VMS
      <select value={targetId} onChange={(event) => setTargetId(event.target.value)}>
        <option value="">All targets</option>
        {(targets.data ?? []).map((target) => <option key={target.id} value={target.id}>{target.displayName}</option>)}
      </select>
    </label>
    {items.length === 0 ? (
      <PageState title="Nothing to reconcile">Every discovered camera is already linked to a registry record.</PageState>
    ) : (
      <div className="camera-table-wrap"><table className="camera-table"><caption>{items.length} unreconciled camera{items.length === 1 ? '' : 's'}</caption><thead><tr>
        <th scope="col">Camera</th><th scope="col">Vendor facts</th><th scope="col">Organization unit</th>
        <th scope="col">Geographic area</th><th scope="col">Last seen</th><th scope="col">Reconcile</th>
      </tr></thead><tbody>{items.map((row) => {
        const key = rowKey(row);
        const expanded = expandedRow === key;
        return <Fragment key={key}>
          <tr>
            <td><strong>{displayName(row)}</strong><div>{row.nativeCameraId}</div></td>
            <td>{row.vendorModel ?? 'Not reported'}<div>Firmware: {row.firmware ?? 'Not reported'}</div></td>
            <td>{row.organizationUnitId}</td>
            <td>{row.geographicAreaId ?? 'Not reported'}</td>
            <td>{row.lastSeen ? new Date(row.lastSeen).toLocaleString() : 'Never'}</td>
            <td>
              <button aria-expanded={expanded && mode === 'link'} className="button button--secondary button--small" type="button" onClick={() => toggle(row, 'link')}>
                Link existing
              </button>
              {canCreate && <button aria-expanded={expanded && mode === 'create'} className="button button--secondary button--small" type="button" onClick={() => toggle(row, 'create')}>
                Register new
              </button>}
            </td>
          </tr>
          {expanded && mode === 'link' && <tr><td colSpan={6}>
            <LinkExistingForm row={row} onSuccess={refresh} />
          </td></tr>}
          {expanded && mode === 'create' && <tr><td colSpan={6}>
            <CreateFromFederatedForm row={row} onSuccess={refresh} />
          </td></tr>}
        </Fragment>;
      })}</tbody></table></div>
    )}
  </section>;
}
