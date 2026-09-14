import { useQuery } from '@tanstack/react-query';
import { useState } from 'react';

import { api } from '../../api/endpoints';
import { queryKeys } from '../../api/queryKeys';
import { TreeSelect } from './TreeSelect';
import './cameras.css';

/**
 * Organization → organization-unit filter. Organization narrows which unit tree is fetched (units
 * are only ever listed per-organization on the backend — there is no flat "all units" endpoint).
 * The organization choice is local UI state only — it narrows the tree, it is never sent to the
 * camera list query, which only ever filters by organizationUnitId.
 */
export function OrganizationUnitFilter({ organizationUnitId, onChange }: {
  organizationUnitId: string | undefined;
  onChange(organizationUnitId: string | undefined): void;
}) {
  const [organizationId, setOrganizationId] = useState('');

  const organizations = useQuery({ queryKey: queryKeys.reference.organizations, queryFn: ({ signal }) => api.reference.organizations(signal) });
  const units = useQuery({
    queryKey: queryKeys.reference.organizationUnits(organizationId),
    queryFn: ({ signal }) => api.reference.organizationUnits(organizationId, signal),
    enabled: Boolean(organizationId),
  });

  return (
    <div className="org-unit-filter">
      <label htmlFor="registry-organization">Organization
        <select
          id="registry-organization"
          value={organizationId}
          onChange={(event) => {
            setOrganizationId(event.target.value);
            onChange(undefined);
          }}
        >
          <option value="">Select an organization</option>
          {organizations.data?.map((organization) => <option key={organization.id} value={organization.id}>{organization.name}</option>)}
        </select>
      </label>
      <TreeSelect
        id="registry-org-unit"
        label="Organization unit"
        items={units.data}
        getParentId={(unit) => unit.parentUnitId}
        value={organizationUnitId}
        onChange={onChange}
        disabled={!organizationId}
        loading={units.isPending}
        error={units.isError}
        emptyMessage="This organization has no units."
      />
    </div>
  );
}
