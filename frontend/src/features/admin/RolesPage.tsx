import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { type FormEvent, useState } from 'react';

import { errorDetail } from '../../api/client';
import { api } from '../../api/endpoints';
import type { PermissionResponse, RoleResponse, RoleWriteRequest } from '../../api/models';
import { queryKeys } from '../../api/queryKeys';
import { useAuth } from '../../auth/AuthProvider';
import { hasPermission } from '../../auth/permissions';
import { Button, PageState, StatusBadge } from '../../components/ui';
import './admin.css';

function field(form: FormData, name: string) {
  return String(form.get(name) ?? '').trim();
}

function groupPermissionsByCategory(permissions: PermissionResponse[]) {
  const map = new Map<string, PermissionResponse[]>();
  permissions.forEach((perm) => {
    const list = map.get(perm.category) ?? [];
    list.push(perm);
    map.set(perm.category, list);
  });
  return Array.from(map.entries()).sort(([a], [b]) => a.localeCompare(b));
}

function PermissionSelector({
  availablePermissions,
  selectedPermissions,
  onChange,
}: {
  availablePermissions: PermissionResponse[];
  selectedPermissions: Set<string>;
  onChange(newSet: Set<string>): void;
}) {
  const grouped = groupPermissionsByCategory(availablePermissions);

  function togglePermission(code: string) {
    const next = new Set(selectedPermissions);
    if (next.has(code)) next.delete(code);
    else next.add(code);
    onChange(next);
  }

  function toggleCategory(categoryPerms: PermissionResponse[]) {
    const allSelected = categoryPerms.every((p) => selectedPermissions.has(p.code));
    const next = new Set(selectedPermissions);
    if (allSelected) {
      categoryPerms.forEach((p) => next.delete(p.code));
    } else {
      categoryPerms.forEach((p) => next.add(p.code));
    }
    onChange(next);
  }

  return (
    <div className="permission-selector">
      <div className="permission-selector__header">
        <span>Selected permissions: <strong>{selectedPermissions.size}</strong></span>
        <button
          className="admin-action-link"
          type="button"
          onClick={() => {
            if (selectedPermissions.size === availablePermissions.length) {
              onChange(new Set());
            } else {
              onChange(new Set(availablePermissions.map((p) => p.code)));
            }
          }}
        >
          {selectedPermissions.size === availablePermissions.length ? 'Clear all' : 'Select all permissions'}
        </button>
      </div>
      <div className="permission-selector__categories">
        {grouped.map(([category, perms]) => {
          const catSelectedCount = perms.filter((p) => selectedPermissions.has(p.code)).length;
          const allSelected = catSelectedCount === perms.length;
          return (
            <fieldset key={category} className="permission-category-group">
              <legend>
                <div className="permission-category-legend">
                  <span>{category} ({catSelectedCount}/{perms.length})</span>
                  <button
                    className="admin-action-link"
                    type="button"
                    onClick={() => toggleCategory(perms)}
                  >
                    {allSelected ? 'Clear category' : 'Select all'}
                  </button>
                </div>
              </legend>
              <div className="permission-checkbox-grid">
                {perms.map((perm) => (
                  <label key={perm.code} className="permission-checkbox-label" htmlFor={`permission-${perm.code}`}>
                    <input
                      id={`permission-${perm.code}`}
                      type="checkbox"
                      checked={selectedPermissions.has(perm.code)}
                      onChange={() => togglePermission(perm.code)}
                    />
                    <div>
                      <code>{perm.code}</code>
                      <span className="permission-name">{perm.name}</span>
                    </div>
                  </label>
                ))}
              </div>
            </fieldset>
          );
        })}
      </div>
    </div>
  );
}

export function RolesPage() {
  const { session } = useAuth();
  const queryClient = useQueryClient();
  // Every write here (POST/PUT/DELETE /roles) is gated on role.manage alone — group.manage
  // grants nothing on this resource, so including it would show a fully-enabled create/edit/
  // deactivate UI to a caller who gets a 403 on every submit.
  const canManageRoles = hasPermission(session, 'role.manage');

  const [includeInactive, setIncludeInactive] = useState(true);
  const [selectedRoleId, setSelectedRoleId] = useState('');
  const [creatingRole, setCreatingRole] = useState(false);
  const [editingRoleId, setEditingRoleId] = useState('');
  const [selectedPermissions, setSelectedPermissions] = useState<Set<string>>(new Set());
  const [searchQuery, setSearchQuery] = useState('');

  const roles = useQuery({
    queryKey: queryKeys.admin.roles(includeInactive),
    queryFn: () => api.admin.roles.list(includeInactive),
  });

  const permissions = useQuery({
    queryKey: queryKeys.admin.permissions,
    queryFn: api.admin.roles.permissions,
  });

  const effectiveRoleId = selectedRoleId || roles.data?.[0]?.id || '';

  const roleDetail = useQuery({
    queryKey: queryKeys.admin.role(effectiveRoleId),
    queryFn: () => api.admin.roles.get(effectiveRoleId),
    enabled: Boolean(effectiveRoleId),
  });

  const currentRole = roleDetail.data ?? roles.data?.find((r) => r.id === effectiveRoleId);

  const createRole = useMutation({
    mutationFn: api.admin.roles.create,
    onSuccess: (created) => {
      setCreatingRole(false);
      setSelectedRoleId(created.id);
      setSelectedPermissions(new Set());
      return Promise.all([
        queryClient.invalidateQueries({ queryKey: queryKeys.admin.roles() }),
        queryClient.invalidateQueries({ queryKey: queryKeys.admin.role(created.id) }),
      ]);
    },
  });

  const updateRole = useMutation({
    mutationFn: ({ id, body }: { id: string; body: RoleWriteRequest }) => api.admin.roles.update(id, body),
    onSuccess: (_, { id }) => {
      setEditingRoleId('');
      return Promise.all([
        queryClient.invalidateQueries({ queryKey: queryKeys.admin.roles() }),
        queryClient.invalidateQueries({ queryKey: queryKeys.admin.role(id) }),
      ]);
    },
  });

  const deleteRole = useMutation({
    mutationFn: (id: string) => api.admin.roles.delete(id),
    onSuccess: (_, id) => Promise.all([
      queryClient.invalidateQueries({ queryKey: queryKeys.admin.roles() }),
      queryClient.invalidateQueries({ queryKey: queryKeys.admin.role(id) }),
    ]),
  });

  function startCreate() {
    setCreatingRole(true);
    setEditingRoleId('');
    setSelectedPermissions(new Set());
  }

  function startEdit(role: RoleResponse) {
    setEditingRoleId(role.id);
    setCreatingRole(false);
    setSelectedPermissions(new Set(role.permissions ?? []));
  }

  function submitCreateRole(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const data = new FormData(event.currentTarget);
    const roleCode = field(data, 'code').toUpperCase();
    const name = field(data, 'name');
    const description = field(data, 'description');
    const status = field(data, 'status') || 'DRAFT';

    createRole.mutate({
      code: roleCode,
      name,
      description: description || null,
      status,
      permissions: Array.from(selectedPermissions),
    });
  }

  function submitEditRole(event: FormEvent<HTMLFormElement>, role: RoleResponse) {
    event.preventDefault();
    const data = new FormData(event.currentTarget);
    const name = field(data, 'name');
    const description = field(data, 'description');
    const status = field(data, 'status') || role.status || 'ACTIVE';

    updateRole.mutate({
      id: role.id,
      body: {
        code: role.code,
        name,
        description: description || null,
        status,
        permissions: Array.from(selectedPermissions),
      },
    });
  }

  function toggleWorkflowStatus(role: RoleResponse) {
    const nextStatus = role.status === 'ACTIVE' ? 'INACTIVE' : 'ACTIVE';
    if (nextStatus === 'INACTIVE' && !role.isSystem) {
      deleteRole.mutate(role.id);
    } else {
      updateRole.mutate({
        id: role.id,
        body: {
          code: role.code,
          name: role.name,
          description: role.description,
          status: nextStatus,
          permissions: role.permissions ?? [],
        },
      });
    }
  }

  const roleList = roles.data ?? [];
  const filteredRoles = roleList.filter((r) => {
    if (!searchQuery) return true;
    const query = searchQuery.toLowerCase();
    return r.name.toLowerCase().includes(query) || r.code.toLowerCase().includes(query);
  });

  return (
    <section className="admin-workspace roles-page" aria-labelledby="roles-title">
      <header>
        <div>
          <p className="eyebrow">Access control</p>
          <h2 id="roles-title">Roles &amp; permissions</h2>
        </div>
        <p>Maintain the platform role catalogue, compose custom roles from permissions, and manage lifecycle workflow statuses.</p>
      </header>

      {/* Role Catalog Maintenance Section */}
      <section className="admin-panel" aria-labelledby="role-catalogue-title">
        <header className="panel-header">
          <div>
            <h3 id="role-catalogue-title">Role catalogue</h3>
            <p>Presets shipped with the platform and agency-specific custom roles.</p>
          </div>
          {canManageRoles && !creatingRole && (
            <button className="button" type="button" onClick={startCreate}>
              Create custom role
            </button>
          )}
        </header>

        {roles.isPending ? (
          <PageState title="Loading roles">Retrieving the role catalogue…</PageState>
        ) : roles.isError ? (
          <PageState title="Couldn’t load roles">{errorDetail(roles.error, 'Roles could not be loaded.')}</PageState>
        ) : (
          <>
            <div className="role-catalog-controls">
              <label className="search-control">
                Search roles
                <input
                  type="search"
                  placeholder="Filter by name or code…"
                  value={searchQuery}
                  onChange={(e) => setSearchQuery(e.target.value)}
                />
              </label>
              <label className="checkbox-label">
                <input
                  type="checkbox"
                  checked={includeInactive}
                  onChange={(e) => setIncludeInactive(e.target.checked)}
                />
                Include draft and inactive roles
              </label>
            </div>

            {/* Create Custom Role Form Modal / Banner */}
            {creatingRole && canManageRoles && (
              <div className="role-editor-card">
                <form
                  aria-label="Create custom role"
                  className="admin-form"
                  onSubmit={submitCreateRole}
                >
                  <header className="panel-header">
                    <h4>Create custom role</h4>
                    <button
                      className="button button--secondary button--small"
                      type="button"
                      onClick={() => setCreatingRole(false)}
                    >
                      Cancel
                    </button>
                  </header>
                  <p>Compose a new role from platform permissions. New custom roles can begin in DRAFT or ACTIVE status.</p>

                  <div className="admin-coordinate-grid">
                    <label>
                      Role code
                      <input
                        name="code"
                        placeholder="e.g. DISPATCH_SUPERVISOR"
                        pattern="^[A-Za-z0-9_]{3,50}$"
                        title="Letters, numbers, and underscores (3-50 characters) — lowercase is uppercased automatically"
                        required
                      />
                    </label>
                    <label>
                      Display name
                      <input name="name" placeholder="e.g. Dispatch Supervisor" required />
                    </label>
                  </div>

                  <div className="admin-coordinate-grid">
                    <label>
                      Initial workflow status
                      <select name="status" defaultValue="DRAFT">
                        <option value="DRAFT">DRAFT (Non-granting stage)</option>
                        <option value="ACTIVE">ACTIVE (Ready for assignment)</option>
                      </select>
                    </label>
                    <label>
                      Description
                      <input name="description" placeholder="Optional explanation of duties" />
                    </label>
                  </div>

                  <label className="permissions-label">
                    Compose permissions ({selectedPermissions.size} selected)
                  </label>
                  <PermissionSelector
                    availablePermissions={permissions.data ?? []}
                    selectedPermissions={selectedPermissions}
                    onChange={setSelectedPermissions}
                  />

                  {createRole.isError && (
                    <p className="form-error" role="alert">
                      {errorDetail(createRole.error, 'The role could not be created.')}
                    </p>
                  )}

                  <div className="form-actions">
                    <Button disabled={createRole.isPending} type="submit">
                      {createRole.isPending ? 'Creating role…' : 'Create role'}
                    </Button>
                    <button
                      className="button button--secondary"
                      type="button"
                      onClick={() => setCreatingRole(false)}
                    >
                      Cancel
                    </button>
                  </div>
                </form>
              </div>
            )}

            <div className="role-layout">
              {/* Left Column: Role Table */}
              <div className="role-table-container">
                {filteredRoles.length === 0 ? (
                  <p className="admin-empty">No roles match your filter.</p>
                ) : (
                  <div className="admin-table-wrap">
                    <table className="admin-table">
                      <thead>
                        <tr>
                          <th>Role</th>
                          <th>Code</th>
                          <th>Type</th>
                          <th>Status</th>
                          <th>Action</th>
                        </tr>
                      </thead>
                      <tbody>
                        {filteredRoles.map((role) => {
                          const isSelected = role.id === effectiveRoleId;
                          return (
                            <tr
                              key={role.id}
                              className={isSelected ? 'table-row--selected' : ''}
                            >
                              <td>
                                <strong>{role.name}</strong>
                                {role.customized && <span className="tag-customized">Customized</span>}
                              </td>
                              <td><code>{role.code}</code></td>
                              <td>
                                <StatusBadge tone={role.isSystem ? 'neutral' : 'warning'}>
                                  {role.isSystem ? 'System' : 'Custom'}
                                </StatusBadge>
                              </td>
                              <td>
                                <StatusBadge
                                  tone={
                                    role.status === 'ACTIVE'
                                      ? 'success'
                                      : role.status === 'DRAFT'
                                        ? 'neutral'
                                        : 'warning'
                                  }
                                >
                                  {role.status ?? 'ACTIVE'}
                                </StatusBadge>
                              </td>
                              <td>
                                <button
                                  className="admin-action-link"
                                  type="button"
                                  onClick={() => {
                                    setSelectedRoleId(role.id);
                                    setCreatingRole(false);
                                    setEditingRoleId('');
                                  }}
                                >
                                  View details
                                </button>
                              </td>
                            </tr>
                          );
                        })}
                      </tbody>
                    </table>
                  </div>
                )}
              </div>

              {/* Right Column: Selected Role Detail & Maintenance */}
              {currentRole && (
                <div className="role-detail-card" aria-label={`Details for ${currentRole.name}`}>
                  <header className="role-detail-header">
                    <div>
                      <p className="eyebrow">
                        {currentRole.isSystem ? 'System Preset' : 'Custom Role'}
                        {currentRole.customized ? ' · Customized' : ''}
                      </p>
                      <h4>{currentRole.name}</h4>
                      <p><code>{currentRole.code}</code></p>
                    </div>
                    <div className="role-detail-badges">
                      <StatusBadge
                        tone={
                          currentRole.status === 'ACTIVE'
                            ? 'success'
                            : currentRole.status === 'DRAFT'
                              ? 'neutral'
                              : 'warning'
                        }
                      >
                        {currentRole.status ?? 'ACTIVE'}
                      </StatusBadge>
                    </div>
                  </header>

                  {currentRole.description && (
                    <p className="role-description">{currentRole.description}</p>
                  )}

                  {/* Lifecycle & Usage Metrics */}
                  <dl className="role-metadata-grid">
                    <div>
                      <dt>Usage in groups</dt>
                      <dd><strong>{currentRole.usageCount ?? (currentRole.usedBy?.length ?? 0)}</strong> access groups</dd>
                    </div>
                    {currentRole.createdAt && (
                      <div>
                        <dt>Created</dt>
                        <dd>{new Date(currentRole.createdAt).toLocaleDateString()}</dd>
                      </div>
                    )}
                    {currentRole.updatedAt && (
                      <div>
                        <dt>Last updated</dt>
                        <dd>{new Date(currentRole.updatedAt).toLocaleDateString()}</dd>
                      </div>
                    )}
                  </dl>

                  {/* Role Action Controls */}
                  {canManageRoles && (
                    <div className="role-actions-toolbar">
                      {currentRole.code !== 'SUPER_ADMIN' && (
                        <button
                          className="button button--secondary button--small"
                          type="button"
                          onClick={() => (editingRoleId === currentRole.id ? setEditingRoleId('') : startEdit(currentRole))}
                        >
                          {editingRoleId === currentRole.id ? 'Close edit' : 'Edit role'}
                        </button>
                      )}

                      {currentRole.code !== 'SUPER_ADMIN' && (
                        <button
                          className="button button--secondary button--small"
                          disabled={updateRole.isPending || deleteRole.isPending}
                          type="button"
                          onClick={() => toggleWorkflowStatus(currentRole)}
                        >
                          {currentRole.status === 'ACTIVE'
                            ? 'Deactivate role'
                            : currentRole.status === 'DRAFT'
                              ? 'Activate role'
                              : 'Reactivate role'}
                        </button>
                      )}
                    </div>
                  )}

                  {/* Errors from the toolbar's own actions (activate/deactivate/reactivate) —
                      distinct from the edit form's own error message below, since this action
                      can be taken without the edit form ever being open. */}
                  {editingRoleId !== currentRole.id && updateRole.isError && (
                    <p className="form-error" role="alert">
                      {errorDetail(updateRole.error, 'The role could not be updated.')}
                    </p>
                  )}
                  {deleteRole.isError && (
                    <p className="form-error" role="alert">
                      {errorDetail(deleteRole.error, 'The role could not be deactivated.')}
                    </p>
                  )}

                  {/* Edit Form */}
                  {editingRoleId === currentRole.id && canManageRoles && (
                    <form
                      aria-label={`Edit role ${currentRole.name}`}
                      className="admin-form role-edit-form"
                      onSubmit={(e) => submitEditRole(e, currentRole)}
                    >
                      <h5>Edit {currentRole.name}</h5>

                      <label>
                        Display name
                        <input name="name" defaultValue={currentRole.name} required />
                      </label>

                      <label>
                        Workflow status
                        <select name="status" defaultValue={currentRole.status ?? 'ACTIVE'}>
                          <option value="ACTIVE">ACTIVE</option>
                          <option value="INACTIVE">INACTIVE</option>
                          {currentRole.status === 'DRAFT' && <option value="DRAFT">DRAFT</option>}
                        </select>
                      </label>

                      <label>
                        Description
                        <textarea name="description" defaultValue={currentRole.description ?? ''} />
                      </label>

                      <label className="permissions-label">
                        Assigned permissions ({selectedPermissions.size} selected)
                      </label>
                      <PermissionSelector
                        availablePermissions={permissions.data ?? []}
                        selectedPermissions={selectedPermissions}
                        onChange={setSelectedPermissions}
                      />

                      {updateRole.isError && (
                        <p className="form-error" role="alert">
                          {errorDetail(updateRole.error, 'The role could not be updated.')}
                        </p>
                      )}

                      <div className="form-actions">
                        <Button disabled={updateRole.isPending} type="submit">
                          {updateRole.isPending ? 'Saving changes…' : 'Save changes'}
                        </Button>
                        <button
                          className="button button--secondary"
                          type="button"
                          onClick={() => setEditingRoleId('')}
                        >
                          Cancel
                        </button>
                      </div>
                    </form>
                  )}

                  {/* Associated Groups */}
                  {currentRole.usedBy && currentRole.usedBy.length > 0 && (
                    <section aria-labelledby="role-used-by-title" className="role-used-by-section">
                      <h5 id="role-used-by-title">Associated Access Groups</h5>
                      <ul className="plain-list">
                        {currentRole.usedBy.map((group) => (
                          <li key={group.id}>
                            <strong>{group.name}</strong>
                            <span><code>{group.code}</code> · {group.status}</span>
                          </li>
                        ))}
                      </ul>
                    </section>
                  )}

                  {/* Granted Permissions List */}
                  <section aria-labelledby="role-permissions-title" className="role-permissions-section">
                    <h5 id="role-permissions-title">
                      Granted Permissions ({currentRole.permissions?.length ?? 0})
                    </h5>
                    {currentRole.permissionDetails && currentRole.permissionDetails.length > 0 ? (
                      <div className="role-permission-details-list">
                        {currentRole.permissionDetails.map((detail) => (
                          <div key={detail.code} className="permission-detail-item">
                            <div>
                              <code>{detail.code}</code>
                              <strong>{detail.name}</strong>
                            </div>
                            {detail.description && <p>{detail.description}</p>}
                          </div>
                        ))}
                      </div>
                    ) : currentRole.permissions && currentRole.permissions.length > 0 ? (
                      <ul className="token-list">
                        {currentRole.permissions.map((perm) => (
                          <li key={perm}><code>{perm}</code></li>
                        ))}
                      </ul>
                    ) : (
                      <p className="admin-empty">This role grants no permissions.</p>
                    )}
                  </section>
                </div>
              )}
            </div>
          </>
        )}
      </section>

      {/* Permission Catalogue Reference Section */}
      <section className="admin-panel" aria-labelledby="permission-catalogue-title">
        <h3 id="permission-catalogue-title">Permission catalogue</h3>
        {permissions.isPending ? (
          <PageState title="Loading permissions">Retrieving permission categories…</PageState>
        ) : permissions.isError ? (
          <PageState title="Couldn’t load permissions">{errorDetail(permissions.error, 'Permissions could not be loaded.')}</PageState>
        ) : permissions.data.length === 0 ? (
          <p className="admin-empty">No permissions are available.</p>
        ) : (
          <div className="admin-table-wrap">
            <table className="admin-table">
              <thead>
                <tr>
                  <th>Category</th>
                  <th>Permission</th>
                  <th>Name</th>
                  <th>Description</th>
                </tr>
              </thead>
              <tbody>
                {[...permissions.data]
                  .sort((a, b) => a.category.localeCompare(b.category) || a.code.localeCompare(b.code))
                  .map((permission) => (
                    <tr key={permission.code}>
                      <td>{permission.category}</td>
                      <td><code>{permission.code}</code></td>
                      <td>{permission.name}</td>
                      <td>{permission.description ?? '—'}</td>
                    </tr>
                  ))}
              </tbody>
            </table>
          </div>
        )}
      </section>
    </section>
  );
}

