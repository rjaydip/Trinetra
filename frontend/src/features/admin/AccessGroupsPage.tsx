import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { type FormEvent, useState } from 'react';

import { errorDetail, isApiProblem } from '../../api/client';
import { api } from '../../api/endpoints';
import type { AccessGroupResponse, AddScopeRequest, CreateGroupRequest, GeographicAreaResponse, OrganizationUnitResponse, ScopeResponse } from '../../api/models';
import { queryKeys } from '../../api/queryKeys';
import { useAuth } from '../../auth/AuthProvider';
import { hasPermission } from '../../auth/permissions';
import { Button, Pager, PageState, StatusBadge } from '../../components/ui';
import { TreeSelect } from '../cameras/TreeSelect';
import './admin.css';

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
  areas: GeographicAreaResponse[];
  organizations: Array<{ id: string; name: string }>;
  onAdded(): Promise<unknown> | void;
}) {
  const [scopeType, setScopeType] = useState<'ORGANIZATION' | 'GEOGRAPHY' | 'RESOURCE'>('ORGANIZATION');
  const [organizationId, setOrganizationId] = useState('');
  const [organizationUnitId, setOrganizationUnitId] = useState('');
  const [geographicAreaId, setGeographicAreaId] = useState('');
  const units = useQuery({
    queryKey: queryKeys.admin.scopeOrganizationUnits(organizationId),
    queryFn: () => api.admin.organizations.listUnits(organizationId),
    enabled: scopeType === 'ORGANIZATION' && Boolean(organizationId),
  });
  const add = useMutation({ mutationFn: (request: AddScopeRequest) => api.admin.groups.addScope(groupId, request), onSuccess: onAdded });

  function changeScopeType(nextScopeType: typeof scopeType) {
    setScopeType(nextScopeType);
    setOrganizationId('');
    setOrganizationUnitId('');
    setGeographicAreaId('');
  }

  function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const form = event.currentTarget;
    const data = new FormData(form);
    let request: AddScopeRequest;
    if (scopeType === 'ORGANIZATION') request = { scopeType, organizationUnitId };
    else if (scopeType === 'GEOGRAPHY') request = { scopeType, geographicAreaId };
    else request = { scopeType, resourceType: field(data, 'resourceType'), resourceId: field(data, 'resourceId') };
    const description = field(data, 'description');
    if (description) request.description = description;
    add.mutate(request, {
      onSuccess: () => {
        form.reset();
        setOrganizationId('');
        setOrganizationUnitId('');
        setGeographicAreaId('');
      },
    });
  }

  return <form aria-label="Add access-group scope" className="admin-form scope-form" onSubmit={submit}>
    <h4>Add scope</h4>
    <label>Scope type<select name="scopeType" value={scopeType} onChange={(event) => changeScopeType(event.target.value as typeof scopeType)}><option value="ORGANIZATION">Organization</option><option value="GEOGRAPHY">Geography</option><option value="RESOURCE">Resource</option></select></label>
    {scopeType === 'ORGANIZATION' && <>
      <label>Organization<select name="organizationId" value={organizationId} onChange={(event) => { setOrganizationId(event.target.value); setOrganizationUnitId(''); }} required><option value="">Select an organization</option>{organizations.map((organization) => <option key={organization.id} value={organization.id}>{organization.name}</option>)}</select></label>
      <TreeSelect
        id="scope-form-organizationUnitId"
        label="Organization unit"
        items={units.data}
        getParentId={(unit) => unit.parentUnitId}
        value={organizationUnitId || undefined}
        onChange={(unitId) => setOrganizationUnitId(unitId ?? '')}
        disabled={!organizationId || units.isPending}
        loading={units.isPending}
        error={units.isError}
        required
        placeholder="Select an organization unit"
        emptyMessage="This organization has no units."
      />
    </>}
    {scopeType === 'GEOGRAPHY' && <TreeSelect
      id="scope-form-geographicAreaId"
      label="Geographic area"
      items={areas}
      getParentId={(area) => area.parentAreaId}
      value={geographicAreaId || undefined}
      onChange={(areaId) => setGeographicAreaId(areaId ?? '')}
      required
      placeholder="Select a geographic area"
      emptyMessage="No geographic areas are available."
    />}
    {scopeType === 'RESOURCE' && <div className="admin-coordinate-grid"><label>Resource type<input name="resourceType" required /></label><label>Resource ID<input name="resourceId" required /></label></div>}
    <label>Description<textarea name="description" /></label>
    {add.isError && <p className="form-error" role="alert">{errorDetail(add.error, 'The scope could not be added.')}</p>}
    <Button disabled={add.isPending} type="submit">Add scope</Button>
  </form>;
}

interface EditGroupFormProps {
  group: AccessGroupResponse;
  roles: Array<{ id: string; name: string; code?: string }>;
  onSave(request: { name: string; description?: string; roleId: string }): void;
  onCancel(): void;
  pending: boolean;
  error: unknown;
}

function EditGroupForm({ group, roles, onSave, onCancel, pending, error }: EditGroupFormProps) {
  const defaultRoleId = group.roleId ?? roles.find((r) => r.code === group.roleCode)?.id;

  function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const data = new FormData(event.currentTarget);
    const description = field(data, 'description');
    onSave({
      name: field(data, 'name'),
      roleId: field(data, 'roleId'),
      ...(description ? { description } : {}),
    });
  }

  return (
    <form aria-label="Edit access group" className="admin-form" onSubmit={submit}>
      <h4>Edit access group {group.name}</h4>
      <p>Group code <code>{group.code}</code> is established and cannot be changed.</p>
      <label>
        Name
        <input name="name" defaultValue={group.name} required />
      </label>
      <label>
        Role
        <select name="roleId" defaultValue={defaultRoleId} required>
          {roles.map((role) => (
            <option key={role.id} value={role.id}>
              {role.name}
            </option>
          ))}
        </select>
      </label>
      <label>
        Description
        <textarea name="description" defaultValue={group.description ?? ''} />
      </label>
      {Boolean(error) && (
        <p className="form-error" role="alert">
          {errorDetail(error, 'The access group could not be updated.')}
        </p>
      )}
      <div className="form-actions">
        <Button disabled={pending} type="submit">
          {pending ? 'Saving…' : 'Save changes'}
        </Button>
        <button className="button button--secondary" type="button" onClick={onCancel}>
          Cancel
        </button>
      </div>
    </form>
  );
}

export function AccessGroupsPage() {
  const { session } = useAuth();
  const queryClient = useQueryClient();
  const canManage = hasPermission(session, 'group.manage');
  const [selectedId, setSelectedId] = useState('');
  const [editing, setEditing] = useState(false);
  const [confirmUnscoped, setConfirmUnscoped] = useState(false);
  const [activationConflict, setActivationConflict] = useState('');
  const [page, setPage] = useState(1);
  const pageSize = 20;

  const groups = useQuery({
    queryKey: queryKeys.admin.accessGroupsPage(page, pageSize),
    queryFn: () => api.admin.groups.listPage({ page, pageSize }),
  });
  const detail = useQuery({ queryKey: queryKeys.admin.accessGroup(selectedId), queryFn: () => api.admin.groups.get(selectedId), enabled: Boolean(selectedId) });
  const [membersPage, setMembersPage] = useState(1);
  const membersPageSize = 20;
  const members = useQuery({
    queryKey: queryKeys.admin.accessGroupMembersPage(selectedId, membersPage, membersPageSize),
    queryFn: () => api.admin.groups.membersPage(selectedId, { page: membersPage, pageSize: membersPageSize }),
    enabled: Boolean(selectedId),
  });
  const roles = useQuery({ queryKey: queryKeys.admin.roles(), queryFn: () => api.admin.roles.list(), enabled: canManage });
  const organizations = useQuery({ queryKey: queryKeys.admin.scopeOrganizations, queryFn: api.admin.organizations.list, enabled: canManage });
  const areas = useQuery({ queryKey: queryKeys.admin.scopeAreas, queryFn: () => api.admin.geography.listAreas(), enabled: canManage });

  const create = useMutation({
    mutationFn: api.admin.groups.create,
    onSuccess: (created) => {
      setSelectedId(created.id);
      setMembersPage(1);
      return queryClient.invalidateQueries({ queryKey: queryKeys.admin.accessGroups });
    },
  });

  const update = useMutation({
    mutationFn: ({ id, body }: { id: string; body: { code: string; name: string; roleId: string; description?: string } }) => api.admin.groups.update(id, body),
    onSuccess: () => {
      setEditing(false);
      return refreshSelected();
    },
  });

  const activate = useMutation({
    mutationFn: ({ id, confirmUnscoped }: { id: string; confirmUnscoped?: boolean }) => (
      api.admin.groups.activate(id, confirmUnscoped ? { confirmUnscoped } : undefined)
    ),
    onSuccess: () => {
      setActivationConflict('');
      setConfirmUnscoped(false);
      return refreshSelected();
    },
    onError: (error) => {
      // Only the "estate-wide grant" 409 (an unconstrained org/geo dimension) is fixed by
      // confirmUnscoped — resending it for the other 409 this route can return ("Role is not
      // active", the group's role isn't ACTIVE) would just 409 again for the same unrelated
      // reason, since that check runs before the unconstrained-dimension check server-side.
      if (isApiProblem(error) && error.status === 409 && error.title === 'Confirm the estate-wide grant') {
        setActivationConflict(error.detail);
      } else {
        setActivationConflict('');
      }
    },
  });

  const disable = useMutation({
    mutationFn: (id: string) => api.admin.groups.disable(id),
    onSuccess: () => {
      setActivationConflict('');
      return refreshSelected();
    },
  });

  const removeScope = useMutation({
    mutationFn: ({ groupId, scopeId }: { groupId: string; scopeId: string }) => api.admin.groups.removeScope(groupId, scopeId),
    onSuccess: () => refreshSelected(),
  });

  async function refreshSelected() {
    await Promise.all([
      queryClient.invalidateQueries({ queryKey: queryKeys.admin.accessGroups }),
      queryClient.invalidateQueries({ queryKey: queryKeys.admin.accessGroup(selectedId) }),
    ]);
  }

  return <section className="admin-workspace access-groups-page" aria-labelledby="access-groups-title">
    <header><div><p className="eyebrow">Scoped grants</p><h2 id="access-groups-title">Access groups</h2></div><p>A group pairs one role with the scopes where that role applies. Server authorization remains authoritative.</p></header>
    <div className="access-group-layout">
      <section className="admin-panel" aria-labelledby="group-list-title"><h3 id="group-list-title">Groups</h3>
        {groups.isPending ? <PageState title="Loading access groups">Retrieving authorized groups…</PageState>
          : groups.isError ? <PageState title="Couldn’t load access groups">{errorDetail(groups.error, 'Access groups could not be loaded.')}</PageState>
            : groups.data.items.length === 0 ? <p className="admin-empty">No access groups are available.</p>
              : <>
                <ul className="admin-record-list">{groups.data.items.map((group) => <li key={group.id} className={group.id === selectedId ? 'admin-record--active' : ''}><div><strong>{group.name}</strong><span>{group.code} · {group.roleCode}</span><button className="admin-action-link" type="button" onClick={() => { setSelectedId(group.id); setEditing(false); setMembersPage(1); }}>View {group.name}</button></div><StatusBadge tone={group.status === 'ACTIVE' ? 'success' : 'warning'}>{group.status}</StatusBadge></li>)}</ul>
                <Pager page={groups.data.page} pageSize={groups.data.pageSize} total={groups.data.total} onPageChange={setPage} />
              </>}
      </section>
      {canManage && <CreateGroupForm error={create.error} onCreate={(request) => create.mutate(request)} pending={create.isPending} roles={roles.data ?? []} />}
    </div>

    {selectedId && <section className="admin-panel group-detail" aria-labelledby="group-detail-title">
      {detail.isPending ? <PageState title="Loading group detail">Retrieving scopes and permissions…</PageState>
        : detail.isError ? <PageState title="Couldn’t load group detail">{errorDetail(detail.error, 'The access group could not be loaded.')}</PageState>
          : <>
            <header>
              <div>
                <p className="eyebrow">{detail.data.code}</p>
                <h3 id="group-detail-title">{detail.data.name}</h3>
                <p>{detail.data.description ?? 'No description provided.'}</p>
              </div>
              <div className="group-detail-header-actions">
                <StatusBadge tone={detail.data.status === 'ACTIVE' ? 'success' : 'warning'}>{detail.data.status}</StatusBadge>
                {canManage && (
                  <div className="group-management-actions" style={{ marginTop: '.5rem', display: 'flex', gap: '.5rem' }}>
                    <button
                      className="button button--secondary button--small"
                      type="button"
                      onClick={() => setEditing((prev) => !prev)}
                    >
                      {editing ? 'Close edit' : 'Edit group'}
                    </button>
                    {detail.data.status === 'ACTIVE' ? (
                      <button
                        className="button button--secondary button--small"
                        disabled={disable.isPending}
                        type="button"
                        onClick={() => disable.mutate(detail.data.id)}
                      >
                        {disable.isPending ? 'Disabling…' : 'Disable group'}
                      </button>
                    ) : (
                      <button
                        className="button button--small"
                        disabled={activate.isPending}
                        type="button"
                        onClick={() => activate.mutate({ id: detail.data.id })}
                      >
                        {activate.isPending ? 'Activating…' : 'Activate group'}
                      </button>
                    )}
                  </div>
                )}
              </div>
            </header>

            {activationConflict && (
              <fieldset className="deactivation-strategy" style={{ marginTop: '1rem' }}>
                <legend>Estate-wide scope acknowledgement required</legend>
                <p role="alert">{activationConflict}</p>
                <label className="strategy-option">
                  <input
                    type="checkbox"
                    checked={confirmUnscoped}
                    onChange={(e) => setConfirmUnscoped(e.target.checked)}
                  />
                  I confirm this group is estate-wide and unrestricted on missing dimensions
                </label>
                <Button
                  disabled={!confirmUnscoped || activate.isPending}
                  type="button"
                  onClick={() => activate.mutate({ id: detail.data.id, confirmUnscoped: true })}
                >
                  Confirm estate-wide activation
                </Button>
              </fieldset>
            )}

            {activate.isError && !activationConflict && (
              <p className="form-error" role="alert">{errorDetail(activate.error, 'The group could not be activated.')}</p>
            )}
            {disable.isError && (
              <p className="form-error" role="alert">{errorDetail(disable.error, 'The group could not be disabled.')}</p>
            )}

            {editing && canManage && (
              <EditGroupForm
                group={detail.data}
                roles={roles.data ?? []}
                pending={update.isPending}
                error={update.error}
                onCancel={() => setEditing(false)}
                onSave={(fields) => update.mutate({ id: detail.data.id, body: { code: detail.data.code, ...fields } })}
              />
            )}

            <div className="group-detail-grid">
              <section aria-labelledby="group-permissions-title">
                <h4 id="group-permissions-title">Permissions from {detail.data.roleCode}</h4>
                {detail.data.permissions.length ? (
                  <ul className="token-list">{detail.data.permissions.map((permission) => <li key={permission}><code>{permission}</code></li>)}</ul>
                ) : (
                  <p className="admin-empty">This role has no permissions.</p>
                )}
              </section>
              <section aria-labelledby="group-members-title">
                <h4 id="group-members-title">Members ({detail.data.memberCount})</h4>
                {members.isPending ? (
                  <p>Loading members…</p>
                ) : members.isError ? (
                  <p className="form-error">{errorDetail(members.error, 'Members could not be loaded.')}</p>
                ) : members.data?.items.length ? (
                  <>
                    <ul className="plain-list">
                      {members.data.items.map((member) => (
                        <li key={member.userId}>
                          <strong>{member.username}</strong>
                          {member.expiresAt ? <span>Expires {new Date(member.expiresAt).toLocaleDateString()}</span> : <span>No expiry</span>}
                        </li>
                      ))}
                    </ul>
                    <Pager page={members.data.page} pageSize={members.data.pageSize} total={members.data.total} onPageChange={setMembersPage} />
                  </>
                ) : (
                  <p className="admin-empty">This group has no members.</p>
                )}
              </section>
            </div>
            <section aria-labelledby="group-scopes-title">
              <header className="scope-header">
                <div>
                  <h4 id="group-scopes-title">Scopes</h4>
                  <p>Removing the last scope in a dimension can widen access; the server refuses unsafe changes.</p>
                </div>
              </header>
              {detail.data.scopes.length ? (
                <ul className="scope-list">
                  {detail.data.scopes.map((scope) => (
                    <li key={scope.id}>
                      <div>
                        <StatusBadge>{scope.scopeType}</StatusBadge>
                        <strong>{scopeDescription(scope)}</strong>
                      </div>
                      {canManage && (
                        <button
                          className="admin-action-link admin-action-link--danger"
                          disabled={removeScope.isPending}
                          type="button"
                          onClick={() => removeScope.mutate({ groupId: detail.data.id, scopeId: scope.id })}
                        >
                          Remove scope {scopeDescription(scope)}
                        </button>
                      )}
                    </li>
                  ))}
                </ul>
              ) : (
                <p className="admin-empty">No scopes constrain this group.</p>
              )}
              {removeScope.isError && <p className="form-error" role="alert">{errorDetail(removeScope.error, 'The scope could not be removed.')}</p>}
            </section>
            {canManage && <ScopeForm key={detail.data.id} areas={areas.data ?? []} groupId={detail.data.id} onAdded={refreshSelected} organizations={organizations.data ?? []} />}
          </>}
    </section>}
  </section>;
}
