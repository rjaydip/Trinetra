import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { Link, useParams } from 'react-router-dom';

import { errorDetail } from '../../api/client';
import { api } from '../../api/endpoints';
import type { ConnectorTargetRequest } from '../../api/models';
import { queryKeys } from '../../api/queryKeys';
import { useAuth } from '../../auth/AuthProvider';
import { hasPermission } from '../../auth/permissions';
import { Button, Pager, PageState, StatusBadge } from '../../components/ui';
import type { SelectorState } from '../cameras/CameraForm';
import { CredentialPanel } from './CredentialPanel';
import { VmsCapabilitiesPanel, VmsDeleteControl, VmsEditForm, VmsHealthPanel, VmsStateControl } from './VmsManagement';
import { VmsForm, type VmsCredentialInput } from './VmsForm';
import { vmsStateTone } from './vmsTone';
import './vms.css';

function selectorState(query: {
  isPending: boolean;
  isError: boolean;
  isFetching: boolean;
  error: unknown;
  data?: unknown[];
  refetch(): unknown;
}, fallback: string): SelectorState {
  if (query.isPending || (query.isError && query.isFetching)) return { state: 'loading' };
  if (query.isError) return { state: 'error', message: errorDetail(query.error, fallback), retry: () => { void query.refetch(); } };
  return query.data?.length ? { state: 'ready' } : { state: 'empty' };
}

function VmsDetail({ vmsId }: { vmsId: string }) {
  const { session } = useAuth();
  const target = useQuery({ queryKey: queryKeys.vms.detail(vmsId), queryFn: () => api.vms.get(vmsId) });
  const permissions = ['vms.read', 'credential.write', 'integration.manage'].filter((permission) => hasPermission(session, permission));
  const canImportCameras = hasPermission(session, 'camera.import');
  const canUpdate = hasPermission(session, 'vms.update');
  const canDelete = hasPermission(session, 'vms.delete');
  const [editing, setEditing] = useState(false);
  const pollInventory = useMutation({
    mutationFn: () => api.vms.pollInventory(vmsId),
    onSuccess: () => target.refetch(),
  });
  const organizationUnit = useQuery({
    queryKey: queryKeys.reference.organizationUnit(target.data?.organizationUnitId ?? ''),
    queryFn: () => api.reference.organizationUnit(target.data!.organizationUnitId),
    enabled: Boolean(target.data?.organizationUnitId),
  });
  const geographicAreaId = target.data?.geographicAreaId;
  const geographicArea = useQuery({
    queryKey: queryKeys.reference.geographicArea(geographicAreaId ?? ''),
    queryFn: () => api.reference.geographicArea(geographicAreaId!),
    enabled: Boolean(geographicAreaId),
  });

  if (target.isPending) return <PageState title="Loading VMS">Retrieving the selected integration…</PageState>;
  if (target.isError) return <><PageState title="Couldn&apos;t load VMS">{errorDetail(target.error, 'The selected VMS could not be loaded.')}</PageState><button className="button" type="button" onClick={() => target.refetch()}>Try again</button></>;

  return <section className="vms-detail-page" aria-labelledby="vms-detail-title">
    <Link className="back-link" to="/vms">Back to VMS integrations</Link>
    <header><div><p className="eyebrow">VMS integration</p><h1 id="vms-detail-title">{target.data.displayName}</h1><p>{target.data.code}</p></div><StatusBadge tone={vmsStateTone(target.data.state)}>{target.data.state}</StatusBadge></header>
    <section className="detail-panel" aria-labelledby="configuration-title">
      <h2 id="configuration-title">Configuration</h2>
      {editing ? (
        <VmsEditForm target={target.data} onSuccess={async () => { setEditing(false); await target.refetch(); }} />
      ) : (
        <dl>
          <div><dt>Vendor</dt><dd>{target.data.vendor}</dd></div>
          <div><dt>Endpoint</dt><dd>{target.data.endpoint}</dd></div>
          <div><dt>Organization unit</dt><dd>{organizationUnit.data?.name ?? target.data.organizationUnitId}</dd></div>
          <div><dt>Geographic area</dt><dd>{geographicAreaId ? (geographicArea.data?.name ?? geographicAreaId) : 'Not set'}</dd></div>
          <div><dt>Runtime class</dt><dd>{target.data.runtimeClass}</dd></div>
          <div><dt>TLS verification</dt><dd>{target.data.verifyTls ? 'Required' : 'Disabled'}</dd></div>
          <div><dt>Expected cameras</dt><dd>{target.data.expectedCameraCount ?? 'Not set'}</dd></div>
        </dl>
      )}
      {canUpdate && <Button type="button" onClick={() => setEditing((open) => !open)}>{editing ? 'Cancel edit' : 'Edit configuration'}</Button>}
      {canImportCameras && <p><Link className="button button--secondary" to={`/vms/${vmsId}/discovery`}>Discover cameras</Link></p>}
      {canUpdate && <div className="inventory-poll-status">
        <p>
          {target.data.lastInventoryPollAt
            ? <>Inventory last refreshed {new Date(target.data.lastInventoryPollAt).toLocaleString()} ({target.data.lastInventoryCameraCount ?? 0} camera{target.data.lastInventoryCameraCount === 1 ? '' : 's'} reported).</>
            : 'Inventory has not been refreshed yet.'}
        </p>
        <button
          className="button button--secondary"
          disabled={pollInventory.isPending || target.data.state.toLowerCase() !== 'active'}
          title={target.data.state.toLowerCase() !== 'active' ? 'The target must be Active for a worker to act on the request.' : undefined}
          type="button"
          onClick={() => pollInventory.mutate()}
        >
          {pollInventory.isPending ? 'Requesting inventory refresh…' : 'Refresh inventory now'}
        </button>
        {pollInventory.isSuccess && <p className="save-confirmation" role="status">
          Inventory refresh requested. {pollInventory.data.circuitOpen ? 'The connection breaker is currently tripped, so this may take longer than usual.' : "It'll run on the worker's next cycle."}
        </p>}
        {pollInventory.isError && <p className="form-error" role="alert">{errorDetail(pollInventory.error, 'Unable to request an inventory refresh. Please try again.')}</p>}
      </div>}
    </section>
    {canUpdate && <section className="detail-panel" aria-labelledby="state-title">
      <h2 id="state-title">Polling state</h2>
      <VmsStateControl target={target.data} onSuccess={() => target.refetch()} />
    </section>}
    <section className="detail-panel" aria-labelledby="health-title">
      <h2 id="health-title">Connector health</h2>
      <VmsHealthPanel vmsId={vmsId} />
    </section>
    <section className="detail-panel" aria-labelledby="capabilities-title">
      <h2 id="capabilities-title">Capabilities</h2>
      <VmsCapabilitiesPanel vmsId={vmsId} />
    </section>
    <CredentialPanel key={vmsId} permissions={permissions} vmsId={vmsId} />
    {canDelete && <section className="detail-panel" aria-labelledby="danger-title">
      <h2 id="danger-title">Remove this target</h2>
      <VmsDeleteControl target={target.data} />
    </section>}
  </section>;
}

export function VmsPage() {
  const { vmsId } = useParams();
  const { session } = useAuth();
  const queryClient = useQueryClient();
  const canCreate = hasPermission(session, 'vms.create');
  const [organizationId, setOrganizationId] = useState('');
  const [createdId, setCreatedId] = useState('');
  const [page, setPage] = useState(1);
  const pageSize = 20;
  const vmsList = useQuery({
    queryKey: queryKeys.vms.page(page, pageSize),
    queryFn: () => api.vms.listPage({ page, pageSize }),
    enabled: !vmsId,
  });
  const organizations = useQuery({ queryKey: queryKeys.reference.organizations, queryFn: api.reference.organizations, enabled: canCreate && !vmsId });
  const geographicAreas = useQuery({ queryKey: queryKeys.reference.geographicAreas, queryFn: () => api.reference.geographicAreas(), enabled: canCreate && !vmsId });
  const organizationUnits = useQuery({
    queryKey: queryKeys.reference.organizationUnits(organizationId),
    queryFn: () => api.reference.organizationUnits(organizationId),
    enabled: canCreate && !vmsId && Boolean(organizationId),
  });
  const create = useMutation({
    mutationFn: async ({ request, credential }: { request: ConnectorTargetRequest; credential: VmsCredentialInput }) => {
      const response = await api.vms.create(request);
      if (credential.username || credential.password) {
        await api.credentials.save(response.id, credential);
      }
      return response;
    },
    onSuccess: async (response) => {
      setCreatedId(response.id);
      await queryClient.invalidateQueries({ queryKey: queryKeys.vms.all });
    },
  });

  if (vmsId) return <VmsDetail vmsId={vmsId} />;

  const organizationUnitsState: SelectorState = !organizationId
    ? { state: 'idle' }
    : selectorState(organizationUnits, 'Organization units could not be loaded. Please try again.');

  return <section className="vms-page" aria-labelledby="vms-page-title">
    <header><div><p className="eyebrow">Federation control</p><h1 id="vms-page-title">VMS integrations</h1></div><p>Register and verify the NVRs, gateways, and video management systems that feed the camera registry.</p></header>
    <section className="vms-list" aria-labelledby="vms-list-title">
      <h2 id="vms-list-title">Registered targets</h2>
      {vmsList.isPending ? <PageState title="Loading VMS integrations">Retrieving authorized targets…</PageState>
        : vmsList.isError ? <><PageState title="Couldn&apos;t load VMS integrations">{errorDetail(vmsList.error, 'VMS integrations could not be loaded.')}</PageState><button className="button" type="button" onClick={() => vmsList.refetch()}>Try again</button></>
          : vmsList.data.items.length === 0 ? <PageState title="No VMS integrations">Register a target to begin secure onboarding.</PageState>
            : <>
              <ul>{vmsList.data.items.map((target) => <li key={target.id}><div><Link to={`/vms/${target.id}`}>{target.displayName}</Link><p>{target.code} · {target.vendor}</p></div><StatusBadge tone={vmsStateTone(target.state)}>{target.state}</StatusBadge></li>)}</ul>
              <Pager page={vmsList.data.page} pageSize={vmsList.data.pageSize} total={vmsList.data.total} onPageChange={setPage} />
            </>}
    </section>
    {canCreate && <section className="vms-registration" aria-labelledby="vms-registration-title">
      <header>
        <div>
          <p className="eyebrow">Step 1</p>
          <h2 id="vms-registration-title">Register a VMS</h2>
        </div>
        <p>Connect to the device and test it, then fill in placement and vendor details. The credential is sealed once registration completes.</p>
      </header>
      {createdId && <p className="save-confirmation" role="status">VMS registered. <Link to={`/vms/${createdId}`}>View the new integration</Link></p>}
      <VmsForm
        organizations={organizations.data ?? []}
        organizationUnits={organizationUnits.data ?? []}
        geographicAreas={geographicAreas.data ?? []}
        selectorStates={{
          organizations: selectorState(organizations, 'Organizations could not be loaded. Please try again.'),
          organizationUnits: organizationUnitsState,
          geographicAreas: selectorState(geographicAreas, 'Geographic areas could not be loaded. Please try again.'),
        }}
        onOrganizationChange={setOrganizationId}
        onSubmit={async (request, credential) => { setCreatedId(''); await create.mutateAsync({ request, credential }); }}
      />
    </section>}
  </section>;
}
