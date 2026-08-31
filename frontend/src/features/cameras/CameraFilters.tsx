import type { ChangeEvent } from 'react';

import type { CameraListQuery } from '../../api/models';

export type RegistryFilters = Pick<CameraListQuery,
  'q' | 'cameraType' | 'organizationUnitId' | 'siteId' | 'geographicAreaId' |
  'operationalStatus' | 'connectivityStatus' | 'maintenanceStatus' | 'includeRetired'>;

export function CameraFilters({ filters, onChange, onClear }: {
  filters: RegistryFilters;
  onChange(name: keyof RegistryFilters, value: string | boolean | undefined): void;
  onClear(): void;
}) {
  function update(event: ChangeEvent<HTMLInputElement>) {
    const { checked, name, type, value } = event.target;
    onChange(name as keyof RegistryFilters, type === 'checkbox' ? checked : value || undefined);
  }

  function clear() {
    onClear();
  }

  return (
    <fieldset className="registry-filters">
      <legend>Registry filters</legend>
      <label>Search cameras
        <input name="q" onChange={update} type="search" value={filters.q ?? ''} />
      </label>
      <label>Camera type
        <input name="cameraType" onChange={update} value={filters.cameraType ?? ''} />
      </label>
      <label>Organization unit ID
        <input name="organizationUnitId" onChange={update} value={filters.organizationUnitId ?? ''} />
      </label>
      <label>Site ID
        <input name="siteId" onChange={update} value={filters.siteId ?? ''} />
      </label>
      <label>Geographic area ID
        <input name="geographicAreaId" onChange={update} value={filters.geographicAreaId ?? ''} />
      </label>
      <label>Operational status
        <input name="operationalStatus" onChange={update} value={filters.operationalStatus ?? ''} />
      </label>
      <label>Connectivity status
        <input name="connectivityStatus" onChange={update} value={filters.connectivityStatus ?? ''} />
      </label>
      <label>Maintenance status
        <input name="maintenanceStatus" onChange={update} value={filters.maintenanceStatus ?? ''} />
      </label>
      <label className="checkbox-label">
        <input checked={filters.includeRetired ?? false} name="includeRetired" onChange={update} type="checkbox" />
        Include retired cameras
      </label>
      <button className="button button--secondary" onClick={clear} type="button">Clear filters</button>
    </fieldset>
  );
}
