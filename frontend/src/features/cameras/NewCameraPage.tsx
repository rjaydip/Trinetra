import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useNavigate } from 'react-router-dom';

import { api } from '../../api/endpoints';
import { PageState } from '../../components/ui';
import { CameraForm } from './CameraForm';

export function NewCameraPage() {
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const organizations = useQuery({ queryKey: ['reference', 'organizations'], queryFn: api.reference.organizations });
  const sites = useQuery({ queryKey: ['reference', 'sites'], queryFn: () => api.reference.sites() });
  const organizationUnits = useQuery({
    queryKey: ['reference', 'organization-units', organizations.data?.map((organization) => organization.id).join(',')],
    enabled: Boolean(organizations.data),
    queryFn: async () => (await Promise.all((organizations.data ?? []).map((organization) => api.reference.organizationUnits(organization.id)))).flat(),
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

  if (organizations.isError || organizationUnits.isError || sites.isError) return <section>
    <PageState title="Couldn&apos;t load onboarding options">Try again to retrieve the organization units and sites you can use.</PageState>
    <button className="button" type="button" onClick={() => {
      if (organizations.isError) void organizations.refetch();
      if (sites.isError) void sites.refetch();
      if (organizationUnits.isError) void organizationUnits.refetch();
    }}>Try again</button>
  </section>;
  if (organizations.isPending || organizationUnits.isPending || sites.isPending) return <PageState title="Loading onboarding options">Retrieving the organization units and sites you can use…</PageState>;

  return <section className="onboarding-page" aria-labelledby="new-camera-title"><header><p className="eyebrow">Camera registry</p><h1 id="new-camera-title">Register camera</h1><p>Required fields identify the camera and its physical location.</p></header><CameraForm organizationUnits={organizationUnits.data ?? []} sites={sites.data ?? []} onSubmit={async (values) => { await create.mutateAsync(values); }} /></section>;
}
