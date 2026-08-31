import type { ChangeEvent } from 'react';

import type { MapFilters as MapFiltersValue } from './geo';

export function MapFilters({ filters, onChange }: { filters: MapFiltersValue; onChange(filters: MapFiltersValue): void }) {
  const textField = (field: keyof MapFiltersValue) => (event: ChangeEvent<HTMLInputElement>) => {
    const value = event.target.value.trim();
    onChange({ ...filters, [field]: value || undefined });
  };

  return (
    <fieldset className="map-filters">
      <legend>Map filters</legend>
      <label>
        Organization unit ID
        <input value={filters.organizationUnitId ?? ''} onChange={textField('organizationUnitId')} />
      </label>
      <label>
        Operational status
        <input value={filters.operationalStatus ?? ''} onChange={textField('operationalStatus')} />
      </label>
      <label>
        Maintenance status
        <input value={filters.maintenanceStatus ?? ''} onChange={textField('maintenanceStatus')} />
      </label>
      <label>
        Camera type
        <input value={filters.cameraType ?? ''} onChange={textField('cameraType')} />
      </label>
      <label>
        Connectivity status
        <input value={filters.connectivityStatus ?? ''} onChange={textField('connectivityStatus')} />
      </label>
      <label className="checkbox-label">
        <input checked={Boolean(filters.coverage)} type="checkbox" onChange={(event) => onChange({ ...filters, coverage: event.target.checked })} />
        Show estimated coverage
      </label>
    </fieldset>
  );
}
