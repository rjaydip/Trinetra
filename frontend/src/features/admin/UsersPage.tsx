import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { type FormEvent, useState } from 'react';

import { errorDetail } from '../../api/client';
import { api } from '../../api/endpoints';
import type { AssignGroupRequest, CreateUserRequest, UpdateUserRequest } from '../../api/models';
import { queryKeys } from '../../api/queryKeys';
import { useAuth } from '../../auth/AuthProvider';
import { hasPermission } from '../../auth/permissions';
import { Button, Pager, PageState, StatusBadge } from '../../components/ui';

function field(form: FormData, name: string) {
  return String(form.get(name) ?? '').trim();
}

function CreateUserForm({ onCreate, error, pending }: {
  onCreate(request: CreateUserRequest): void;
  error: unknown;
  pending: boolean;
}) {
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
    });
    form.reset();
  }

  return <form aria-label="Create user" className="admin-form" onSubmit={submit}>
    <h3>Create user</h3>
    <p>A new account holds no permissions until it is added to an access group.</p>
    <label>Username<input name="username" required /></label>
    <label>Display name<input name="displayName" required /></label>
    <label>Initial password<input name="password" type="password" required /></label>
    <label>Email<input name="email" type="email" /></label>
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
  const [newPassword, setNewPassword] = useState('');
  const [groupId, setGroupId] = useState('');

  const detail = useQuery({ queryKey: queryKeys.users.detail(userId), queryFn: () => api.admin.users.get(userId) });
  const groups = useQuery({ queryKey: queryKeys.users.groups(userId), queryFn: () => api.admin.users.groups(userId) });
  const permissions = useQuery({ queryKey: queryKeys.users.permissions(userId), queryFn: () => api.admin.users.permissions(userId) });
  const allGroups = useQuery({ queryKey: queryKeys.admin.accessGroups, queryFn: api.admin.groups.list, enabled: canManage });

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
    <button className="admin-action-link" type="button" onClick={onClose}>Back to users</button>
    <header><h3 id="user-detail-title">{user.displayName}</h3><StatusBadge tone={user.status === 'ACTIVE' ? 'success' : 'warning'}>{user.status}</StatusBadge></header>
    {editing ? (
      <form aria-label="Edit user" className="admin-form admin-form--inline" onSubmit={(event) => {
        event.preventDefault();
        const data = new FormData(event.currentTarget);
        const email = field(data, 'email');
        update.mutate({ displayName: field(data, 'displayName'), email: email || undefined, status: field(data, 'status') });
      }}>
        <label>Display name<input name="displayName" defaultValue={user.displayName} required /></label>
        <label>Email<input name="email" type="email" defaultValue={user.email ?? ''} /></label>
        <label>Status<select name="status" defaultValue={user.status}>
          <option value="ACTIVE">Active</option>
          <option value="INACTIVE">Inactive</option>
          <option value="LOCKED">Locked</option>
        </select></label>
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
        <div><dt>Must change password</dt><dd>{user.mustChangePassword ? 'Yes' : 'No'}</dd></div>
        <div><dt>Last login</dt><dd>{user.lastLoginAt ? new Date(user.lastLoginAt).toLocaleString() : 'Never'}</dd></div>
      </dl>
    )}
    {canManage && !editing && !user.isSystem && <Button type="button" onClick={() => setEditing(true)}>Edit user</Button>}

    {canManage && !user.isSystem && <section aria-labelledby="reset-password-title">
      <h4 id="reset-password-title">Reset password</h4>
      <form aria-label="Reset password" onSubmit={(event) => { event.preventDefault(); resetPassword.mutate(); }}>
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
  const { session } = useAuth();
  const queryClient = useQueryClient();
  const canManage = hasPermission(session, 'user.manage');
  const [selectedId, setSelectedId] = useState('');
  const [page, setPage] = useState(1);
  const pageSize = 20;

  const users = useQuery({
    queryKey: queryKeys.users.page(page, pageSize),
    queryFn: () => api.admin.users.listPage({ page, pageSize }),
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
