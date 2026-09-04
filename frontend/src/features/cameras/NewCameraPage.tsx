import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useNavigate } from 'react-router-dom';

import { isApiProblem } from '../../api/client';
import { api } from '../../api/endpoints';
import { PageState } from '../../components/ui';
import { CameraForm } from './CameraForm';

export function NewCameraPage() {
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const [organizationId, setOrganizationId] = useState('');
  const organizations = useQuery({ queryKey: ['reference', 'organizations'], queryFn: api.reference.organizations });
  const sites = useQuery({ queryKey: ['reference', 'sites'], queryFn: () => api.reference.sites() });
  const vms = useQuery({ queryKey: ['vms'], queryFn: api.vms.list });
  const organizationUnits = useQuery({
    queryKey: ['reference', 'organization-units', organizationId],
    enabled: Boolean(organizationId),
    queryFn: () => api.reference.organizationUnits(organizationId),
  });
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
  const onboardingError = organizations.isError ? organizations.error
    : organizationUnits.isError ? organizationUnits.error
      : sites.isError ? sites.error
        : vms.isError ? vms.error
          : undefined;

  if (organizations.isError || organizationUnits.isError || sites.isError || vms.isError) return <section>
    <PageState title="Couldn&apos;t load onboarding options">{isApiProblem(onboardingError) ? onboardingError.detail : 'Try again to retrieve the organizations, organization units, sites, and VMS records you can use.'}</PageState>
    <button className="button" type="button" onClick={() => {
      if (organizations.isError) void organizations.refetch();
      if (sites.isError) void sites.refetch();
      if (vms.isError) void vms.refetch();
      if (organizationUnits.isError) void organizationUnits.refetch();
    }}>Try again</button>
  </section>;
  if (organizations.isPending || sites.isPending || vms.isPending) return <PageState title="Loading onboarding options">Retrieving the organization, site, and VMS options you can use…</PageState>;

  return <section className="onboarding-page" aria-labelledby="new-camera-title"><header><p className="eyebrow">Camera registry</p><h1 id="new-camera-title">Register camera</h1><p>Required fields identify the camera and its physical location.</p></header><CameraForm organizations={organizations.data ?? []} organizationUnits={organizationUnits.data ?? []} sites={sites.data ?? []} vms={vms.data ?? []} onOrganizationChange={setOrganizationId} onSubmit={async (values) => { await create.mutateAsync(values); }} /></section>;
}
