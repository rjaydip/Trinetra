import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { type FormEvent, useEffect, useState } from 'react';

import { errorDetail } from '../../api/client';
import { api } from '../../api/endpoints';
import type { AssignGroupRequest, CreateUserRequest, UpdateUserRequest } from '../../api/models';
import { queryKeys } from '../../api/queryKeys';
import { useAuth } from '../../auth/AuthProvider';
import { hasPermission } from '../../auth/permissions';
import { Button, Pager, PageState, StatusBadge } from '../../components/ui';
import { TreeSelect } from '../cameras/TreeSelect';
import { useDocumentTitle } from '../../lib/useDocumentTitle';
import './admin.css';

function field(form: FormData, name: string) {
  return String(form.get(name) ?? '').trim();
}

/** A user's home organization unit / geographic area is descriptive HR metadata only — who they
 * actually belong to and where they cover — never an access-control input; that still comes
 * entirely from the Access Groups a user is a member of (role + scope). See the `v1.21` schema
 * comment on `platform_users.organization_unit_id`/`geographic_area_id` for the same note
 * server-side. Reuses `TreeSelect` (organization units are scoped to one organization at a time,
 * same as everywhere else this hierarchy is edited — e.g. `CameraForm`, `HierarchyPage`). */
interface OrgGeoDesignation {
  organizationUnitId: string;
  geographicAreaId: string;
  designation: string;
}

function OrgGeoDesignationFields({ value, onChange, idPrefix }: {
  value: OrgGeoDesignation;
  onChange(next: OrgGeoDesignation): void;
  idPrefix: string;
}) {
  const [organizationId, setOrganizationId] = useState('');

  const organizations = useQuery({ queryKey: queryKeys.reference.organizations, queryFn: ({ signal }) => api.reference.organizations(signal) });
  const geographicAreas = useQuery({ queryKey: queryKeys.reference.geographicAreas, queryFn: ({ signal }) => api.reference.geographicAreas(undefined, signal) });
  const organizationUnits = useQuery({
    queryKey: queryKeys.reference.organizationUnits(organizationId),
    queryFn: ({ signal }) => api.reference.organizationUnits(organizationId, signal),
    enabled: Boolean(organizationId),
  });
  // Editing an existing user: their saved unit may belong to an organization not yet selected
  // above — resolve it once so the organization step (and the unit tree under it) starts
  // pre-filled instead of forcing a re-pick of something already chosen.
  const currentUnit = useQuery({
    queryKey: queryKeys.reference.organizationUnit(value.organizationUnitId),
    queryFn: ({ signal }) => api.reference.organizationUnit(value.organizationUnitId, signal),
    enabled: Boolean(value.organizationUnitId) && !organizationId,
  });

  useEffect(() => {
    if (currentUnit.data && !organizationId) setOrganizationId(currentUnit.data.organizationId);
  }, [currentUnit.data, organizationId]);

  return <fieldset className="org-geo-designation-fields">
    <legend>Home assignment <span className="org-geo-designation-fields__hint">(descriptive only — access comes from group membership below, not from this)</span></legend>
    <label htmlFor={`${idPrefix}-organizationId`}>Home organization
      <select
        id={`${idPrefix}-organizationId`} value={organizationId}
        onChange={(event) => {
          setOrganizationId(event.target.value);
          onChange({ ...value, organizationUnitId: '' }); // the unit belonged to the previous organization.
        }}
      >
        <option value="">Not set</option>
        {(organizations.data ?? []).map((org) => <option key={org.id} value={org.id}>{org.name}</option>)}
      </select>
    </label>
    <TreeSelect
      id={`${idPrefix}-organizationUnitId`}
      label="Home organization unit"
      items={organizationUnits.data}
      getParentId={(unit) => unit.parentUnitId}
      value={value.organizationUnitId || undefined}
      onChange={(id) => onChange({ ...value, organizationUnitId: id ?? '' })}
      disabled={!organizationId}
      loading={organizationUnits.isFetching}
      error={organizationUnits.isError}
      emptyMessage="No organization units are available for this organization."
      placeholder="Not set"
    />
    <TreeSelect
      id={`${idPrefix}-geographicAreaId`}
      label="Home geographic area"
      items={geographicAreas.data}
      getParentId={(area) => area.parentAreaId}
      value={value.geographicAreaId || undefined}
      onChange={(id) => onChange({ ...value, geographicAreaId: id ?? '' })}
      loading={geographicAreas.isFetching}
      error={geographicAreas.isError}
      emptyMessage="No geographic areas are available."
      placeholder="Not set"
    />
    <label htmlFor={`${idPrefix}-designation`}>Designation
      <input
        id={`${idPrefix}-designation`} maxLength={150}
        value={value.designation}
        onChange={(event) => onChange({ ...value, designation: event.target.value })}
        placeholder="e.g. Station Inspector"
      />
    </label>
  </fieldset>;
}

function CreateUserForm({ onCreate, error, pending }: {
  onCreate(request: CreateUserRequest): void;
  error: unknown;
  pending: boolean;
}) {
  const [orgGeo, setOrgGeo] = useState<OrgGeoDesignation>({ organizationUnitId: '', geographicAreaId: '', designation: '' });

  function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const form = event.currentTarget;
    const data = new FormData(form);
    const email = field(data, 'email');
    onCreate({
      username: field(data, 'username'),
      displayName: field(data, 'displayName'),
      password: field(data, 'password'),
      ...(email ? { email } : {}),
      ...(orgGeo.organizationUnitId ? { organizationUnitId: orgGeo.organizationUnitId } : {}),
      ...(orgGeo.geographicAreaId ? { geographicAreaId: orgGeo.geographicAreaId } : {}),
      ...(orgGeo.designation.trim() ? { designation: orgGeo.designation.trim() } : {}),
    });
    form.reset();
    setOrgGeo({ organizationUnitId: '', geographicAreaId: '', designation: '' });
  }

  return <form aria-label="Create user" className="admin-form" onSubmit={submit}>
    <h3>Create user</h3>
    <p>A new account holds no permissions until it is added to an access group — the fields below are descriptive (which department/area this person belongs to), not access control.</p>
    <label>Username<input name="username" required /></label>
    <label>Display name<input name="displayName" required /></label>
    <label>Initial password<input name="password" type="password" required /></label>
    <label>Email<input name="email" type="email" /></label>
    <OrgGeoDesignationFields idPrefix="create-user" value={orgGeo} onChange={setOrgGeo} />
    {Boolean(error) && <p className="form-error" role="alert">{errorDetail(error, 'The user could not be created.')}</p>}
    <Button disabled={pending} type="submit">Create user</Button>
  </form>;
}

function UserDetail({ userId, canManage, onClose }: {
  userId: string;
  canManage: boolean;
  onClose(): void;
}) {
  const queryClient = useQueryClient();
  const [editing, setEditing] = useState(false);
  const [orgGeo, setOrgGeo] = useState<OrgGeoDesignation>({ organizationUnitId: '', geographicAreaId: '', designation: '' });
  const [newPassword, setNewPassword] = useState('');
  const [groupId, setGroupId] = useState('');

  const detail = useQuery({ queryKey: queryKeys.users.detail(userId), queryFn: ({ signal }) => api.admin.users.get(userId, signal) });
  const groups = useQuery({ queryKey: queryKeys.users.groups(userId), queryFn: ({ signal }) => api.admin.users.groups(userId, signal) });
  const permissions = useQuery({ queryKey: queryKeys.users.permissions(userId), queryFn: ({ signal }) => api.admin.users.permissions(userId, signal) });
  const allGroups = useQuery({ queryKey: queryKeys.admin.accessGroups, queryFn: ({ signal }) => api.admin.groups.list(signal), enabled: canManage });

  async function refresh() {
    await Promise.all([
      queryClient.invalidateQueries({ queryKey: queryKeys.users.detail(userId) }),
      queryClient.invalidateQueries({ queryKey: queryKeys.users.groups(userId) }),
      queryClient.invalidateQueries({ queryKey: queryKeys.users.permissions(userId) }),
      queryClient.invalidateQueries({ queryKey: queryKeys.users.allPages }),
    ]);
  }

  const update = useMutation({
    mutationFn: (body: UpdateUserRequest) => api.admin.users.update(userId, body),
    onSuccess: async () => { setEditing(false); await refresh(); },
  });

  const resetPassword = useMutation({
    mutationFn: () => api.admin.users.resetPassword(userId, { newPassword }),
    onSuccess: () => setNewPassword(''),
  });

  const addToGroup = useMutation({
    mutationFn: (body: AssignGroupRequest) => api.admin.users.addToGroup(userId, body),
    onSuccess: async () => { setGroupId(''); await refresh(); },
  });

  const removeFromGroup = useMutation({
    mutationFn: (removeGroupId: string) => api.admin.users.removeFromGroup(userId, removeGroupId),
    onSuccess: () => refresh(),
  });

  if (detail.isPending) return <PageState title="Loading user">Retrieving the selected account…</PageState>;
  if (detail.isError) return <><PageState title="Couldn&apos;t load user">{errorDetail(detail.error, 'The selected user could not be loaded.')}</PageState><button className="button" type="button" onClick={() => detail.refetch()}>Try again</button></>;

  const user = detail.data;
  const memberGroupIds = new Set((groups.data ?? []).map((g) => g.groupId));
  const availableGroups = (allGroups.data ?? []).filter((g) => !memberGroupIds.has(g.id));

  return <section className="detail-panel" aria-labelledby="user-detail-title">
    <button className="button button--secondary detail-panel__back" type="button" onClick={onClose}>&larr; Back to users</button>
    <header><h3 id="user-detail-title">{user.displayName}</h3><StatusBadge tone={user.status === 'ACTIVE' ? 'success' : 'warning'}>{user.status}</StatusBadge></header>
    {editing ? (
      <form aria-label="Edit user" className="admin-form admin-form--inline" onSubmit={(event) => {
        event.preventDefault();
        const data = new FormData(event.currentTarget);
        const email = field(data, 'email');
        update.mutate({
          displayName: field(data, 'displayName'), email: email || undefined, status: field(data, 'status'),
          organizationUnitId: orgGeo.organizationUnitId || undefined,
          geographicAreaId: orgGeo.geographicAreaId || undefined,
          designation: orgGeo.designation.trim() || undefined,
        });
      }}>
        <label>Display name<input name="displayName" defaultValue={user.displayName} required /></label>
        <label>Email<input name="email" type="email" defaultValue={user.email ?? ''} /></label>
        <label>Status<select name="status" defaultValue={user.status}>
          <option value="ACTIVE">Active</option>
          <option value="INACTIVE">Inactive</option>
          <option value="LOCKED">Locked</option>
        </select></label>
        <OrgGeoDesignationFields idPrefix={`edit-user-${userId}`} value={orgGeo} onChange={setOrgGeo} />
        {update.isError && <p className="form-error" role="alert">{errorDetail(update.error, 'The user could not be updated.')}</p>}
        <div className="form-actions">
          <Button disabled={update.isPending} type="submit">Save changes</Button>
          <button className="button button--secondary" type="button" onClick={() => setEditing(false)}>Cancel</button>
        </div>
      </form>
    ) : (
      <dl>
        <div><dt>Username</dt><dd>{user.username}</dd></div>
        <div><dt>Email</dt><dd>{user.email ?? 'Not set'}</dd></div>
        <div><dt>Designation</dt><dd>{user.designation ?? 'Not set'}</dd></div>
        <div><dt>Home organization unit</dt><dd>{user.organizationUnitName ?? 'Not set'}</dd></div>
        <div><dt>Home geographic area</dt><dd>{user.geographicAreaName ?? 'Not set'}</dd></div>
        <div><dt>Must change password</dt><dd>{user.mustChangePassword ? 'Yes' : 'No'}</dd></div>
        <div><dt>Last login</dt><dd>{user.lastLoginAt ? new Date(user.lastLoginAt).toLocaleString() : 'Never'}</dd></div>
      </dl>
    )}
    {canManage && !editing && (
      <Button type="button" onClick={() => {
        setOrgGeo({
          organizationUnitId: user.organizationUnitId ?? '',
          geographicAreaId: user.geographicAreaId ?? '',
          designation: user.designation ?? '',
        });
        setEditing(true);
      }}>
        Edit user
      </Button>
    )}

    {canManage && <section aria-labelledby="reset-password-title">
      <h4 id="reset-password-title">Reset password</h4>
      <form aria-label="Reset password" className="admin-form admin-form--inline" onSubmit={(event) => { event.preventDefault(); resetPassword.mutate(); }}>
        <label>New password<input name="newPassword" type="password" required value={newPassword} onChange={(event) => setNewPassword(event.target.value)} /></label>
        {resetPassword.isError && <p className="form-error" role="alert">{errorDetail(resetPassword.error, 'The password could not be reset.')}</p>}
        {resetPassword.isSuccess && <p role="status">Password reset. The account must change it at next sign-in.</p>}
        <Button disabled={resetPassword.isPending || !newPassword} type="submit">Reset password</Button>
      </form>
    </section>}

    <section aria-labelledby="user-groups-title">
      <h4 id="user-groups-title">Group memberships</h4>
      {groups.isPending ? <p>Loading memberships…</p>
        : groups.isError ? <p className="form-error">{errorDetail(groups.error, 'Group memberships could not be loaded.')}</p>
          : groups.data.length === 0 ? <p className="admin-empty">This account belongs to no access groups.</p>
            : <ul className="plain-list">{groups.data.map((membership) => <li key={membership.groupId}>
              <strong>{membership.name}</strong> <span>{membership.code}</span>
              {membership.expiresAt && <span> · Expires {new Date(membership.expiresAt).toLocaleDateString()}</span>}
              {canManage && <button className="admin-action-link" type="button" onClick={() => removeFromGroup.mutate(membership.groupId)}>Remove</button>}
            </li>)}</ul>}
      {removeFromGroup.isError && <p className="form-error" role="alert">{errorDetail(removeFromGroup.error, 'The user could not be removed from the group.')}</p>}
      {canManage && <form aria-label="Add to access group" className="admin-form admin-form--inline" onSubmit={(event) => {
        event.preventDefault();
        if (groupId) addToGroup.mutate({ groupId });
      }}>
        <label>Add to group
          <select value={groupId} onChange={(event) => setGroupId(event.target.value)} required>
            <option value="">Select a group</option>
            {availableGroups.map((group) => <option key={group.id} value={group.id}>{group.name} ({group.code})</option>)}
          </select>
        </label>
        {addToGroup.isError && <p className="form-error" role="alert">{errorDetail(addToGroup.error, 'The user could not be added to the group.')}</p>}
        <Button disabled={addToGroup.isPending || !groupId} type="submit">Add to group</Button>
      </form>}
    </section>

    <section aria-labelledby="user-permissions-title">
      <h4 id="user-permissions-title">Effective permissions</h4>
      {permissions.isPending ? <p>Loading permissions…</p>
        : permissions.isError ? <p className="form-error">{errorDetail(permissions.error, 'Permissions could not be loaded.')}</p>
          : permissions.data.length === 0 ? <p className="admin-empty">This account holds no permissions.</p>
            : <ul className="token-list">{permissions.data.map((permission) => <li key={permission}><code>{permission}</code></li>)}</ul>}
    </section>
  </section>;
}

export function UsersPage() {
  useDocumentTitle('Users');
  const { session } = useAuth();
  const queryClient = useQueryClient();
  const canManage = hasPermission(session, 'user.manage');
  const [selectedId, setSelectedId] = useState('');
  const [page, setPage] = useState(1);
  const pageSize = 20;

  const users = useQuery({
    queryKey: queryKeys.users.page(page, pageSize),
    queryFn: ({ signal }) => api.admin.users.listPage({ page, pageSize }, signal),
    enabled: !selectedId,
  });

  const create = useMutation({
    mutationFn: (body: CreateUserRequest) => api.admin.users.create(body),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: queryKeys.users.allPages }),
  });

  if (selectedId) return <section className="admin-workspace users-page" aria-labelledby="users-title">
    <header><div><p className="eyebrow">Accounts</p><h2 id="users-title">Users</h2></div></header>
    <UserDetail canManage={canManage} userId={selectedId} onClose={() => setSelectedId('')} />
  </section>;

  return <section className="admin-workspace users-page" aria-labelledby="users-title">
    <header><div><p className="eyebrow">Accounts</p><h2 id="users-title">Users</h2></div><p>A new account holds no permissions until it is added to an access group.</p></header>
    <section aria-labelledby="user-list-title">
      <h3 id="user-list-title">Accounts</h3>
      {users.isPending ? <PageState title="Loading users">Retrieving authorized accounts…</PageState>
        : users.isError ? <><PageState title="Couldn&apos;t load users">{errorDetail(users.error, 'Users could not be loaded.')}</PageState><button className="button" type="button" onClick={() => users.refetch()}>Try again</button></>
          : users.data.items.length === 0 ? <p className="admin-empty">No user accounts are available.</p>
            : <>
              <ul className="admin-record-list">{users.data.items.map((user) => <li key={user.id}><div><strong>{user.displayName}</strong><span>{user.username} · {user.email ?? 'No email'}</span><button className="admin-action-link" type="button" onClick={() => setSelectedId(user.id)}>View {user.displayName}</button></div><StatusBadge tone={user.status === 'ACTIVE' ? 'success' : 'warning'}>{user.status}</StatusBadge></li>)}</ul>
              <Pager page={users.data.page} pageSize={users.data.pageSize} total={users.data.total} onPageChange={setPage} />
            </>}
    </section>
    {canManage && <CreateUserForm error={create.error} pending={create.isPending} onCreate={(request) => create.mutate(request)} />}
  </section>;
}
