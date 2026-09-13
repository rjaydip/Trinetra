import { useMutation, useQuery } from '@tanstack/react-query';
import { useState, type FormEvent } from 'react';
import { useNavigate } from 'react-router-dom';

import { errorDetail, isApiProblem } from '../../api/client';
import { api } from '../../api/endpoints';
import type { ConnectorTargetRequest, VmsResponse } from '../../api/models';
import { queryKeys } from '../../api/queryKeys';
import { Button, StatusBadge } from '../../components/ui';
import { runtimeClasses, vendors } from './VmsForm';

const targetStates = ['Active', 'Quarantined', 'Disabled'] as const;

/**
 * `POST /vms/{id}/state` — this is what actually puts a target into or out of service; the
 * workers act on state, not on the configuration row.
 */
export function VmsStateControl({ target, onSuccess }: {
  target: VmsResponse;
  onSuccess(): Promise<unknown> | void;
}) {
  const mutation = useMutation({
    mutationFn: (state: typeof targetStates[number]) => api.vms.setState(target.id, { state }),
    onSuccess,
  });

  return <div className="vms-state-control">
    {targetStates.map((state) => <Button
      key={state}
      disabled={mutation.isPending || target.state === state}
      type="button"
      onClick={() => mutation.mutate(state)}
    >
      {state === 'Active' ? 'Activate' : state === 'Quarantined' ? 'Quarantine' : 'Disable'}
    </Button>)}
    {mutation.isError && <p className="form-error" role="alert">{errorDetail(mutation.error, 'The target state could not be changed.')}</p>}
  </div>;
}

/**
 * `DELETE /vms/{id}` — a hard delete cascading the discovered inventory, capability matrix,
 * health history and event cursor. The backend itself refuses this while the target is `Active`
 * (409), so the control stays disabled rather than let the admin discover that from an error.
 */
export function VmsDeleteControl({ target }: { target: VmsResponse }) {
  const navigate = useNavigate();
  const [confirming, setConfirming] = useState(false);
  const mutation = useMutation({
    mutationFn: () => api.vms.remove(target.id),
    onSuccess: () => navigate('/vms'),
  });
  const canDelete = target.state.toLowerCase() !== 'active';

  if (!confirming) {
    return <button
      className="button button--secondary"
      disabled={!canDelete}
      title={canDelete ? undefined : 'Set the target to Quarantined or Disabled before deleting it.'}
      type="button"
      onClick={() => setConfirming(true)}
    >
      Delete VMS
    </button>;
  }

  return <div className="deactivation-strategy">
    <p role="alert">Deleting {target.displayName} removes its discovered camera inventory, capability matrix, health history and event cursor. This cannot be undone.</p>
    {mutation.isError && <p className="form-error" role="alert">{errorDetail(mutation.error, 'The VMS could not be deleted.')}</p>}
    <div className="form-actions">
      <Button disabled={mutation.isPending} type="button" onClick={() => mutation.mutate()}>Confirm delete</Button>
      <button className="button button--secondary" type="button" onClick={() => setConfirming(false)}>Cancel</button>
    </div>
  </div>;
}

/**
 * `PUT /vms/{id}` — a full replacement, not a patch. Connection-tuning fields (rate limits, poll
 * intervals) aren't part of `VmsResponse`, so this form can't pre-fill them; per the endpoint's
 * own contract, omitting them here resets them to their defaults. Organization unit and
 * geographic area are entered as ids directly (no cascading picker) — consistent with how the
 * registry filters expose the same ids elsewhere in this app.
 */
export function VmsEditForm({ target, onSuccess }: {
  target: VmsResponse;
  onSuccess(): Promise<unknown> | void;
}) {
  const mutation = useMutation({
    mutationFn: (body: ConnectorTargetRequest) => api.vms.replace(target.id, body),
    onSuccess,
  });

  function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const data = new FormData(event.currentTarget);
    const value = (name: string) => String(data.get(name) ?? '').trim();
    const geographicAreaId = value('geographicAreaId');
    const runtimeClass = value('runtimeClass');

    mutation.mutate({
      code: value('code'),
      organizationUnitId: value('organizationUnitId'),
      displayName: value('displayName'),
      vendor: value('vendor'),
      endpoint: value('endpoint'),
      credentialReference: value('credentialReference'),
      geographicAreaId: geographicAreaId || undefined,
      verifyTls: data.get('verifyTls') === 'on',
      runtimeClass: runtimeClass || undefined,
    });
  }

  return <form aria-label={`Edit VMS ${target.displayName}`} className="admin-form" onSubmit={submit}>
    <p className="field-help">
      This replaces the whole configuration. Connection-tuning fields not shown here (rate
      limits, poll intervals) reset to their defaults if they were set.
    </p>
    <label>VMS code<input defaultValue={target.code} name="code" required /></label>
    <label>Display name<input defaultValue={target.displayName} name="displayName" required /></label>
    <label>Organization unit ID<input defaultValue={target.organizationUnitId} name="organizationUnitId" required /></label>
    <label>Geographic area ID<input defaultValue={target.geographicAreaId ?? ''} name="geographicAreaId" /></label>
    <label>Vendor
      <select defaultValue={target.vendor} name="vendor" required>
        {vendors.map((vendor) => <option key={vendor} value={vendor}>{vendor}</option>)}
      </select>
    </label>
    <label>Endpoint<input defaultValue={target.endpoint} name="endpoint" required /></label>
    <label>Credential reference<input defaultValue={target.credentialReference} name="credentialReference" required /></label>
    <label>Runtime class
      <select defaultValue={target.runtimeClass} name="runtimeClass">
        <option value="">Use server default</option>
        {runtimeClasses.map((runtimeClass) => <option key={runtimeClass} value={runtimeClass}>{runtimeClass}</option>)}
      </select>
    </label>
    <label className="checkbox-label"><input defaultChecked={target.verifyTls} name="verifyTls" type="checkbox" /> Verify TLS certificate</label>
    {mutation.isError && <p className="form-error" role="alert">{errorDetail(mutation.error, 'The VMS could not be updated.')}</p>}
    <Button disabled={mutation.isPending} type="submit">Save changes</Button>
  </form>;
}

/** `GET /vms/{id}/health` — what the workers have actually observed, newest first. */
export function VmsHealthPanel({ vmsId }: { vmsId: string }) {
  const health = useQuery({ queryKey: queryKeys.vms.health(vmsId), queryFn: () => api.vms.health(vmsId) });

  if (health.isPending) return <p>Loading health history…</p>;
  if (health.isError) return <p className="form-error">{errorDetail(health.error, 'Health history could not be loaded.')}</p>;
  if (health.data.length === 0) return <p className="admin-empty">No health checks recorded yet.</p>;

  return <div className="camera-table-wrap"><table className="camera-table"><thead><tr>
    <th>Checked at</th><th>Status</th><th>Latency</th><th>Cameras</th><th>Failures</th><th>Circuit</th><th>Cursor lag</th><th>Last error</th>
  </tr></thead><tbody>{health.data.map((check) => <tr key={check.checkedAt}>
    <td>{new Date(check.checkedAt).toLocaleString()}</td>
    <td><StatusBadge tone={check.status.toLowerCase() === 'ok' ? 'success' : 'warning'}>{check.status}</StatusBadge></td>
    <td>{check.latencyMs ?? 'Not recorded'}</td>
    <td>{check.cameraCount ?? 'Not recorded'}</td>
    <td>{check.consecutiveFailures}</td>
    <td>{check.circuitOpen ? 'Open' : 'Closed'}</td>
    <td>{check.cursorLagSeconds ?? 'Not recorded'}</td>
    <td>{check.lastError ?? 'None'}</td>
  </tr>)}</tbody></table></div>;
}

// Mirrors Federation.Core's [Flags] Capability enum — reference data, not fetched from anywhere,
// since the API returns only the resolved bitmask.
const capabilityBits: Array<[number, string]> = [
  [1 << 0, 'Inventory'], [1 << 1, 'Camera status'], [1 << 2, 'Streams'], [1 << 3, 'Recordings'],
  [1 << 4, 'Recording export'], [1 << 5, 'Events (pull)'], [1 << 6, 'Events (subscribe)'],
  [1 << 7, 'Metadata'], [1 << 8, 'PTZ'], [1 << 9, 'Snapshot'], [1 << 10, 'Time-sync check'],
];

/** `GET /vms/{id}/capabilities` — served from the stored matrix, never by probing the device. */
export function VmsCapabilitiesPanel({ vmsId }: { vmsId: string }) {
  const capabilities = useQuery({ queryKey: queryKeys.vms.capabilities(vmsId), queryFn: () => api.vms.capabilities(vmsId) });

  if (capabilities.isPending) return <p>Loading capabilities…</p>;
  if (capabilities.isError) {
    if (isApiProblem(capabilities.error) && capabilities.error.status === 404) {
      return <p className="admin-empty">No capability matrix yet — a worker hasn't reached this target.</p>;
    }
    return <p className="form-error">{errorDetail(capabilities.error, 'Capabilities could not be loaded.')}</p>;
  }

  const supportedNames = capabilityBits
    .filter(([bit]) => (capabilities.data.supported & bit) !== 0)
    .map(([, name]) => name);
  const notes = Object.entries(capabilities.data.notes);

  return <div>
    <p>Adapter {capabilities.data.adapterVersion} · probed {new Date(capabilities.data.probedAt).toLocaleString()}</p>
    {supportedNames.length ? <ul className="admin-record-list">{supportedNames.map((name) => <li key={name}>{name}</li>)}</ul>
      : <p className="admin-empty">No capabilities reported.</p>}
    {notes.length > 0 && <dl>{notes.map(([key, value]) => <div key={key}><dt>{key}</dt><dd>{value}</dd></div>)}</dl>}
  </div>;
}
