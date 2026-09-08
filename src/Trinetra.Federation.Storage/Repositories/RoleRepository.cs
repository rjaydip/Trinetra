using Dapper;
using Npgsql;

namespace Trinetra.Federation.Storage.Repositories;

/// <summary>One permission a role composes, with its vocabulary metadata (P6).</summary>
public sealed record RolePermissionInfo(string Code, string Name, string Category, string? Description);

/// <summary>An access group that references a role (P6 <c>usedBy</c>).</summary>
public sealed record RoleUsingGroupInfo(Guid Id, string Code, string Name, string Status);

/// <summary>
/// One role with the permission codes it composes, plus lifecycle timestamps and usage
/// information. <see cref="Permissions"/> stays a bare code list — it is what the escalation
/// guard reads. <see cref="PermissionDetails"/> and <see cref="UsingGroups"/> are populated only
/// on the single-role read <c>GetAsync(id, caller, ct)</c>; the list read leaves them null and
/// carries <see cref="UsageCount"/> only.
/// </summary>
public sealed record RoleDetail(
    Guid Id, string Code, string Name, string? Description, bool IsSystem, string Status,
    bool Customized, IReadOnlyList<string> Permissions,
    DateTimeOffset CreatedAt = default,
    DateTimeOffset UpdatedAt = default,
    DateTimeOffset? CustomizedAt = null,
    int UsageCount = 0,
    IReadOnlyList<RoleUsingGroupInfo>? UsingGroups = null,
    IReadOnlyList<RolePermissionInfo>? PermissionDetails = null);

/// <summary>Why a role write could not be applied. <see cref="None"/> means it was.</summary>
public enum RoleWriteError
{
    /// <summary>Applied.</summary>
    None,

    /// <summary>The role does not exist.</summary>
    NotFound,

    /// <summary>SUPER_ADMIN — locked in the API, it is the recovery role.</summary>
    Locked,

    /// <summary>A preset's <c>code</c> cannot change, or a preset cannot be deleted.</summary>
    PresetShapeFixed,

    /// <summary>Editing / disabling a preset needs <c>role.manage</c> held UNSCOPED.</summary>
    PresetRequiresUnscoped,

    /// <summary>The caller would touch a permission they do not themselves hold.</summary>
    Escalation,

    /// <summary>Deletion refused: an access group still references this role. (No longer used — P8.)</summary>
    InUse,

    /// <summary>Another role already uses this <c>code</c>.</summary>
    CodeInUse,

    /// <summary>One of the supplied permission codes is not in the vocabulary.</summary>
    UnknownPermission,

    /// <summary>A <c>status</c> value not settable through a write (unknown, or a return to DRAFT).</summary>
    InvalidStatus,
}

/// <summary>Outcome of a role write: an error and, for <see cref="RoleWriteError.Escalation"/>, the offending codes.</summary>
public readonly record struct RoleWriteResult(
    RoleWriteError Error, IReadOnlyList<string> Exceeding, Guid Id)
{
    public static readonly RoleWriteResult Ok = new(RoleWriteError.None, [], Guid.Empty);
    public static RoleWriteResult Fail(RoleWriteError error) => new(error, [], Guid.Empty);
    public static RoleWriteResult Exceeds(IReadOnlyList<string> codes) =>
        new(RoleWriteError.Escalation, codes, Guid.Empty);
}

/// <summary>
/// Roles — the seeded presets and any custom roles composed through the API. A role is a named
/// bundle of permission codes with no scope of its own; scope is attached to the access group.
/// </summary>
/// <remarks>
/// <para>
/// Lifecycle is <c>DRAFT -&gt; ACTIVE -&gt; INACTIVE</c> (v1.12). The API creates a custom role
/// as <c>DRAFT</c>; presets are seeded <c>ACTIVE</c>. <c>DELETE</c> soft-deletes to
/// <c>INACTIVE</c> (the row and its permission rows survive). Every authz function joins
/// <c>roles r ... AND r.status = 'ACTIVE'</c>, so a <c>DRAFT</c> or <c>INACTIVE</c> role grants
/// nothing.
/// </para>
/// <para>
/// The seeded roles (<c>is_system</c>) are <b>presets</b>: their name, description and permission
/// set are editable. A preset's <c>code</c> is fixed and a preset cannot be deleted. Because a
/// role is a GLOBAL object, editing a preset changes every access group built on it in every
/// organization — so <b>editing, disabling or re-composing a preset requires <c>role.manage</c>
/// held UNSCOPED</b>. <c>SUPER_ADMIN</c> is immutable regardless.
/// </para>
/// <para>
/// Every write also enforces the privilege-escalation rule — a caller who is not unscoped for
/// <c>role.manage</c> may only touch permissions they themselves hold — and does so against the
/// role row read <c>FOR UPDATE</c>.
/// </para>
/// </remarks>
// CA1822 fires on the write methods now that they take their connection from the UnitOfWork
// rather than the data source. They stay instance methods so one entity's operations are called
// the same way regardless of which need the pool.
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Performance", "CA1822:Mark members as static",
    Justification = "Write methods take their connection from the UnitOfWork by design.",
    Scope = "type")]
public sealed class RoleRepository
{
    private const string LockedRoleCode = "SUPER_ADMIN";

    private static readonly string[] WritableStatuses = ["DRAFT", "ACTIVE", "INACTIVE"];

    private readonly NpgsqlDataSource _dataSource;

    public RoleRepository(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    /// <summary>
    /// Roles for the list endpoint. Default (<paramref name="includeInactive"/> false) returns
    /// <c>ACTIVE</c> only; <c>true</c> adds <c>DRAFT</c> and <c>INACTIVE</c>. Each row carries its
    /// permission codes and a <c>usageCount</c>, but not the per-group <c>usedBy</c> list.
    /// </summary>
    public async Task<IReadOnlyList<RoleDetail>> ListAsync(bool includeInactive, CancellationToken ct)
    {
        await using var c = await _dataSource.OpenConnectionAsync(ct);
        await using var reader = await c.QueryMultipleAsync(new CommandDefinition("""
            SELECT id, code, name, description, is_system, status,
                   (customized_at IS NOT NULL) AS customized,
                   created_at, updated_at, customized_at
            FROM federation.roles
            WHERE status = 'ACTIVE' OR (@includeInactive AND status IN ('DRAFT', 'INACTIVE'))
            ORDER BY code;

            SELECT role_id, permission_code
            FROM federation.role_permissions ORDER BY role_id, permission_code;

            SELECT role_id, count(*)::int AS usage_count
            FROM federation.access_groups GROUP BY role_id;
            """, new { includeInactive }, cancellationToken: ct));

        var roles = (await reader.ReadAsync<RoleRow>()).ToList();
        var perms = (await reader.ReadAsync<(Guid RoleId, string PermissionCode)>())
            .ToLookup(x => x.RoleId, x => x.PermissionCode);
        var usage = (await reader.ReadAsync<(Guid RoleId, int UsageCount)>())
            .ToDictionary(x => x.RoleId, x => x.UsageCount);

        return
        [
            .. roles.Select(r => r.ToDetail(
                [.. perms[r.Id]],
                usageCount: usage.TryGetValue(r.Id, out var u) ? u : 0)),
        ];
    }

    /// <summary>Every role regardless of status — internal callers only.</summary>
    public Task<IReadOnlyList<RoleDetail>> ListAsync(CancellationToken ct) => ListAsync(true, ct);

    /// <summary>
    /// One role with its permission codes, permission metadata, lifecycle timestamps and the
    /// access groups that reference it (<c>usedBy</c>), or null. <paramref name="visibleTo"/>
    /// filters <c>usedBy</c> to groups that caller may see; <c>usageCount</c> stays a true total.
    /// </summary>
    public async Task<RoleDetail?> GetAsync(Guid id, CallerContext? visibleTo, CancellationToken ct)
    {
        await using var c = await _dataSource.OpenConnectionAsync(ct);

        var groupFilter = "";
        var p = visibleTo is null
            ? new DynamicParameters()
            : AccessGroupRepository.GroupVisibilityParams(visibleTo, "group.read");
        p.Add("id", id);

        if (visibleTo is not null)
        {
            groupFilter = $"AND ({AccessGroupRepository.GroupVisiblePredicate})";
        }

        await using var reader = await c.QueryMultipleAsync(new CommandDefinition($"""
            SELECT id, code, name, description, is_system, status,
                   (customized_at IS NOT NULL) AS customized,
                   created_at, updated_at, customized_at
            FROM federation.roles WHERE id = @id;

            SELECT rp.permission_code AS code, p.name, p.category, p.description
            FROM federation.role_permissions rp
            JOIN federation.permissions p ON p.code = rp.permission_code
            WHERE rp.role_id = @id
            ORDER BY rp.permission_code;

            SELECT count(*)::int FROM federation.access_groups WHERE role_id = @id;

            SELECT ag.id, ag.code, ag.name, ag.status
            FROM federation.access_groups ag
            WHERE ag.role_id = @id {groupFilter}
            ORDER BY ag.code;
            """, p, cancellationToken: ct));

        var row = await reader.ReadSingleOrDefaultAsync<RoleRow>();
        if (row is null)
        {
            return null;
        }

        var details = (await reader.ReadAsync<RolePermissionInfo>()).ToList();
        var usageCount = await reader.ReadSingleAsync<int>();
        var usedBy = (await reader.ReadAsync<RoleUsingGroupInfo>()).ToList();

        return row.ToDetail(
            [.. details.Select(d => d.Code)],
            usageCount: usageCount,
            usingGroups: usedBy,
            permissionDetails: details);
    }

    /// <summary>One role with its permission codes, or null. No usage / metadata enrichment.</summary>
    public async Task<RoleDetail?> GetAsync(Guid id, CancellationToken ct)
    {
        await using var c = await _dataSource.OpenConnectionAsync(ct);
        return await LoadAsync(c, null, id, ct);
    }

    /// <summary>The lifecycle status of a role, or null if it does not exist (P11).</summary>
    public async Task<string?> GetStatusAsync(Guid roleId, CancellationToken ct)
    {
        await using var c = await _dataSource.OpenConnectionAsync(ct);
        return await c.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT status FROM federation.roles WHERE id = @roleId;",
            new { roleId }, cancellationToken: ct));
    }

    /// <summary>The permission codes a role grants.</summary>
    public async Task<IReadOnlySet<string>> GetPermissionsAsync(Guid roleId, CancellationToken ct)
    {
        await using var c = await _dataSource.OpenConnectionAsync(ct);
        var rows = await c.QueryAsync<string>(new CommandDefinition("""
            SELECT permission_code FROM federation.role_permissions WHERE role_id = @roleId;
            """, new { roleId }, cancellationToken: ct));
        return rows.ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Creates a custom role (<c>is_system = FALSE</c>). Defaults to <c>DRAFT</c>.</summary>
    public async Task<RoleWriteResult> CreateAsync(
        string code, string name, string? description, string? status,
        IReadOnlyCollection<string> permissions, CallerContext caller, UnitOfWork work,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var c = work.Connection;

        if (status is not null && Array.IndexOf(WritableStatuses, status) < 0)
        {
            return RoleWriteResult.Fail(RoleWriteError.InvalidStatus);
        }

        var escalation = Escalation(caller, permissions);
        if (escalation is { } exceeding)
        {
            return RoleWriteResult.Exceeds(exceeding);
        }

        if (await c.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT EXISTS (SELECT 1 FROM federation.roles WHERE code = @code);",
                new { code }, work.Transaction, cancellationToken: ct)))
        {
            return RoleWriteResult.Fail(RoleWriteError.CodeInUse);
        }

        if (await UnknownPermissionAsync(c, work.Transaction, permissions, ct))
        {
            return RoleWriteResult.Fail(RoleWriteError.UnknownPermission);
        }

        // New custom roles are DRAFT: an operator composes, then flips to ACTIVE via PUT.
        var id = await c.ExecuteScalarAsync<Guid>(new CommandDefinition("""
            INSERT INTO federation.roles (code, name, description, is_system, status)
            VALUES (@code, @name, @description, FALSE, COALESCE(@status, 'DRAFT'))
            RETURNING id;
            """, new { code, name, description, status }, work.Transaction, cancellationToken: ct));

        await ReplacePermissionsAsync(c, work.Transaction, id, permissions, ct);

        return new RoleWriteResult(RoleWriteError.None, [], id);
    }

    /// <summary>
    /// Edits a role's name, description, status and permission set. A preset (<c>is_system</c>)
    /// is edited in place and marked <c>customized_at</c> only if the content actually changed;
    /// <c>SUPER_ADMIN</c> is refused, and preset edits require <c>role.manage</c> held unscoped.
    /// A write may set <c>DRAFT</c> / <c>ACTIVE</c> / <c>INACTIVE</c>; it may not return a role
    /// that has left <c>DRAFT</c> back to it.
    /// </summary>
    public async Task<RoleWriteResult> UpdateAsync(
        Guid id, string name, string? description, string? status,
        IReadOnlyCollection<string> permissions, CallerContext caller, UnitOfWork work,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var c = work.Connection;

        var role = await LockAsync(c, work.Transaction, id, ct);
        if (role is null)
        {
            return RoleWriteResult.Fail(RoleWriteError.NotFound);
        }

        if (role.Code == LockedRoleCode)
        {
            return RoleWriteResult.Fail(RoleWriteError.Locked);
        }

        if (status is not null)
        {
            if (Array.IndexOf(WritableStatuses, status) < 0)
            {
                return RoleWriteResult.Fail(RoleWriteError.InvalidStatus);
            }

            // A role that was ever live must not pretend to be a draft again.
            if (status == "DRAFT" && role.Status != "DRAFT")
            {
                return RoleWriteResult.Fail(RoleWriteError.InvalidStatus);
            }
        }

        if (role.IsSystem && !caller.IsUnscopedFor("role.manage"))
        {
            return RoleWriteResult.Fail(RoleWriteError.PresetRequiresUnscoped);
        }

        // Escalation is checked against the LOCKED prior set ∪ the new set.
        var prior = role.Permissions;
        var escalation = Escalation(caller, [.. permissions, .. prior]);
        if (escalation is { } exceeding)
        {
            return RoleWriteResult.Exceeds(exceeding);
        }

        if (await UnknownPermissionAsync(c, work.Transaction, permissions, ct))
        {
            return RoleWriteResult.Fail(RoleWriteError.UnknownPermission);
        }

        var contentChanged =
            role.Name != name
            || (role.Description ?? "") != (description ?? "")
            || !new HashSet<string>(prior, StringComparer.Ordinal).SetEquals(permissions);

        await c.ExecuteAsync(new CommandDefinition("""
            UPDATE federation.roles
            SET name = @name, description = @description,
                status = COALESCE(@status, status),
                customized_at = CASE
                    WHEN is_system AND @contentChanged THEN COALESCE(customized_at, now())
                    ELSE customized_at
                END,
                updated_at = now()
            WHERE id = @id;
            """, new { id, name, description, status, contentChanged },
            work.Transaction, cancellationToken: ct));

        await ReplacePermissionsAsync(c, work.Transaction, id, permissions, ct);

        return RoleWriteResult.Ok;
    }

    /// <summary>
    /// Soft-deletes a role: sets <c>status = 'INACTIVE'</c> (P8). The row and its
    /// <c>role_permissions</c> are kept, so a later <c>PUT status = ACTIVE</c> restores it. A
    /// preset cannot be deleted; <c>SUPER_ADMIN</c> cannot be touched. A role still referenced by
    /// access groups <b>can</b> be soft-deleted — those groups keep their <c>role_id</c> and
    /// simply grant nothing onward (authz already gates <c>role.status = 'ACTIVE'</c>). Idempotent
    /// on an already-<c>INACTIVE</c> role.
    /// </summary>
    public async Task<RoleWriteResult> DeleteAsync(
        Guid id, CallerContext caller, UnitOfWork work, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var c = work.Connection;

        var role = await LockAsync(c, work.Transaction, id, ct);
        if (role is null)
        {
            return RoleWriteResult.Fail(RoleWriteError.NotFound);
        }

        if (role.Code == LockedRoleCode)
        {
            return RoleWriteResult.Fail(RoleWriteError.Locked);
        }

        if (role.IsSystem)
        {
            return RoleWriteResult.Fail(RoleWriteError.PresetShapeFixed);
        }

        // Removing capability is never an escalation, but keep the same "may only touch a role
        // whose permissions you all hold" rule as edit, for symmetry.
        var escalation = Escalation(caller, role.Permissions);
        if (escalation is { } exceeding)
        {
            return RoleWriteResult.Exceeds(exceeding);
        }

        if (role.Status == "INACTIVE")
        {
            return RoleWriteResult.Ok;
        }

        await c.ExecuteAsync(new CommandDefinition("""
            UPDATE federation.roles SET status = 'INACTIVE', updated_at = now() WHERE id = @id;
            """, new { id }, work.Transaction, cancellationToken: ct));

        return RoleWriteResult.Ok;
    }

    /// <summary>The permissions in <paramref name="codes"/> the caller does not hold, or null if none.</summary>
    private static List<string>? Escalation(CallerContext caller, IEnumerable<string> codes)
    {
        if (caller.IsUnscopedFor("role.manage"))
        {
            return null;
        }

        var exceeding = codes.Where(p => !caller.Has(p)).Distinct(StringComparer.Ordinal)
                             .OrderBy(p => p, StringComparer.Ordinal).ToList();
        return exceeding.Count == 0 ? null : exceeding;
    }

    private static async Task<RoleDetail?> LockAsync(
        NpgsqlConnection c, NpgsqlTransaction tx, Guid id, CancellationToken ct)
    {
        var row = await c.QuerySingleOrDefaultAsync<RoleRow>(new CommandDefinition("""
            SELECT id, code, name, description, is_system, status,
                   (customized_at IS NOT NULL) AS customized,
                   created_at, updated_at, customized_at
            FROM federation.roles WHERE id = @id FOR UPDATE;
            """, new { id }, tx, cancellationToken: ct));

        if (row is null)
        {
            return null;
        }

        var perms = await c.QueryAsync<string>(new CommandDefinition("""
            SELECT permission_code FROM federation.role_permissions
            WHERE role_id = @id ORDER BY permission_code;
            """, new { id }, tx, cancellationToken: ct));

        return row.ToDetail([.. perms]);
    }

    private static async Task<RoleDetail?> LoadAsync(
        NpgsqlConnection c, NpgsqlTransaction? tx, Guid id, CancellationToken ct)
    {
        var row = await c.QuerySingleOrDefaultAsync<RoleRow>(new CommandDefinition("""
            SELECT id, code, name, description, is_system, status,
                   (customized_at IS NOT NULL) AS customized,
                   created_at, updated_at, customized_at
            FROM federation.roles WHERE id = @id;
            """, new { id }, tx, cancellationToken: ct));

        if (row is null)
        {
            return null;
        }

        var perms = await c.QueryAsync<string>(new CommandDefinition("""
            SELECT permission_code FROM federation.role_permissions
            WHERE role_id = @id ORDER BY permission_code;
            """, new { id }, tx, cancellationToken: ct));

        return row.ToDetail([.. perms]);
    }

    private static async Task<bool> UnknownPermissionAsync(
        NpgsqlConnection c, NpgsqlTransaction tx, IReadOnlyCollection<string> permissions,
        CancellationToken ct)
    {
        var distinct = permissions.Distinct(StringComparer.Ordinal).ToArray();
        if (distinct.Length == 0)
        {
            return false;
        }

        var known = await c.ExecuteScalarAsync<long>(new CommandDefinition("""
            SELECT count(*) FROM federation.permissions WHERE code = ANY (@distinct);
            """, new { distinct }, tx, cancellationToken: ct));

        return known != distinct.Length;
    }

    private static async Task ReplacePermissionsAsync(
        NpgsqlConnection c, NpgsqlTransaction tx, Guid roleId,
        IReadOnlyCollection<string> permissions, CancellationToken ct)
    {
        await c.ExecuteAsync(new CommandDefinition(
            "DELETE FROM federation.role_permissions WHERE role_id = @roleId;",
            new { roleId }, tx, cancellationToken: ct));

        var distinct = permissions.Distinct(StringComparer.Ordinal).ToArray();
        if (distinct.Length == 0)
        {
            return;
        }

        await c.ExecuteAsync(new CommandDefinition("""
            INSERT INTO federation.role_permissions (role_id, permission_code)
            SELECT @roleId, unnest(@distinct::text[])
            ON CONFLICT DO NOTHING;
            """, new { roleId, distinct }, tx, cancellationToken: ct));
    }

    private sealed record RoleRow
    {
        public Guid Id { get; init; }
        public string Code { get; init; } = "";
        public string Name { get; init; } = "";
        public string? Description { get; init; }
        public bool IsSystem { get; init; }
        public string Status { get; init; } = "ACTIVE";
        public bool Customized { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset UpdatedAt { get; init; }
        public DateTimeOffset? CustomizedAt { get; init; }

        public RoleDetail ToDetail(
            IReadOnlyList<string> permissions,
            int usageCount = 0,
            IReadOnlyList<RoleUsingGroupInfo>? usingGroups = null,
            IReadOnlyList<RolePermissionInfo>? permissionDetails = null) =>
            new(Id, Code, Name, Description, IsSystem, Status, Customized, permissions,
                CreatedAt, UpdatedAt, CustomizedAt, usageCount, usingGroups, permissionDetails);
    }
}
