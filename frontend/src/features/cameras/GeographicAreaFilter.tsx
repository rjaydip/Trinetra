import { useQuery } from '@tanstack/react-query';

import { api } from '../../api/endpoints';
import { queryKeys } from '../../api/queryKeys';
import { TreeSelect } from './TreeSelect';

/** Geographic-area filter, tree-style like OrganizationUnitFilter. No scoping step first (unlike
 * organization units, geographic areas have a flat "all areas" endpoint), so the full hierarchy
 * loads directly. */
export function GeographicAreaFilter({ geographicAreaId, onChange }: {
  geographicAreaId: string | undefined;
  onChange(geographicAreaId: string | undefined): void;
}) {
  const areas = useQuery({ queryKey: queryKeys.reference.geographicAreas, queryFn: ({ signal }) => api.reference.geographicAreas(undefined, signal) });

  return (
    <TreeSelect
      id="registry-geographic-area"
      label="Geographic area"
      items={areas.data}
      getParentId={(area) => area.parentAreaId}
      value={geographicAreaId}
      onChange={onChange}
      loading={areas.isPending}
      error={areas.isError}
      emptyMessage="No geographic areas are available."
    />
  );
}
