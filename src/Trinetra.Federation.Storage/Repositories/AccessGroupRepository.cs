using Dapper;
using Npgsql;

namespace Trinetra.Federation.Storage.Repositories;

/// <summary>A role definition and the permissions it grants.</summary>
public sealed record RoleSummary(Guid Id, string Code, string Name, string? Description, bool IsSystem);

/// <summary>A permission in the vocabulary.</summary>
public sealed record PermissionSummary(string Code, string Name, string Category, string? Description);

/// <summary>One boundary a group applies within.</summary>
public sealed record ScopeSummary(
    Guid Id, string ScopeType, Guid? OrganizationUnitId, Guid? GeographicAreaId,
    string? ResourceType, Guid? ResourceId, string? Description);

/// <summary>A group with its role and scopes resolved.</summary>
public sealed record AccessGroupDetail(
    Guid Id, string Code, string Name, string? Description, string Status,
    string RoleCode, IReadOnlyList<string> Permissions, IReadOnlyList<ScopeSummary> Scopes,
    int MemberCount);

/// <summary>Access groups, roles, permissions and scopes.</summary>
// CA1822 fires on the write methods now that they take their connection from the UnitOfWork
// rather than the data source. That is the point of the change, not a defect: they stay instance
// methods so one entity's operations are called the same way regardless of which need the pool.
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Performance", "CA1822:Mark members as static",
    Justification = "Write methods take their connection from the UnitOfWork by design.",
    Scope = "type")]
public sealed class AccessGroupRepository
{
    private readonly NpgsqlDataSource _dataSource;

    public AccessGroupRepository(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    /// <summary>
    /// A boolean SQL fragment, true when the access group aliased <c>ag</c> is visible to the
    /// caller exercising the permission in <c>@VisPermission</c>.
    /// </summary>
    /// <remarks>
    /// This is the read-side mirror of <see cref="GroupScopesWithinReachAsync"/> +
    /// <see cref="GroupGeographyScopesWithinReachAsync"/> — the same rule the escalation
    /// chokepoint applies before <i>conferring</i> a group: per dimension the caller is scoped
    /// on, the group must declare a scope of that type and every such scope must be within the
    /// caller's authorized set. A dimension-unrestricted group is therefore visible only to a
    /// caller unscoped on that dimension, so PLATFORM-ADMINS (no scopes) is invisible to every
    /// scoped administrator. Parameters: <c>@UnscopedOrg</c>, <c>@UnscopedGeo</c>, <c>@UserId</c>,
    /// <c>@ApiKeyId</c>, <c>@VisPermission</c>. Not user input — a fixed fragment, interpolated
    /// the same way <c>CameraRepository.Scope</c> is.
    /// </remarks>
    internal const string GroupVisiblePredicate = """
        (@UnscopedOrg OR (
            EXISTS (SELECT 1 FROM federation.group_scopes gs
                    JOIN federation.scopes s ON s.id = gs.scope_id
                    WHERE gs.group_id = ag.id AND s.scope_type = 'ORGANIZATION')
            AND NOT EXISTS (
                SELECT 1 FROM federation.group_scopes gs
                JOIN federation.scopes s ON s.id = gs.scope_id
                WHERE gs.group_id = ag.id AND s.scope_type = 'ORGANIZATION'
                  AND s.organization_unit_id <> ALL (
                      SELECT organization_unit_id FROM federation.authorized_org_units(
                          p_user_id => @UserId, p_api_key_id => @ApiKeyId,
                          p_permission => @VisPermission)))))
        AND (@UnscopedGeo OR (
            EXISTS (SELECT 1 FROM federation.group_scopes gs
                    JOIN federation.scopes s ON s.id = gs.scope_id
                    WHERE gs.group_id = ag.id AND s.scope_type = 'GEOGRAPHY')
            AND NOT EXISTS (
                SELECT 1 FROM federation.group_scopes gs
                JOIN federation.scopes s ON s.id = gs.scope_id
                WHERE gs.group_id = ag.id AND s.scope_type = 'GEOGRAPHY'
                  AND s.geographic_area_id <> ALL (
                      SELECT geographic_area_id FROM federation.authorized_geographic_areas(
                          p_user_id => @UserId, p_api_key_id => @ApiKeyId,
                          p_permission => @VisPermission)))))
        """;

    /// <summary>
    /// The parameters <see cref="GroupVisiblePredicate"/> needs, for <paramref name="caller"/>
    /// exercising <paramref name="permission"/>. Returned as <see cref="DynamicParameters"/> so a
    /// caller can add its own (e.g. <c>groupId</c>) before executing.
    /// </summary>
    internal static DynamicParameters GroupVisibilityParams(CallerContext caller, string permission)
    {
        var p = new DynamicParameters();
        p.Add("UnscopedOrg", caller.IsUnscopedFor(permission));
        p.Add("UnscopedGeo", caller.IsUnscopedForGeography(permission));
        p.Add("UserId", caller.UserId);
        p.Add("ApiKeyId", caller.ApiKeyId);
        p.Add("VisPermission", permission);
        return p;
    }

    // ---- Reference data ----------------------------------------------------

    public async Task<IReadOnlyList<RoleSummary>> ListRolesAsync(CancellationToken ct)
    {
        await using var c = await _dataSource.OpenConnectionAsync(ct);
        var rows = await c.QueryAsync<RoleSummary>(new CommandDefinition("""
            SELECT id, code, name, description, is_system
            FROM federation.roles WHERE status = 'ACTIVE' ORDER BY code;
            """, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<IReadOnlyList<PermissionSummary>> ListPermissionsAsync(
        CancellationToken ct)
    {
        await using var c = await _dataSource.OpenConnectionAsync(ct);
        var rows = await c.QueryAsync<PermissionSummary>(new CommandDefinition("""
            SELECT code, name, category, description
            FROM federation.permissions ORDER BY category, code;
            """, cancellationToken: ct));
        return rows.ToList();
    }

    /// <summary>The permissions a role grants. Used to block privilege escalation.</summary>
    public async Task<IReadOnlySet<string>> GetRolePermissionsAsync(
        Guid roleId, CancellationToken ct)
    {
        await using var c = await _dataSource.OpenConnectionAsync(ct);
        var rows = await c.QueryAsync<string>(new CommandDefinition("""
            SELECT permission_code FROM federation.role_permissions WHERE role_id = @roleId;
            """, new { roleId }, cancellationToken: ct));
        return rows.ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>The permissions a group grants, via its role.</summary>
    public async Task<IReadOnlySet<string>> GetGroupPermissionsAsync(
        Guid groupId, CancellationToken ct)
    {
        await using var c = await _dataSource.OpenConnectionAsync(ct);
        var rows = await c.QueryAsync<string>(new CommandDefinition("""
            SELECT rp.permission_code
            FROM federation.access_groups ag
            JOIN federation.role_permissions rp ON rp.role_id = ag.role_id
            WHERE ag.id = @groupId;
            """, new { groupId }, cancellationToken: ct));
        return rows.ToHashSet(StringComparer.Ordinal);
    }

    // ---- Groups ------------------------------------------------------------

    /// <summary>All access groups, with role, permissions and scopes resolved.</summary>
    /// <remarks>
    /// One round trip, three result sets, joined in memory. The obvious shape — loop the groups
    /// and fetch permissions and scopes per group — was both an N+1 (200 groups = 401 queries)
    /// and a pool-exhaustion deadlock: the outer query held a pooled connection while each inner
    /// call rented another from the same pool. Under concurrency every connection ends up held
    /// by an outer scope waiting for an inner one, and the API stalls until the pool times out.
    ///
    /// The rule this encodes: a repository method must never call another repository method
    /// while holding a connection.
    /// </remarks>
    /// <summary>
    /// Every access group <paramref name="caller"/> may see, with role, permissions and scopes
    /// resolved. Filtered by <see cref="GroupVisiblePredicate"/> keyed on <c>group.read</c>: a
    /// scoped caller sees only groups it could itself confer, so a dimension-unrestricted group
    /// (PLATFORM-ADMINS and any other) is hidden from every scoped administrator.
    /// </summary>
    public Task<IReadOnlyList<AccessGroupDetail>> ListAsync(CallerContext caller, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        return LoadAsync(groupId: null, caller, ct);
    }

    /// <summary>
    /// One group by id, unfiltered. The caller-visibility gate for a single group is
    /// <see cref="IsVisibleToAsync"/>, run by the endpoint before this — <see cref="GetAsync"/>
    /// itself is also called from the write-side guards, which must load a group precisely so
    /// they can then reject conferring it.
    /// </summary>
    public async Task<AccessGroupDetail?> GetAsync(Guid id, CancellationToken ct)
    {
        var groups = await LoadAsync(id, filterCaller: null, ct);
        return groups.Count == 0 ? null : groups[0];
    }

    /// <summary>
    /// Whether <paramref name="caller"/> may see group <paramref name="groupId"/> while
    /// exercising <paramref name="permission"/> — the single-resource counterpart of the filter
    /// <see cref="ListAsync"/> applies. Used by the endpoint to answer <c>GET /{id}</c> and
    /// <c>GET /{id}/members</c> with a 404 (never a 403) for an out-of-reach group, so a hidden
    /// group is indistinguishable from one that does not exist.
    /// </summary>
    public async Task<bool> IsVisibleToAsync(
        Guid groupId, CallerContext caller, string permission, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);

        var p = GroupVisibilityParams(caller, permission);
        p.Add("groupId", groupId);

        await using var c = await _dataSource.OpenConnectionAsync(ct);
        return await c.ExecuteScalarAsync<bool>(new CommandDefinition($"""
            SELECT EXISTS (
                SELECT 1 FROM federation.access_groups ag
                WHERE ag.id = @groupId AND ({GroupVisiblePredicate}));
            """, p, cancellationToken: ct));
    }

    private async Task<IReadOnlyList<AccessGroupDetail>> LoadAsync(
        Guid? groupId, CallerContext? filterCaller, CancellationToken ct)
    {
        // The list path filters to what the caller may see; the by-id path does not (its gate is
        // IsVisibleToAsync at the endpoint, and the write guards must see every group).
        var visibleClause = filterCaller is null ? "" : $"AND ({GroupVisiblePredicate})";

        var sql = $"""
            SELECT ag.id, ag.code, ag.name, ag.description, ag.status, r.code AS role_code,
                   (SELECT count(*) FROM federation.user_groups ug
                     WHERE ug.group_id = ag.id AND ug.status = 'ACTIVE') AS member_count
            FROM federation.access_groups ag
            JOIN federation.roles r ON r.id = ag.role_id
            WHERE (@groupId::uuid IS NULL OR ag.id = @groupId)
            {visibleClause}
            ORDER BY ag.code;

            SELECT ag.id AS group_id, rp.permission_code
            FROM federation.access_groups ag
            JOIN federation.role_permissions rp ON rp.role_id = ag.role_id
            WHERE (@groupId::uuid IS NULL OR ag.id = @groupId);

            SELECT gs.group_id, s.id, s.scope_type, s.organization_unit_id,
                   s.geographic_area_id, s.resource_type, s.resource_id, s.description
            FROM federation.group_scopes gs
            JOIN federation.scopes s ON s.id = gs.scope_id
            WHERE (@groupId::uuid IS NULL OR gs.group_id = @groupId);
            """;

        DynamicParameters p;
        if (filterCaller is null)
        {
            p = new DynamicParameters();
        }
        else
        {
            p = GroupVisibilityParams(filterCaller, "group.read");
        }

        p.Add("groupId", groupId);

        await using var c = await _dataSource.OpenConnectionAsync(ct);
        await using var reader = await c.QueryMultipleAsync(new CommandDefinition(
            sql, p, cancellationToken: ct));

        var groups = (await reader.ReadAsync<GroupRow>()).ToList();
        var permissions = (await reader.ReadAsync<(Guid GroupId, string PermissionCode)>())
            .ToLookup(x => x.GroupId, x => x.PermissionCode);
        var scopes = (await reader.ReadAsync<ScopeRow>()).ToLookup(x => x.GroupId);

        return
        [
            .. groups.Select(g => new AccessGroupDetail(
                g.Id, g.Code, g.Name, g.Description, g.Status, g.RoleCode,
                [.. permissions[g.Id]],
                [.. scopes[g.Id].Select(x => new ScopeSummary(
                    x.Id, x.ScopeType, x.OrganizationUnitId, x.GeographicAreaId,
                    x.ResourceType, x.ResourceId, x.Description))],
                g.MemberCount)),
        ];
    }

    public async Task<Guid> UpsertAsync(
        AccessGroup group, Guid? actorId, UnitOfWork work, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(group);

        var c = work.Connection;
        return await c.ExecuteScalarAsync<Guid>(new CommandDefinition("""
            INSERT INTO federation.access_groups
                (id, code, name, description, role_id, status, created_by, updated_by)
            VALUES (COALESCE(NULLIF(@Id,'00000000-0000-0000-0000-000000000000'::uuid), gen_random_uuid()),
                    @Code, @Name, @Description, @RoleId, @Status, @actorId, @actorId)
            ON CONFLICT (id) DO UPDATE
            SET code = EXCLUDED.code, name = EXCLUDED.name,
                description = EXCLUDED.description, role_id = EXCLUDED.role_id,
                status = EXCLUDED.status, updated_by = EXCLUDED.updated_by, updated_at = now()
            RETURNING id;
            """, new { group.Id, group.Code, group.Name, group.Description, group.RoleId, group.Status, actorId },
            work.Transaction, cancellationToken: ct));
    }

    /// <summary>
    /// Sets a group's lifecycle status — <c>DRAFT</c>, <c>ACTIVE</c> or <c>DISABLED</c>. Returns
    /// <see langword="false"/> if the group does not exist.
    /// </summary>
    /// <remarks>
    /// The scope-completeness check that guards activation (an unscoped group is an estate-wide
    /// grant) is in the endpoint, next to the identical check on group creation.
    /// </remarks>
    public async Task<bool> SetStatusAsync(
        Guid groupId, string status, Guid? actorId, UnitOfWork work, CancellationToken ct)
    {
        var affected = await work.Connection.ExecuteAsync(new CommandDefinition("""
            UPDATE federation.access_groups
            SET status = @status, updated_by = @actorId, updated_at = now()
            WHERE id = @groupId;
            """, new { groupId, status, actorId }, work.Transaction, cancellationToken: ct));
        return affected > 0;
    }

    // ---- Scopes ------------------------------------------------------------

    // A group's scopes are returned as part of GetAsync's AccessGroupDetail (which is behind
    // IsVisibleToAsync). There is deliberately no standalone unscoped ListScopesAsync — it would
    // be a row-returning query with no CallerContext, i.e. an invariant-11 hole waiting for a
    // caller.

    /// <summary>Creates a scope and attaches it to a group.</summary>
    /// <remarks>
    /// Scopes are created per attachment rather than shared, so removing one from a group cannot
    /// silently alter another group that happened to reference the same boundary.
    /// </remarks>
    public async Task<Guid> AddScopeAsync(
        Guid groupId, string scopeType, Guid? organizationUnitId, Guid? geographicAreaId,
        string? resourceType, Guid? resourceId, string? description, UnitOfWork work, CancellationToken ct)
    {
        var c = work.Connection;

        var scopeId = await c.ExecuteScalarAsync<Guid>(new CommandDefinition("""
            INSERT INTO federation.scopes
                (scope_type, organization_unit_id, geographic_area_id,
                 resource_type, resource_id, description)
            VALUES (@scopeType, @organizationUnitId, @geographicAreaId,
                    @resourceType, @resourceId, @description)
            RETURNING id;
            """, new
        {
            scopeType, organizationUnitId, geographicAreaId, resourceType, resourceId, description,
        }, work.Transaction, cancellationToken: ct));

        await c.ExecuteAsync(new CommandDefinition("""
            INSERT INTO federation.group_scopes (group_id, scope_id)
            VALUES (@groupId, @scopeId) ON CONFLICT DO NOTHING;
            """, new { groupId, scopeId }, work.Transaction, cancellationToken: ct));
        return scopeId;
    }

    public async Task<bool> RemoveScopeAsync(
        Guid groupId, Guid scopeId, UnitOfWork work, CancellationToken ct)
    {
        var c = work.Connection;
        var affected = await c.ExecuteAsync(new CommandDefinition("""
            DELETE FROM federation.group_scopes WHERE group_id = @groupId AND scope_id = @scopeId;
            DELETE FROM federation.scopes WHERE id = @scopeId;
            """, new { groupId, scopeId }, work.Transaction, cancellationToken: ct));
        return affected > 0;
    }

    // ---- Membership --------------------------------------------------------

    /// <summary>
    /// A group's active members, filtered to the accounts <paramref name="caller"/> could also
    /// see in the user directory (<see cref="UserRepository.VisibleForUserReadPredicate"/>).
    /// </summary>
    /// <remarks>
    /// The endpoint gates the group itself through <see cref="IsVisibleToAsync"/> first; this
    /// second filter stops a caller who legitimately sees a multi-department group from reading
    /// the full roster of members in departments outside their reach. An unscoped caller sees
    /// every member.
    /// </remarks>
    public async Task<IReadOnlyList<(Guid UserId, string Username, DateTimeOffset? ExpiresAt)>>
        ListMembersAsync(Guid groupId, CallerContext caller, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);

        var p = UserRepository.UserVisibilityParams(caller);
        p.Add("groupId", groupId);

        await using var c = await _dataSource.OpenConnectionAsync(ct);
        var rows = await c.QueryAsync<(Guid, string, DateTimeOffset?)>(new CommandDefinition($"""
            SELECT pu.id, pu.username, ug.expires_at
            FROM federation.user_groups ug
            JOIN federation.platform_users pu ON pu.id = ug.user_id
            WHERE ug.group_id = @groupId AND ug.status = 'ACTIVE'
              AND {UserRepository.VisibleForUserReadPredicate}
            ORDER BY pu.username;
            """, p, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<bool> RevokeMembershipAsync(
        Guid userId, Guid groupId, UnitOfWork work, CancellationToken ct)
    {
        var c = work.Connection;
        var affected = await c.ExecuteAsync(new CommandDefinition("""
            UPDATE federation.user_groups SET status = 'REVOKED'
            WHERE user_id = @userId AND group_id = @groupId AND status = 'ACTIVE';
            """, new { userId, groupId }, work.Transaction, cancellationToken: ct));
        return affected > 0;
    }

    /// <summary>
    /// Whether every organization scope on a group falls inside what the caller administers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The missing half of the escalation guard. Comparing permission sets alone stops a caller
    /// granting rights they do not hold, but not granting their own rights <i>over another
    /// department</i> — which is the same takeover by a different route.
    /// </para>
    /// <para>
    /// A group with no organization scope is unrestricted by design, so an empty result is
    /// refused rather than treated as trivially satisfied. That distinction is the entire bug:
    /// <c>bool_and</c> over no rows returns NULL, and reading NULL as success would let a caller
    /// grant an estate-wide group by simply never scoping it.
    /// </para>
    /// </remarks>
    public async Task<bool> GroupScopesWithinReachAsync(
        Guid groupId, CallerContext caller, string permission, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);

        if (caller.UserId is null && caller.ApiKeyId is null)
        {
            return false;
        }

        await using var c = await _dataSource.OpenConnectionAsync(ct);

        return await c.ExecuteScalarAsync<bool>(new CommandDefinition("""
            SELECT COALESCE(bool_and(
                       s.organization_unit_id = ANY (
                           SELECT organization_unit_id
                           FROM federation.authorized_org_units(
                                    p_user_id => @UserId, p_api_key_id => @ApiKeyId,
                                    p_permission => @permission))),
                   FALSE)
            FROM federation.group_scopes gs
            JOIN federation.scopes s ON s.id = gs.scope_id
            WHERE gs.group_id = @groupId AND s.scope_type = 'ORGANIZATION';
            """, new { groupId, caller.UserId, caller.ApiKeyId, permission }, cancellationToken: ct));
    }

    /// <summary>The unscoped SUPER_ADMIN group, created if absent.</summary>
    /// <remarks>
    /// No organization scope, so it reaches the whole estate. This is the only group for which
    /// that is true, and every other user is created from the account that holds it.
    /// </remarks>
    public async Task<Guid> EnsurePlatformAdminGroupAsync(UnitOfWork work, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(work);

        return await work.Connection.ExecuteScalarAsync<Guid>(new CommandDefinition(
            """
            INSERT INTO federation.access_groups (code, name, description, role_id, status)
            SELECT 'PLATFORM-ADMINS', 'Platform Administrators',
                   'Unscoped administration. Created at first start.', r.id, 'ACTIVE'
            FROM federation.roles r WHERE r.code = 'SUPER_ADMIN'
            ON CONFLICT (code) DO UPDATE SET status = 'ACTIVE'
            RETURNING id;
            """, work.Transaction, cancellationToken: ct));
    }

    /// <summary>Whether the caller may scope a group to this organization unit.</summary>
    /// <remarks>
    /// A scoped caller widening a group beyond their own reach is escalation by another route:
    /// they would be granting access to a department they cannot themselves see.
    /// </remarks>
    public async Task<bool> CanScopeToUnitAsync(
        Guid unitId, CallerContext caller, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);

        await using var c = await _dataSource.OpenConnectionAsync(ct);

        return await c.ExecuteScalarAsync<bool>(new CommandDefinition(
            """
            SELECT federation.has_permission(
                p_user_id => @UserId, p_api_key_id => @ApiKeyId,
                p_permission => 'group.manage', p_organization_unit_id => @unitId);
            """, new { caller.UserId, caller.ApiKeyId, unitId }, cancellationToken: ct));
    }

    /// <summary>Whether a group declares any organization scope at all.</summary>
    /// <remarks>
    /// A group without one reaches every department. Only a caller who is already unscoped for
    /// the permission may create or hand out such a group.
    /// </remarks>
    public async Task<bool> HasOrganizationScopeAsync(Guid groupId, CancellationToken ct)
    {
        await using var c = await _dataSource.OpenConnectionAsync(ct);

        return await c.ExecuteScalarAsync<bool>(new CommandDefinition("""
            SELECT EXISTS (
                SELECT 1 FROM federation.group_scopes gs
                JOIN federation.scopes s ON s.id = gs.scope_id
                WHERE gs.group_id = @groupId AND s.scope_type = 'ORGANIZATION');
            """, new { groupId }, cancellationToken: ct));
    }

    /// <summary>Whether a group declares any geographic scope at all.</summary>
    /// <remarks>
    /// The geographic-dimension counterpart of <see cref="HasOrganizationScopeAsync"/>. A group
    /// without one reaches every area; only a caller already unscoped for geography on the
    /// permission may hand it out.
    /// </remarks>
    public async Task<bool> HasGeographyScopeAsync(Guid groupId, CancellationToken ct)
    {
        await using var c = await _dataSource.OpenConnectionAsync(ct);

        return await c.ExecuteScalarAsync<bool>(new CommandDefinition("""
            SELECT EXISTS (
                SELECT 1 FROM federation.group_scopes gs
                JOIN federation.scopes s ON s.id = gs.scope_id
                WHERE gs.group_id = @groupId AND s.scope_type = 'GEOGRAPHY');
            """, new { groupId }, cancellationToken: ct));
    }

    /// <summary>
    /// Whether every geographic scope on a group falls inside what the caller administers — the
    /// geographic-dimension counterpart of <see cref="GroupScopesWithinReachAsync"/>.
    /// </summary>
    /// <remarks>
    /// Same rule and same NULL-over-no-rows hazard: a group with no geographic scope is
    /// unrestricted, so an empty result is refused rather than read as trivially satisfied.
    /// </remarks>
    public async Task<bool> GroupGeographyScopesWithinReachAsync(
        Guid groupId, CallerContext caller, string permission, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);

        if (caller.UserId is null && caller.ApiKeyId is null)
        {
            return false;
        }

        await using var c = await _dataSource.OpenConnectionAsync(ct);

        return await c.ExecuteScalarAsync<bool>(new CommandDefinition("""
            SELECT COALESCE(bool_and(
                       s.geographic_area_id = ANY (
                           SELECT geographic_area_id
                           FROM federation.authorized_geographic_areas(
                                    p_user_id => @UserId, p_api_key_id => @ApiKeyId,
                                    p_permission => @permission))),
                   FALSE)
            FROM federation.group_scopes gs
            JOIN federation.scopes s ON s.id = gs.scope_id
            WHERE gs.group_id = @groupId AND s.scope_type = 'GEOGRAPHY';
            """, new { groupId, caller.UserId, caller.ApiKeyId, permission }, cancellationToken: ct));
    }

    /// <summary>
    /// How many active users would still hold a permission if one membership were revoked.
    /// </summary>
    /// <remarks>
    /// Used to refuse the change that locks everyone out: removing the last account able to
    /// administer the platform is unrecoverable without direct database access.
    /// </remarks>
    public async Task<int> CountOtherHoldersAsync(
        string permission, Guid excludingUserId, CancellationToken ct)
    {
        await using var c = await _dataSource.OpenConnectionAsync(ct);
        return await c.ExecuteScalarAsync<int>(new CommandDefinition("""
            SELECT count(DISTINCT ea.user_id)
            FROM federation.user_effective_access ea
            WHERE ea.permission_code = @permission AND ea.user_id <> @excludingUserId;
            """, new { permission, excludingUserId }, cancellationToken: ct));
    }

    private sealed class ScopeRow
    {
        public Guid GroupId { get; init; }
        public Guid Id { get; init; }
        public string ScopeType { get; init; } = "";
        public Guid? OrganizationUnitId { get; init; }
        public Guid? GeographicAreaId { get; init; }
        public string? ResourceType { get; init; }
        public Guid? ResourceId { get; init; }
        public string? Description { get; init; }
    }

    private sealed class GroupRow
    {
        public Guid Id { get; init; }
        public string Code { get; init; } = "";
        public string Name { get; init; } = "";
        public string? Description { get; init; }
        public string Status { get; init; } = "";
        public string RoleCode { get; init; } = "";
        public int MemberCount { get; init; }
    }
}
