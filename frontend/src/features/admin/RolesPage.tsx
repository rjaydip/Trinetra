import { useQuery } from '@tanstack/react-query';

import { isApiProblem } from '../../api/client';
import { api } from '../../api/endpoints';
import { PageState, StatusBadge } from '../../components/ui';

function errorDetail(error: unknown, fallback: string) {
  return isApiProblem(error) ? error.detail : fallback;
}

export function RolesPage() {
  const roles = useQuery({ queryKey: ['admin', 'roles'], queryFn: api.admin.roles.list });
  const permissions = useQuery({ queryKey: ['admin', 'permissions'], queryFn: api.admin.roles.permissions });

  return <section className="admin-workspace" aria-labelledby="roles-title">
    <header><div><p className="eyebrow">Access reference</p><h2 id="roles-title">Roles &amp; permissions</h2></div><p>Role editing and permission assignment are not available in this version.</p></header>
    <section className="admin-panel" aria-labelledby="role-catalogue-title">
      <h3 id="role-catalogue-title">Role catalogue</h3>
      {roles.isPending ? <PageState title="Loading roles">Retrieving the role catalogue…</PageState>
        : roles.isError ? <PageState title="Couldn’t load roles">{errorDetail(roles.error, 'Roles could not be loaded.')}</PageState>
          : roles.data.length === 0 ? <p className="admin-empty">No roles are available.</p>
            : <div className="admin-table-wrap"><table className="admin-table"><thead><tr><th>Role</th><th>Code</th><th>Type</th><th>Description</th></tr></thead><tbody>{roles.data.map((role) => <tr key={role.id}><td>{role.name}</td><td><code>{role.code}</code></td><td><StatusBadge tone={role.isSystem ? 'neutral' : 'warning'}>{role.isSystem ? 'System' : 'Custom'}</StatusBadge></td><td>{role.description ?? '—'}</td></tr>)}</tbody></table></div>}
    </section>
    <section className="admin-panel" aria-labelledby="permission-catalogue-title">
      <h3 id="permission-catalogue-title">Permission catalogue</h3>
      {permissions.isPending ? <PageState title="Loading permissions">Retrieving permission categories…</PageState>
        : permissions.isError ? <PageState title="Couldn’t load permissions">{errorDetail(permissions.error, 'Permissions could not be loaded.')}</PageState>
          : permissions.data.length === 0 ? <p className="admin-empty">No permissions are available.</p>
            : <div className="admin-table-wrap"><table className="admin-table"><thead><tr><th>Category</th><th>Permission</th><th>Name</th><th>Description</th></tr></thead><tbody>{[...permissions.data].sort((a, b) => a.category.localeCompare(b.category) || a.code.localeCompare(b.code)).map((permission) => <tr key={permission.code}><td>{permission.category}</td><td><code>{permission.code}</code></td><td>{permission.name}</td><td>{permission.description ?? '—'}</td></tr>)}</tbody></table></div>}
    </section>
  </section>;
}
