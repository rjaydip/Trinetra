import type { ChangeEvent } from 'react';

import type { CameraListQuery } from '../../api/models';
import { GeographicAreaFilter } from './GeographicAreaFilter';
import { OrganizationUnitFilter } from './OrganizationUnitFilter';

export type RegistryFilters = Pick<CameraListQuery,
  'q' | 'cameraType' | 'organizationUnitId' | 'geographicAreaId' |
  'operationalStatus' | 'connectivityStatus' | 'maintenanceStatus' | 'includeRetired'>;

// Exact enum values the backend accepts (db/versions/v1.6.sql cameras table CHECK constraints) —
// not reverse-engineered from the StatusBadge tone-mapping, which groups all three fields'
// values together and would offer options that don't apply to a given field.
const OPERATIONAL_STATUSES = ['ONLINE', 'OFFLINE', 'DEGRADED', 'UNKNOWN'];
const CONNECTIVITY_STATUSES = ['CONNECTED', 'DISCONNECTED', 'UNKNOWN'];
const MAINTENANCE_STATUSES = ['NORMAL', 'REQUIRED', 'UNDER_MAINTENANCE', 'RETIRED'];

export function CameraFilters({ filters, onChange, onClear }: {
  filters: RegistryFilters;
  onChange(name: keyof RegistryFilters, value: string | boolean | undefined): void;
  onClear(): void;
}) {
  function update(event: ChangeEvent<HTMLInputElement | HTMLSelectElement>) {
    const { name, value } = event.target;
    const checked = 'checked' in event.target ? event.target.checked : undefined;
    const type = event.target.type;
    onChange(name as keyof RegistryFilters, type === 'checkbox' ? checked : value || undefined);
  }

  function clear() {
    onClear();
  }

  return (
    <fieldset className="registry-filters registry-filters--grouped">
      <legend>Registry filters</legend>
      <fieldset className="registry-filters__group">
        <legend>Search</legend>
        <label>Search cameras
          <input name="q" onChange={update} type="search" value={filters.q ?? ''} />
        </label>
        <label>Camera type
          <input name="cameraType" onChange={update} value={filters.cameraType ?? ''} />
        </label>
      </fieldset>
      <fieldset className="registry-filters__group">
        <legend>Status</legend>
        <label>Operational status
          <select name="operationalStatus" onChange={update} value={filters.operationalStatus ?? ''}>
            <option value="">Any</option>
            {OPERATIONAL_STATUSES.map((status) => <option key={status} value={status}>{status}</option>)}
          </select>
        </label>
        <label>Connectivity status
          <select name="connectivityStatus" onChange={update} value={filters.connectivityStatus ?? ''}>
            <option value="">Any</option>
            {CONNECTIVITY_STATUSES.map((status) => <option key={status} value={status}>{status}</option>)}
          </select>
        </label>
        <label>Maintenance status
          <select name="maintenanceStatus" onChange={update} value={filters.maintenanceStatus ?? ''}>
            <option value="">Any</option>
            {MAINTENANCE_STATUSES.map((status) => <option key={status} value={status}>{status}</option>)}
          </select>
        </label>
        <label className="checkbox-label">
          <input checked={filters.includeRetired ?? false} name="includeRetired" onChange={update} type="checkbox" />
          Include retired cameras
        </label>
      </fieldset>
      <fieldset className="registry-filters__group">
        <legend>Scope</legend>
        <OrganizationUnitFilter
          organizationUnitId={filters.organizationUnitId}
          onChange={(organizationUnitId) => onChange('organizationUnitId', organizationUnitId)}
        />
        <GeographicAreaFilter
          geographicAreaId={filters.geographicAreaId}
          onChange={(geographicAreaId) => onChange('geographicAreaId', geographicAreaId)}
        />
      </fieldset>
      <button className="button button--secondary" onClick={clear} type="button">Clear filters</button>
    </fieldset>
  );
}
