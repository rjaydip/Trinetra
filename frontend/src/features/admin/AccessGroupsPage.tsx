import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { type FormEvent, useState } from 'react';

import { isApiProblem } from '../../api/client';
import { api } from '../../api/endpoints';
import type { AddScopeRequest, CreateGroupRequest, ScopeResponse } from '../../api/models';
import { useAuth } from '../../auth/AuthProvider';
import { hasPermission } from '../../auth/permissions';
import { Button, PageState, StatusBadge } from '../../components/ui';

function errorDetail(error: unknown, fallback: string) {
  return isApiProblem(error) ? error.detail : fallback;
}

function field(form: FormData, name: string) {
  return String(form.get(name) ?? '').trim();
}

function scopeDescription(scope: ScopeResponse) {
  if (scope.description) return scope.description;
  if (scope.scopeType === 'ORGANIZATION') return scope.organizationUnitId ?? 'Organization scope';
  if (scope.scopeType === 'GEOGRAPHY') return scope.geographicAreaId ?? 'Geography scope';
  return [scope.resourceType, scope.resourceId].filter(Boolean).join(' · ') || 'Resource scope';
}

function CreateGroupForm({ roles, onCreate, error, pending }: {
  roles: Array<{ id: string; name: string }>;
  onCreate(request: CreateGroupRequest): void;
  error: unknown;
  pending: boolean;
}) {
  function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const form = event.currentTarget;
    const data = new FormData(form);
    const description = field(data, 'description');
    onCreate({ code: field(data, 'code'), name: field(data, 'name'), roleId: field(data, 'roleId'), ...(description ? { description } : {}) });
    form.reset();
  }

  return <form aria-label="Create access group" className="admin-form" onSubmit={submit}>
    <h3>Create access group</h3>
    <p>New groups begin in draft while their scopes are assembled and reviewed.</p>
    <label>Code<input name="code" required /></label>
    <label>Name<input name="name" required /></label>
    <label>Role<select name="roleId" required><option value="">Select a role</option>{roles.map((role) => <option key={role.id} value={role.id}>{role.name}</option>)}</select></label>
    <label>Description<textarea name="description" /></label>
    {Boolean(error) && <p className="form-error" role="alert">{errorDetail(error, 'The access group could not be created.')}</p>}
    <Button disabled={pending} type="submit">Create access group</Button>
  </form>;
}

function ScopeForm({ groupId, areas, organizations, onAdded }: {
  groupId: string;
  areas: Array<{ id: string; name: string }>;
  organizations: Array<{ id: string; name: string }>;
  onAdded(): Promise<unknown> | void;
}) {
  const [scopeType, setScopeType] = useState<'ORGANIZATION' | 'GEOGRAPHY' | 'RESOURCE'>('ORGANIZATION');
  const [organizationId, setOrganizationId] = useState('');
  const units = useQuery({
    queryKey: ['admin', 'scope-organization-units', organizationId],
    queryFn: () => api.admin.organizations.listUnits(organizationId),
    enabled: scopeType === 'ORGANIZATION' && Boolean(organizationId),
  });
  const add = useMutation({ mutationFn: (request: AddScopeRequest) => api.admin.groups.addScope(groupId, request), onSuccess: onAdded });

  function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const form = event.currentTarget;
    const data = new FormData(form);
    let request: AddScopeRequest;
    if (scopeType === 'ORGANIZATION') request = { scopeType, organizationUnitId: field(data, 'organizationUnitId') };
    else if (scopeType === 'GEOGRAPHY') request = { scopeType, geographicAreaId: field(data, 'geographicAreaId') };
    else request = { scopeType, resourceType: field(data, 'resourceType'), resourceId: field(data, 'resourceId') };
    const description = field(data, 'description');
    if (description) request.description = description;
    add.mutate(request, { onSuccess: () => form.reset() });
  }

  return <form aria-label="Add access-group scope" className="admin-form scope-form" onSubmit={submit}>
    <h4>Add scope</h4>
    <label>Scope type<select name="scopeType" value={scopeType} onChange={(event) => setScopeType(event.target.value as typeof scopeType)}><option value="ORGANIZATION">Organization</option><option value="GEOGRAPHY">Geography</option><option value="RESOURCE">Resource</option></select></label>
    {scopeType === 'ORGANIZATION' && <>
      <label>Organization<select name="organizationId" value={organizationId} onChange={(event) => setOrganizationId(event.target.value)} required><option value="">Select an organization</option>{organizations.map((organization) => <option key={organization.id} value={organization.id}>{organization.name}</option>)}</select></label>
      <label>Organization unit<select name="organizationUnitId" disabled={!organizationId || units.isPending} required><option value="">Select an organization unit</option>{units.data?.map((unit) => <option key={unit.id} value={unit.id}>{unit.name}</option>)}</select></label>
    </>}
    {scopeType === 'GEOGRAPHY' && <label>Geographic area<select name="geographicAreaId" required><option value="">Select a geographic area</option>{areas.map((area) => <option key={area.id} value={area.id}>{area.name}</option>)}</select></label>}
    {scopeType === 'RESOURCE' && <div className="admin-coordinate-grid"><label>Resource type<input name="resourceType" required /></label><label>Resource ID<input name="resourceId" required /></label></div>}
    <label>Description<textarea name="description" /></label>
    {add.isError && <p className="form-error" role="alert">{errorDetail(add.error, 'The scope could not be added.')}</p>}
    <Button disabled={add.isPending} type="submit">Add scope</Button>
  </form>;
}

export function AccessGroupsPage() {
  const { session } = useAuth();
  const queryClient = useQueryClient();
  const canManage = hasPermission(session, 'group.manage');
  const [selectedId, setSelectedId] = useState('');
  const groups = useQuery({ queryKey: ['admin', 'access-groups'], queryFn: api.admin.groups.list });
  const detail = useQuery({ queryKey: ['admin', 'access-groups', selectedId], queryFn: () => api.admin.groups.get(selectedId), enabled: Boolean(selectedId) });
  const members = useQuery({ queryKey: ['admin', 'access-groups', selectedId, 'members'], queryFn: () => api.admin.groups.members(selectedId), enabled: Boolean(selectedId) });
  const roles = useQuery({ queryKey: ['admin', 'roles'], queryFn: api.admin.roles.list, enabled: canManage });
  const organizations = useQuery({ queryKey: ['admin', 'scope-organizations'], queryFn: api.admin.organizations.list, enabled: canManage });
  const areas = useQuery({ queryKey: ['admin', 'scope-areas'], queryFn: () => api.admin.geography.listAreas(), enabled: canManage });
  const create = useMutation({
    mutationFn: api.admin.groups.create,
    onSuccess: (created) => {
      setSelectedId(created.id);
      return queryClient.invalidateQueries({ queryKey: ['admin', 'access-groups'] });
    },
  });
  const removeScope = useMutation({
    mutationFn: ({ groupId, scopeId }: { groupId: string; scopeId: string }) => api.admin.groups.removeScope(groupId, scopeId),
    onSuccess: () => Promise.all([
      queryClient.invalidateQueries({ queryKey: ['admin', 'access-groups'] }),
      queryClient.invalidateQueries({ queryKey: ['admin', 'access-groups', selectedId] }),
    ]),
  });

  async function refreshSelected() {
    await Promise.all([
      queryClient.invalidateQueries({ queryKey: ['admin', 'access-groups'] }),
      queryClient.invalidateQueries({ queryKey: ['admin', 'access-groups', selectedId] }),
    ]);
  }

  return <section className="admin-workspace access-groups-page" aria-labelledby="access-groups-title">
    <header><div><p className="eyebrow">Scoped grants</p><h2 id="access-groups-title">Access groups</h2></div><p>A group pairs one role with the scopes where that role applies. Server authorization remains authoritative.</p></header>
    <div className="access-group-layout">
      <section className="admin-panel" aria-labelledby="group-list-title"><h3 id="group-list-title">Groups</h3>
        {groups.isPending ? <PageState title="Loading access groups">Retrieving authorized groups…</PageState>
          : groups.isError ? <PageState title="Couldn’t load access groups">{errorDetail(groups.error, 'Access groups could not be loaded.')}</PageState>
            : groups.data.length === 0 ? <p className="admin-empty">No access groups are available.</p>
              : <ul className="admin-record-list">{groups.data.map((group) => <li key={group.id}><div><strong>{group.name}</strong><span>{group.code} · {group.roleCode}</span><button className="admin-action-link" type="button" onClick={() => setSelectedId(group.id)}>View {group.name}</button></div><StatusBadge tone={group.status === 'ACTIVE' ? 'success' : 'warning'}>{group.status}</StatusBadge></li>)}</ul>}
      </section>
      {canManage && <CreateGroupForm error={create.error} onCreate={(request) => create.mutate(request)} pending={create.isPending} roles={roles.data ?? []} />}
    </div>

    {selectedId && <section className="admin-panel group-detail" aria-labelledby="group-detail-title">
      {detail.isPending ? <PageState title="Loading group detail">Retrieving scopes and permissions…</PageState>
        : detail.isError ? <PageState title="Couldn’t load group detail">{errorDetail(detail.error, 'The access group could not be loaded.')}</PageState>
          : <>
            <header><div><p className="eyebrow">{detail.data.code}</p><h3 id="group-detail-title">{detail.data.name}</h3><p>{detail.data.description ?? 'No description provided.'}</p></div><StatusBadge tone={detail.data.status === 'ACTIVE' ? 'success' : 'warning'}>{detail.data.status}</StatusBadge></header>
            <div className="group-detail-grid">
              <section aria-labelledby="group-permissions-title"><h4 id="group-permissions-title">Permissions from {detail.data.roleCode}</h4>{detail.data.permissions.length ? <ul className="token-list">{detail.data.permissions.map((permission) => <li key={permission}><code>{permission}</code></li>)}</ul> : <p className="admin-empty">This role has no permissions.</p>}</section>
              <section aria-labelledby="group-members-title"><h4 id="group-members-title">Members ({detail.data.memberCount})</h4>{members.isPending ? <p>Loading members…</p> : members.isError ? <p className="form-error">{errorDetail(members.error, 'Members could not be loaded.')}</p> : members.data?.length ? <ul className="plain-list">{members.data.map((member) => <li key={member.userId}><strong>{member.username}</strong>{member.expiresAt ? <span>Expires {new Date(member.expiresAt).toLocaleDateString()}</span> : <span>No expiry</span>}</li>)}</ul> : <p className="admin-empty">This group has no members.</p>}</section>
            </div>
            <section aria-labelledby="group-scopes-title"><header className="scope-header"><div><h4 id="group-scopes-title">Scopes</h4><p>Removing the last scope in a dimension can widen access; the server refuses unsafe changes.</p></div></header>{detail.data.scopes.length ? <ul className="scope-list">{detail.data.scopes.map((scope) => <li key={scope.id}><div><StatusBadge>{scope.scopeType}</StatusBadge><strong>{scopeDescription(scope)}</strong></div>{canManage && <button className="admin-action-link admin-action-link--danger" disabled={removeScope.isPending} type="button" onClick={() => removeScope.mutate({ groupId: detail.data.id, scopeId: scope.id })}>Remove scope {scopeDescription(scope)}</button>}</li>)}</ul> : <p className="admin-empty">No scopes constrain this group.</p>}{removeScope.isError && <p className="form-error" role="alert">{errorDetail(removeScope.error, 'The scope could not be removed.')}</p>}</section>
            {canManage && <ScopeForm key={detail.data.id} areas={areas.data ?? []} groupId={detail.data.id} onAdded={refreshSelected} organizations={organizations.data ?? []} />}
          </>}
    </section>}
  </section>;
}
