import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { type FormEvent, useState } from 'react';

import { isApiProblem } from '../../api/client';
import { api } from '../../api/endpoints';
import type { DeactivateRequest } from '../../api/models';
import { useAuth } from '../../auth/AuthProvider';
import { hasPermission } from '../../auth/permissions';
import { Button, PageState, StatusBadge } from '../../components/ui';

function errorDetail(error: unknown, fallback: string) {
  return isApiProblem(error) ? error.detail : fallback;
}

function value(form: FormData, name: string) {
  return String(form.get(name) ?? '').trim();
}

function optional(valueToCheck: string) {
  return valueToCheck || undefined;
}

interface ParentOption { id: string; name: string }

function DeactivationControl({
  kind, itemId, itemName, parentLabel, parentOptions, deactivate, onSuccess,
}: {
  kind: 'area' | 'unit';
  itemId: string;
  itemName: string;
  parentLabel: string;
  parentOptions: ParentOption[];
  deactivate(request: DeactivateRequest): Promise<void>;
  onSuccess(): Promise<unknown> | void;
}) {
  const [conflict, setConflict] = useState('');
  const [strategy, setStrategy] = useState<'' | 'cascade' | 'reparent'>('');
  const [newParentId, setNewParentId] = useState('');
  const mutation = useMutation({
    mutationFn: deactivate,
    onSuccess: async () => {
      setConflict('');
      setStrategy('');
      setNewParentId('');
      await onSuccess();
    },
    onError: (error) => {
      if (isApiProblem(error) && error.status === 409) {
        setConflict(error.detail);
        setStrategy('');
        setNewParentId('');
      }
    },
  });
  const confirmDisabled = mutation.isPending || !strategy || (strategy === 'reparent' && !newParentId);

  return <div className="deactivation-control">
    <button className="admin-action-link" disabled={mutation.isPending || Boolean(conflict)} type="button" onClick={() => {
      setConflict('');
      setStrategy('');
      setNewParentId('');
      mutation.mutate({});
    }}>Deactivate {kind} {itemName}</button>
    {mutation.isError && !conflict && <p className="form-error" role="alert">{errorDetail(mutation.error, `The ${kind} could not be deactivated.`)}</p>}
    {conflict && <fieldset className="deactivation-strategy">
      <legend>Children require a decision</legend>
      <p role="alert">{conflict}</p>
      <label className="strategy-option"><input checked={strategy === 'cascade'} name={`${kind}-${itemId}-strategy`} onChange={() => { setStrategy('cascade'); setNewParentId(''); }} type="radio" /> Cascade</label>
      <label className="strategy-option"><input checked={strategy === 'reparent'} name={`${kind}-${itemId}-strategy`} onChange={() => setStrategy('reparent')} type="radio" /> Reparent</label>
      {strategy === 'reparent' && <label className="strategy-parent">{parentLabel}
        <select value={newParentId} onChange={(event) => setNewParentId(event.target.value)} required>
          <option value="">Select a new parent</option>
          {parentOptions.filter((parent) => parent.id !== itemId).map((parent) => <option key={parent.id} value={parent.id}>{parent.name}</option>)}
        </select>
      </label>}
      <Button disabled={confirmDisabled} type="button" onClick={() => mutation.mutate(strategy === 'cascade'
        ? { childStrategy: 'cascade' }
        : { childStrategy: 'reparent', newParentId })}>Confirm deactivation</Button>
    </fieldset>}
  </div>;
}

export function HierarchyPage() {
  const { session } = useAuth();
  const queryClient = useQueryClient();
  const canReadOrganizations = hasPermission(session, 'organization.read');
  const canManageOrganizations = hasPermission(session, 'organization.manage');
  const canReadGeography = hasPermission(session, 'geography.read');
  const canManageGeography = hasPermission(session, 'geography.manage');
  const showOrganizationDomain = canReadOrganizations || canManageOrganizations;
  const showGeographyDomain = canReadGeography || canManageGeography;
  const [organizationId, setOrganizationId] = useState('');

  const organizations = useQuery({
    queryKey: ['admin', 'organizations'], queryFn: api.admin.organizations.list, enabled: showOrganizationDomain,
  });
  const effectiveOrganizationId = organizationId || organizations.data?.[0]?.id || '';
  const units = useQuery({
    queryKey: ['admin', 'organization-units', effectiveOrganizationId],
    queryFn: () => api.admin.organizations.listUnits(effectiveOrganizationId),
    enabled: showOrganizationDomain && Boolean(effectiveOrganizationId),
  });
  const areas = useQuery({
    queryKey: ['admin', 'geographic-areas'], queryFn: () => api.admin.geography.listAreas(), enabled: showGeographyDomain,
  });
  const areaTypes = useQuery({
    queryKey: ['admin', 'geographic-area-types'], queryFn: api.admin.geography.listAreaTypes, enabled: showGeographyDomain && canManageGeography,
  });
  const sites = useQuery({
    queryKey: ['admin', 'sites'], queryFn: () => api.admin.geography.listSites(), enabled: showGeographyDomain,
  });

  const createOrganization = useMutation({
    mutationFn: api.admin.organizations.create,
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['admin', 'organizations'] }),
  });
  const createUnit = useMutation({
    mutationFn: ({ id, body }: { id: string; body: Parameters<typeof api.admin.organizations.createUnit>[1] }) => api.admin.organizations.createUnit(id, body),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['admin', 'organization-units'] }),
  });
  const createArea = useMutation({
    mutationFn: api.admin.geography.createArea,
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['admin', 'geographic-areas'] }),
  });
  const createSite = useMutation({
    mutationFn: api.admin.geography.createSite,
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['admin', 'sites'] }),
  });

  function submitOrganization(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const form = event.currentTarget;
    const data = new FormData(form);
    createOrganization.mutate({ code: value(data, 'code'), name: value(data, 'name'), organizationType: value(data, 'organizationType'), description: optional(value(data, 'description')) }, { onSuccess: () => form.reset() });
  }

  function submitUnit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const form = event.currentTarget;
    const data = new FormData(form);
    const selectedOrganizationId = value(data, 'organizationId');
    createUnit.mutate({ id: selectedOrganizationId, body: { organizationId: selectedOrganizationId, code: value(data, 'code'), name: value(data, 'name'), unitType: value(data, 'unitType'), parentUnitId: optional(value(data, 'parentUnitId')) } }, { onSuccess: () => form.reset() });
  }

  function submitArea(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const form = event.currentTarget;
    const data = new FormData(form);
    createArea.mutate({ code: value(data, 'code'), name: value(data, 'name'), areaType: value(data, 'areaType'), parentAreaId: optional(value(data, 'parentAreaId')) }, { onSuccess: () => form.reset() });
  }

  function submitSite(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const form = event.currentTarget;
    const data = new FormData(form);
    const latitude = value(data, 'latitude');
    const longitude = value(data, 'longitude');
    createSite.mutate({
      code: value(data, 'code'), name: value(data, 'name'), geographicAreaId: value(data, 'geographicAreaId'),
      siteType: optional(value(data, 'siteType')), address: optional(value(data, 'address')),
      latitude: latitude ? Number(latitude) : undefined, longitude: longitude ? Number(longitude) : undefined,
    }, { onSuccess: () => form.reset() });
  }

  return <section className="admin-workspace hierarchy-page" aria-labelledby="hierarchy-title">
    <header><div><p className="eyebrow">Owned structures</p><h2 id="hierarchy-title">Organization &amp; geography</h2></div><p>Build the two independent hierarchies that determine responsibility and physical reach.</p></header>

    {showOrganizationDomain && <section className="admin-domain" aria-labelledby="organizations-title">
      <header><div><h3 id="organizations-title">Organizations</h3><p>Agencies and their operational units.</p></div></header>
      {organizations.isPending ? <PageState title="Loading organizations">Retrieving authorized organizations…</PageState>
        : organizations.isError ? <PageState title="Couldn’t load organizations">{errorDetail(organizations.error, 'Organizations could not be loaded.')}</PageState>
          : organizations.data.length === 0 ? <p className="admin-empty">No organizations are available.</p>
            : <div className="hierarchy-layout"><div>
              <label className="admin-selector">Organization<select value={effectiveOrganizationId} onChange={(event) => setOrganizationId(event.target.value)}>{organizations.data.map((organization) => <option key={organization.id} value={organization.id}>{organization.name}</option>)}</select></label>
              <ul className="admin-record-list">{organizations.data.map((organization) => <li key={organization.id}><div><strong>{organization.name}</strong><span>{organization.code} · {organization.organizationType}</span></div><StatusBadge tone={organization.status === 'ACTIVE' ? 'success' : 'warning'}>{organization.status}</StatusBadge></li>)}</ul>
            </div><div><h4>Units</h4>{units.isPending ? <p>Loading units…</p> : units.isError ? <p className="form-error">{errorDetail(units.error, 'Units could not be loaded.')}</p> : units.data?.length ? <ul className="admin-record-list">{units.data.map((unit) => <li key={unit.id}><div><strong>{unit.name}</strong><span>{unit.code} · {unit.unitType}</span>{canManageOrganizations && <DeactivationControl kind="unit" itemId={unit.id} itemName={unit.name} parentLabel="New parent unit" parentOptions={units.data.map(({ id, name }) => ({ id, name }))} deactivate={(request) => api.admin.organizations.deactivateUnit(unit.id, request)} onSuccess={() => queryClient.invalidateQueries({ queryKey: ['admin', 'organization-units'] })} />}</div><StatusBadge tone={unit.status === 'ACTIVE' ? 'success' : 'warning'}>{unit.status}</StatusBadge></li>)}</ul> : <p className="admin-empty">No units are available for this organization.</p>}</div></div>}
      {canManageOrganizations && <div className="admin-form-grid">
        <form aria-label="Create organization" className="admin-form" onSubmit={submitOrganization}><h4>Create organization</h4><label>Code<input name="code" required /></label><label>Name<input name="name" required /></label><label>Organization type<input name="organizationType" required /></label><label>Description<textarea name="description" /></label>{createOrganization.isError && <p className="form-error" role="alert">{errorDetail(createOrganization.error, 'The organization could not be created.')}</p>}<Button disabled={createOrganization.isPending} type="submit">Create organization</Button></form>
        <form aria-label="Create organization unit" className="admin-form" onSubmit={submitUnit}><h4>Create organization unit</h4><label>Organization<select name="organizationId" required value={effectiveOrganizationId} onChange={(event) => setOrganizationId(event.target.value)}><option value="">Select an organization</option>{organizations.data?.map((organization) => <option key={organization.id} value={organization.id}>{organization.name}</option>)}</select></label><label>Code<input name="code" required /></label><label>Name<input name="name" required /></label><label>Unit type<input name="unitType" required /></label><label>Parent unit<select name="parentUnitId"><option value="">No parent</option>{units.data?.map((unit) => <option key={unit.id} value={unit.id}>{unit.name}</option>)}</select></label>{createUnit.isError && <p className="form-error" role="alert">{errorDetail(createUnit.error, 'The organization unit could not be created.')}</p>}<Button disabled={createUnit.isPending || !effectiveOrganizationId} type="submit">Create organization unit</Button></form>
      </div>}
    </section>}

    {showGeographyDomain && <section className="admin-domain" aria-labelledby="geography-title">
      <header><div><h3 id="geography-title">Geography</h3><p>Areas and the physical sites attached to them.</p></div></header>
      {areas.isPending ? <PageState title="Loading geographic areas">Retrieving authorized areas…</PageState>
        : areas.isError ? <PageState title="Couldn’t load geographic areas">{errorDetail(areas.error, 'Geographic areas could not be loaded.')}</PageState>
          : <div className="hierarchy-layout"><div><h4>Areas</h4>{areas.data.length ? <ul className="admin-record-list">{areas.data.map((area) => <li key={area.id}><div><strong>{area.name}</strong><span>{area.code} · {area.areaType}</span>{canManageGeography && <DeactivationControl kind="area" itemId={area.id} itemName={area.name} parentLabel="New parent area" parentOptions={areas.data.map(({ id, name }) => ({ id, name }))} deactivate={(request) => api.admin.geography.deactivateArea(area.id, request)} onSuccess={() => queryClient.invalidateQueries({ queryKey: ['admin', 'geographic-areas'] })} />}</div><StatusBadge tone={area.status === 'ACTIVE' ? 'success' : 'warning'}>{area.status}</StatusBadge></li>)}</ul> : <p className="admin-empty">No geographic areas are available.</p>}</div><div><h4>Sites</h4>{sites.isPending ? <p>Loading sites…</p> : sites.isError ? <p className="form-error">{errorDetail(sites.error, 'Sites could not be loaded.')}</p> : sites.data?.length ? <ul className="admin-record-list">{sites.data.map((site) => <li key={site.id}><div><strong>{site.name}</strong><span>{site.code}{site.siteType ? ` · ${site.siteType}` : ''}</span></div><StatusBadge tone={site.status === 'ACTIVE' ? 'success' : 'warning'}>{site.status}</StatusBadge></li>)}</ul> : <p className="admin-empty">No sites are available.</p>}</div></div>}
      {canManageGeography && <div className="admin-form-grid">
        <form aria-label="Create geographic area" className="admin-form" onSubmit={submitArea}><h4>Create geographic area</h4><label>Code<input name="code" required /></label><label>Name<input name="name" required /></label><label>Area type<select name="areaType" required><option value="">Select an area type</option>{areaTypes.data?.map((type) => <option key={type.code} value={type.code}>{type.name}</option>)}</select></label><label>Parent area<select name="parentAreaId"><option value="">No parent</option>{areas.data?.map((area) => <option key={area.id} value={area.id}>{area.name}</option>)}</select></label>{createArea.isError && <p className="form-error" role="alert">{errorDetail(createArea.error, 'The geographic area could not be created.')}</p>}<Button disabled={createArea.isPending} type="submit">Create geographic area</Button></form>
        <form aria-label="Create site" className="admin-form" onSubmit={submitSite}><h4>Create site</h4><label>Code<input name="code" required /></label><label>Name<input name="name" required /></label><label>Geographic area<select name="geographicAreaId" required><option value="">Select an area</option>{areas.data?.map((area) => <option key={area.id} value={area.id}>{area.name}</option>)}</select></label><label>Site type<input name="siteType" /></label><label>Address<textarea name="address" /></label><div className="admin-coordinate-grid"><label>Latitude<input max="90" min="-90" name="latitude" step="any" type="number" /></label><label>Longitude<input max="180" min="-180" name="longitude" step="any" type="number" /></label></div>{createSite.isError && <p className="form-error" role="alert">{errorDetail(createSite.error, 'The site could not be created.')}</p>}<Button disabled={createSite.isPending} type="submit">Create site</Button></form>
      </div>}
    </section>}
  </section>;
}
