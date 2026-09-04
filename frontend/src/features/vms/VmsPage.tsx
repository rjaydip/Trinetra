import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { Link, useParams } from 'react-router-dom';

import { isApiProblem } from '../../api/client';
import { api } from '../../api/endpoints';
import type { ConnectorTargetRequest } from '../../api/models';
import { useAuth } from '../../auth/AuthProvider';
import { hasPermission } from '../../auth/permissions';
import { PageState, StatusBadge } from '../../components/ui';
import type { SelectorState } from '../cameras/CameraForm';
import { CredentialPanel } from './CredentialPanel';
import { VmsForm } from './VmsForm';

function errorDetail(error: unknown, fallback: string) {
  return isApiProblem(error) ? error.detail : fallback;
}

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
  const target = useQuery({ queryKey: ['vms', vmsId], queryFn: () => api.vms.get(vmsId) });
  const permissions = ['vms.read', 'credential.write', 'integration.manage'].filter((permission) => hasPermission(session, permission));

  if (target.isPending) return <PageState title="Loading VMS">Retrieving the selected integration…</PageState>;
  if (target.isError) return <><PageState title="Couldn&apos;t load VMS">{errorDetail(target.error, 'The selected VMS could not be loaded.')}</PageState><button className="button" type="button" onClick={() => target.refetch()}>Try again</button></>;

  return <section className="vms-detail-page" aria-labelledby="vms-detail-title">
    <Link className="back-link" to="/vms">Back to VMS integrations</Link>
    <header><div><p className="eyebrow">VMS integration</p><h1 id="vms-detail-title">{target.data.displayName}</h1><p>{target.data.code}</p></div><StatusBadge tone={target.data.state.toLowerCase() === 'active' ? 'success' : 'warning'}>{target.data.state}</StatusBadge></header>
    <section className="detail-panel" aria-labelledby="configuration-title">
      <h2 id="configuration-title">Configuration</h2>
      <dl>
        <div><dt>Vendor</dt><dd>{target.data.vendor}</dd></div>
        <div><dt>Endpoint</dt><dd>{target.data.endpoint}</dd></div>
        <div><dt>Runtime</dt><dd>{target.data.runtimeClass}</dd></div>
        <div><dt>TLS verification</dt><dd>{target.data.verifyTls ? 'Required' : 'Disabled'}</dd></div>
        <div><dt>Expected cameras</dt><dd>{target.data.expectedCameraCount ?? 'Not set'}</dd></div>
      </dl>
    </section>
    <CredentialPanel key={vmsId} permissions={permissions} vmsId={vmsId} />
  </section>;
}

export function VmsPage() {
  const { vmsId } = useParams();
  const { session } = useAuth();
  const queryClient = useQueryClient();
  const canCreate = hasPermission(session, 'vms.create');
  const [organizationId, setOrganizationId] = useState('');
  const [createdId, setCreatedId] = useState('');
  const vmsList = useQuery({ queryKey: ['vms'], queryFn: api.vms.list, enabled: !vmsId });
  const organizations = useQuery({ queryKey: ['reference', 'organizations'], queryFn: api.reference.organizations, enabled: canCreate && !vmsId });
  const sites = useQuery({ queryKey: ['reference', 'sites'], queryFn: () => api.reference.sites(), enabled: canCreate && !vmsId });
  const organizationUnits = useQuery({
    queryKey: ['reference', 'organization-units', organizationId],
    queryFn: () => api.reference.organizationUnits(organizationId),
    enabled: canCreate && !vmsId && Boolean(organizationId),
  });
  const create = useMutation({
    mutationFn: (request: ConnectorTargetRequest) => api.vms.create(request),
    onSuccess: async (response) => {
      setCreatedId(response.id);
      await queryClient.invalidateQueries({ queryKey: ['vms'] });
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
          : vmsList.data.length === 0 ? <PageState title="No VMS integrations">Register a target to begin secure onboarding.</PageState>
            : <ul>{vmsList.data.map((target) => <li key={target.id}><div><Link to={`/vms/${target.id}`}>{target.displayName}</Link><p>{target.code} · {target.vendor}</p></div><StatusBadge tone={target.state.toLowerCase() === 'active' ? 'success' : 'warning'}>{target.state}</StatusBadge></li>)}</ul>}
    </section>
    {canCreate && <section className="vms-registration" aria-labelledby="vms-registration-title">
      <header><p className="eyebrow">Step 1</p><h2 id="vms-registration-title">Register a VMS</h2><p>Save the target configuration first. Device credentials are entered on the next screen.</p></header>
      {createdId && <p className="save-confirmation" role="status">VMS registered. <Link to={`/vms/${createdId}`}>Continue to credentials</Link></p>}
      <VmsForm
        organizations={organizations.data ?? []}
        organizationUnits={organizationUnits.data ?? []}
        sites={sites.data ?? []}
        selectorStates={{
          organizations: selectorState(organizations, 'Organizations could not be loaded. Please try again.'),
          organizationUnits: organizationUnitsState,
          sites: selectorState(sites, 'Sites could not be loaded. Please try again.'),
        }}
        onOrganizationChange={setOrganizationId}
        onSubmit={async (values) => { setCreatedId(''); await create.mutateAsync(values); }}
      />
    </section>}
  </section>;
}
