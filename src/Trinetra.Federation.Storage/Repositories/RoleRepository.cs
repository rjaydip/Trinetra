using Dapper;
using Npgsql;

namespace Trinetra.Federation.Storage.Repositories;

/// <summary>One role with the permission codes it composes.</summary>
public sealed record RoleDetail(
    Guid Id, string Code, string Name, string? Description, bool IsSystem, string Status,
    bool Customized, IReadOnlyList<string> Permissions);

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

    /// <summary>Deletion refused: an access group still references this role.</summary>
    InUse,

    /// <summary>Another role already uses this <c>code</c>.</summary>
    CodeInUse,

    /// <summary>One of the supplied permission codes is not in the vocabulary.</summary>
    UnknownPermission,
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
/// The seeded roles (<c>is_system</c>) are <b>presets</b>: their name, description and permission
/// set are editable. A preset's <c>code</c> is fixed and a preset cannot be deleted. Because a
/// role is a GLOBAL object, editing a preset changes every access group built on it in every
/// organization — so <b>editing, disabling or re-composing a preset requires <c>role.manage</c>
/// held UNSCOPED</b>. <c>SUPER_ADMIN</c> is immutable regardless — the startup backfill and the
/// platform-admin group depend on it.
/// </para>
/// <para>
/// Every write also enforces the privilege-escalation rule — a caller who is not unscoped for
/// <c>role.manage</c> may only touch permissions they themselves hold — and does so against the
/// role row read <c>FOR UPDATE</c>, so a concurrent edit cannot slip a permission past the check.
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

    private readonly NpgsqlDataSource _dataSource;

    public RoleRepository(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    /// <summary>Every role, active or not, each with its permission codes.</summary>
    public async Task<IReadOnlyList<RoleDetail>> ListAsync(CancellationToken ct)
    {
        await using var c = await _dataSource.OpenConnectionAsync(ct);
        await using var reader = await c.QueryMultipleAsync(new CommandDefinition("""
            SELECT id, code, name, description, is_system, status,
                   (customized_at IS NOT NULL) AS customized
            FROM federation.roles ORDER BY code;

            SELECT role_id, permission_code
            FROM federation.role_permissions ORDER BY role_id, permission_code;
            """, cancellationToken: ct));

        var roles = (await reader.ReadAsync<RoleRow>()).ToList();
        var perms = (await reader.ReadAsync<(Guid RoleId, string PermissionCode)>())
            .ToLookup(x => x.RoleId, x => x.PermissionCode);

        return [.. roles.Select(r => r.ToDetail([.. perms[r.Id]]))];
    }

    /// <summary>One role with its permission codes, or null.</summary>
    public async Task<RoleDetail?> GetAsync(Guid id, CancellationToken ct)
    {
        await using var c = await _dataSource.OpenConnectionAsync(ct);
        return await LoadAsync(c, null, id, ct);
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

    /// <summary>Creates a custom role (<c>is_system = FALSE</c>) with the given permission set.</summary>
    public async Task<RoleWriteResult> CreateAsync(
        string code, string name, string? description, string? status,
        IReadOnlyCollection<string> permissions, CallerContext caller, UnitOfWork work,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var c = work.Connection;

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

        var id = await c.ExecuteScalarAsync<Guid>(new CommandDefinition("""
            INSERT INTO federation.roles (code, name, description, is_system, status)
            VALUES (@code, @name, @description, FALSE, COALESCE(@status, 'ACTIVE'))
            RETURNING id;
            """, new { code, name, description, status }, work.Transaction, cancellationToken: ct));

        await ReplacePermissionsAsync(c, work.Transaction, id, permissions, ct);

        return new RoleWriteResult(RoleWriteError.None, [], id);
    }

    /// <summary>
    /// Edits a role's name, description, status and permission set. A preset (<c>is_system</c>)
    /// is edited in place and marked <c>customized_at</c> only if the content actually changed;
    /// <c>SUPER_ADMIN</c> is refused, and preset edits require <c>role.manage</c> held unscoped.
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

        if (role.IsSystem && !caller.IsUnscopedFor("role.manage"))
        {
            return RoleWriteResult.Fail(RoleWriteError.PresetRequiresUnscoped);
        }

        // Escalation is checked against the LOCKED prior set ∪ the new set — a concurrent edit
        // cannot have added a permission the caller then silently strips or keeps.
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
            || !new HashSet<string>(prior, StringComparer.Ordinal)
                    .SetEquals(permissions);

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

    /// <summary>Deletes a custom role. A preset cannot be deleted; a role in use cannot either.</summary>
    public async Task<RoleWriteResult> DeleteAsync(Guid id, UnitOfWork work, CancellationToken ct)
    {
        var c = work.Connection;

        var role = await LockAsync(c, work.Transaction, id, ct);
        if (role is null)
        {
            return RoleWriteResult.Fail(RoleWriteError.NotFound);
        }

        if (role.Code == LockedRoleCode || role.IsSystem)
        {
            return RoleWriteResult.Fail(RoleWriteError.PresetShapeFixed);
        }

        var inUse = await c.ExecuteScalarAsync<bool>(new CommandDefinition("""
            SELECT EXISTS (SELECT 1 FROM federation.access_groups WHERE role_id = @id);
            """, new { id }, work.Transaction, cancellationToken: ct));

        if (inUse)
        {
            return RoleWriteResult.Fail(RoleWriteError.InUse);
        }

        // role_permissions cascades on the FK.
        await c.ExecuteAsync(new CommandDefinition(
            "DELETE FROM federation.roles WHERE id = @id;",
            new { id }, work.Transaction, cancellationToken: ct));

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
                   (customized_at IS NOT NULL) AS customized
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
                   (customized_at IS NOT NULL) AS customized
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

        public RoleDetail ToDetail(IReadOnlyList<string> permissions) =>
            new(Id, Code, Name, Description, IsSystem, Status, Customized, permissions);
    }
}
