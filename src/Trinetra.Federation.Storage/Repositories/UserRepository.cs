using Dapper;
using Npgsql;
using Trinetra.Federation.Storage.Security;

namespace Trinetra.Federation.Storage.Repositories;

/// <summary>Everything the login path needs about a user, including hash material.</summary>
/// <remarks>
/// Deliberately separate from <see cref="PlatformUser"/> and never returned by a read endpoint.
/// Keeping hash and salt off the type the API serialises means they cannot be leaked by adding a
/// field to a response later.
/// </remarks>
public sealed record UserCredentials(
    Guid Id, string Username, string DisplayName, PasswordHash Password,
    bool MustChangePassword, string Status, int FailedLoginCount, DateTimeOffset? LockedUntil);

/// <summary>Users, their credentials, and their effective permissions.</summary>
// CA1822 fires on the write methods now that they take their connection from the UnitOfWork
// rather than the data source. That is the point of the change, not a defect: they stay instance
// methods so one entity's operations are called the same way regardless of which need the pool.
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Performance", "CA1822:Mark members as static",
    Justification = "Write methods take their connection from the UnitOfWork by design.",
    Scope = "type")]
public sealed class UserRepository
{
    /// <summary>Failed attempts before an account locks.</summary>
    private const int MaxFailedLogins = 10;

    /// <summary>How long a lockout lasts. Long enough to defeat automation, short enough that a
    /// genuine user is not stranded waiting for an administrator.</summary>
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    private readonly NpgsqlDataSource _dataSource;

    public UserRepository(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    /// <summary>Loads credentials by id, for a caller already authenticated.</summary>
    /// <remarks>
    /// Separate from the username lookup on purpose. A password change must resolve the user
    /// from the token's subject, not from a display name — those can differ, and matching on the
    /// wrong one fails in a way that reads as "wrong password" rather than "wrong user".
    /// </remarks>
    public async Task<UserCredentials?> FindForLoginByIdAsync(
        Guid id, CancellationToken ct)
    {
        await using var c = await _dataSource.OpenConnectionAsync(ct);

        var row = await c.QuerySingleOrDefaultAsync<LoginRow>(new CommandDefinition("""
            SELECT id, username, display_name, password_hash, password_salt,
                   password_iterations, password_algorithm, must_change_password,
                   status, failed_login_count, locked_until
            FROM federation.platform_users WHERE id = @id;
            """, new { id }, cancellationToken: ct));

        return row is null ? null : new UserCredentials(
            row.Id, row.Username, row.DisplayName,
            new PasswordHash(row.PasswordHash, row.PasswordSalt, row.PasswordIterations,
                row.PasswordAlgorithm),
            row.MustChangePassword, row.Status, row.FailedLoginCount, row.LockedUntil);
    }

    public async Task<UserCredentials?> FindForLoginAsync(
        string username, CancellationToken ct)
    {
        await using var c = await _dataSource.OpenConnectionAsync(ct);

        var row = await c.QuerySingleOrDefaultAsync<LoginRow>(new CommandDefinition("""
            SELECT id, username, display_name, password_hash, password_salt,
                   password_iterations, password_algorithm, must_change_password,
                   status, failed_login_count, locked_until
            FROM federation.platform_users
            WHERE lower(username) = lower(@username);
            """, new { username }, cancellationToken: ct));

        return row is null ? null : new UserCredentials(
            row.Id, row.Username, row.DisplayName,
            new PasswordHash(row.PasswordHash, row.PasswordSalt, row.PasswordIterations,
                row.PasswordAlgorithm),
            row.MustChangePassword, row.Status, row.FailedLoginCount, row.LockedUntil);
    }

    /// <summary>
    /// Records a failed attempt, locking the account once the threshold is reached.
    /// </summary>
    /// <remarks>
    /// Lockout is time-based rather than permanent. A permanent lock turns a forgotten password
    /// into an administrator ticket, and turns an attacker into a denial-of-service against every
    /// account they can name.
    /// </remarks>
    public async Task RecordFailedLoginAsync(Guid userId, CancellationToken ct)
    {
        await using var c = await _dataSource.OpenConnectionAsync(ct);
        await c.ExecuteAsync(new CommandDefinition("""
            UPDATE federation.platform_users
            SET failed_login_count = failed_login_count + 1,
                locked_until = CASE WHEN failed_login_count + 1 >= @MaxFailed
                                    THEN now() + @Lockout ELSE locked_until END,
                updated_at = now()
            WHERE id = @userId;
            """, new { userId, MaxFailed = MaxFailedLogins, Lockout = LockoutDuration },
            cancellationToken: ct));
    }

    public async Task RecordSuccessfulLoginAsync(Guid userId, CancellationToken ct)
    {
        await using var c = await _dataSource.OpenConnectionAsync(ct);
        await c.ExecuteAsync(new CommandDefinition("""
            UPDATE federation.platform_users
            SET failed_login_count = 0, locked_until = NULL,
                last_login_at = now(), updated_at = now()
            WHERE id = @userId;
            """, new { userId }, cancellationToken: ct));
    }

    public async Task SetPasswordAsync(
        Guid userId, PasswordHash password, bool mustChange, UnitOfWork work, CancellationToken ct)
    {
        var c = work.Connection;
        await c.ExecuteAsync(new CommandDefinition("""
            UPDATE federation.platform_users
            SET password_hash = @Hash, password_salt = @Salt,
                password_iterations = @Iterations, password_algorithm = @Algorithm,
                must_change_password = @mustChange,
                failed_login_count = 0, locked_until = NULL, updated_at = now()
            WHERE id = @userId;
            """, new
        {
            userId, mustChange, password.Hash, password.Salt,
            password.Iterations, password.Algorithm,
        }, work.Transaction, cancellationToken: ct));
    }

    public async Task<Guid> CreateAsync(
        string username, string displayName, string? email, PasswordHash password,
        bool mustChangePassword, bool isSystem, UnitOfWork work, CancellationToken ct)
    {
        var c = work.Connection;
        return await c.ExecuteScalarAsync<Guid>(new CommandDefinition("""
            INSERT INTO federation.platform_users
                (username, display_name, email, password_hash, password_salt,
                 password_iterations, password_algorithm, must_change_password, is_system)
            VALUES (@username, @displayName, @email, @Hash, @Salt,
                    @Iterations, @Algorithm, @mustChangePassword, @isSystem)
            RETURNING id;
            """, new
        {
            username, displayName, email, mustChangePassword, isSystem,
            password.Hash, password.Salt, password.Iterations, password.Algorithm,
        }, work.Transaction, cancellationToken: ct));
    }

    public async Task<bool> ExistsAsync(string username, CancellationToken ct)
    {
        await using var c = await _dataSource.OpenConnectionAsync(ct);
        return await c.ExecuteScalarAsync<bool>(new CommandDefinition("""
            SELECT EXISTS (SELECT 1 FROM federation.platform_users
                           WHERE lower(username) = lower(@username));
            """, new { username }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<PlatformUser>> ListAsync(CancellationToken ct)
    {
        await using var c = await _dataSource.OpenConnectionAsync(ct);
        var rows = await c.QueryAsync<PlatformUser>(new CommandDefinition("""
            SELECT id, username, display_name, email, must_change_password,
                   status, last_login_at, is_system
            FROM federation.platform_users ORDER BY username;
            """, cancellationToken: ct));
        return rows.ToList();
    }

    /// <summary>
    /// The permissions a user holds, unioned across all their active groups.
    /// </summary>
    /// <remarks>
    /// This answers "what may they do", not "where". Scope is resolved per request against the
    /// live hierarchy, so a unit created moments ago is already in scope — baking scope into a
    /// token would make it stale the moment the hierarchy changed.
    /// </remarks>
    public async Task<IReadOnlySet<string>> GetPermissionsAsync(
        Guid userId, CancellationToken ct)
    {
        await using var c = await _dataSource.OpenConnectionAsync(ct);
        var rows = await c.QueryAsync<string>(new CommandDefinition("""
            SELECT DISTINCT permission_code FROM federation.user_effective_access
            WHERE user_id = @userId;
            """, new { userId }, cancellationToken: ct));
        return rows.ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// The permissions a user exercises across the whole estate, because they hold them through
    /// a group that declares no organization scope.
    /// </summary>
    /// <remarks>
    /// Returned as a set rather than a single answer for one permission. Collapsing this to one
    /// flag was a real vulnerability: it was computed for one permission and then honoured as a
    /// bypass for every other, so an unscoped read silently conferred unscoped administration.
    /// </remarks>
    /// <summary>Permissions held without GEOGRAPHIC scope.</summary>
    public async Task<IReadOnlySet<string>> GetUnscopedGeographyAsync(Guid userId, CancellationToken ct)
    {
        await using var c = await _dataSource.OpenConnectionAsync(ct);

        var rows = await c.QueryAsync<string>(new CommandDefinition(
            """
            SELECT rp.permission_code
            FROM federation.principal_groups(@userId, NULL) pg
            JOIN federation.access_groups ag    ON ag.id = pg.group_id
            JOIN federation.roles r             ON r.id = ag.role_id AND r.status = 'ACTIVE'
            JOIN federation.role_permissions rp ON rp.role_id = r.id
            WHERE NOT EXISTS (
                SELECT 1 FROM federation.group_scopes gs
                JOIN federation.scopes s ON s.id = gs.scope_id
                WHERE gs.group_id = ag.id AND s.scope_type = 'GEOGRAPHY');
            """, new { userId }, cancellationToken: ct));

        return rows.ToHashSet(StringComparer.Ordinal);
    }

    public async Task<IReadOnlySet<string>> GetUnscopedPermissionsAsync(
        Guid userId, CancellationToken ct)
    {
        await using var c = await _dataSource.OpenConnectionAsync(ct);

        var rows = await c.QueryAsync<string>(new CommandDefinition(
            """
            SELECT permission_code FROM federation.unscoped_permissions(
                p_user_id => @userId, p_api_key_id => NULL);
            """, new { userId }, cancellationToken: ct));

        return rows.ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// Whether the caller may administer this user at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Holding <c>user.manage</c> is necessary but nowhere near sufficient. Without this check a
    /// department-scoped administrator could reset the bootstrap administrator's password, log in
    /// as it, and hold the whole platform — a takeover from the lowest privilege that can manage
    /// users at all.
    /// </para>
    /// <para>Three conditions, all of which must hold:</para>
    /// <list type="number">
    /// <item>The target holds no permission the caller lacks.</item>
    /// <item>The target holds nothing through an organization-unscoped group, unless the caller
    /// is likewise unscoped — otherwise a scoped caller could administer someone whose reach
    /// exceeds their own.</item>
    /// <item>Every organization scope the target holds falls inside the caller's own reach.</item>
    /// </list>
    /// <para>
    /// A user with no groups at all is administrable by anyone with the permission, which is
    /// correct: a fresh account grants nothing until it is placed in a group.
    /// </para>
    /// </remarks>
    public async Task<bool> CanAdministerAsync(
        Guid targetUserId, Guid? callerUserId, bool callerIsUnscoped,
        CancellationToken ct)
    {
        // A caller with no user identity (an API key) cannot administer people.
        if (callerUserId is null)
        {
            return false;
        }

        // Administering yourself is always permitted — rotating your own password must not
        // depend on out-scoping yourself.
        if (callerUserId == targetUserId)
        {
            return true;
        }

        await using var c = await _dataSource.OpenConnectionAsync(ct);

        return await c.ExecuteScalarAsync<bool>(new CommandDefinition("""
            SELECT
                -- 1. The target holds nothing the caller lacks.
                NOT EXISTS (
                    SELECT permission_code FROM federation.user_effective_access
                    WHERE user_id = @targetUserId
                    EXCEPT
                    SELECT permission_code FROM federation.user_effective_access
                    WHERE user_id = @callerUserId)
                -- 2. The target is not unscoped, unless the caller is too.
                AND (@callerIsUnscoped OR NOT EXISTS (
                    SELECT 1
                    FROM federation.user_groups ug
                    JOIN federation.access_groups ag ON ag.id = ug.group_id AND ag.status = 'ACTIVE'
                    WHERE ug.user_id = @targetUserId AND ug.status = 'ACTIVE'
                      AND (ug.expires_at IS NULL OR ug.expires_at > now())
                      AND NOT EXISTS (
                          SELECT 1 FROM federation.group_scopes gs
                          JOIN federation.scopes s ON s.id = gs.scope_id
                          WHERE gs.group_id = ag.id AND s.scope_type = 'ORGANIZATION')))
                -- 3. Every unit the target reaches is one the caller administers.
                AND NOT EXISTS (
                    SELECT 1
                    FROM federation.user_effective_access ea
                    WHERE ea.user_id = @targetUserId
                      AND ea.scope_type = 'ORGANIZATION'
                      AND ea.organization_unit_id IS NOT NULL
                      AND ea.organization_unit_id <> ALL (
                          SELECT organization_unit_id
                          FROM federation.authorized_org_units(
                                   p_user_id => @callerUserId, p_api_key_id => NULL,
                                   p_permission => 'user.manage')));
            """, new { targetUserId, callerUserId, callerIsUnscoped }, cancellationToken: ct));
    }

    /// <summary>Whether a user is the platform's own seeded account.</summary>
    /// <remarks>
    /// Only an unscoped caller may touch it. It is the account every other one was created from,
    /// and losing control of it is unrecoverable without database access.
    /// </remarks>
    public async Task<bool> IsSystemAccountAsync(Guid userId, CancellationToken ct)
    {
        await using var c = await _dataSource.OpenConnectionAsync(ct);
        return await c.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT COALESCE((SELECT is_system FROM federation.platform_users WHERE id = @userId), FALSE);",
            new { userId }, cancellationToken: ct));
    }

    /// <summary>Grants a user membership of a group.</summary>
    public async Task AssignGroupAsync(
        Guid userId, Guid groupId, Guid? assignedBy, DateTimeOffset? expiresAt, UnitOfWork work, CancellationToken ct)
    {
        var c = work.Connection;
        await c.ExecuteAsync(new CommandDefinition("""
            INSERT INTO federation.user_groups (user_id, group_id, assigned_by, expires_at)
            VALUES (@userId, @groupId, @assignedBy, @expiresAt)
            ON CONFLICT (user_id, group_id) DO UPDATE
            SET status = 'ACTIVE', expires_at = EXCLUDED.expires_at, assigned_at = now();
            """, new { userId, groupId, assignedBy, expiresAt }, work.Transaction, cancellationToken: ct));
    }

    public async Task<PlatformUser?> GetAsync(Guid id, CancellationToken ct)
    {
        await using var c = await _dataSource.OpenConnectionAsync(ct);
        return await c.QuerySingleOrDefaultAsync<PlatformUser>(new CommandDefinition("""
            SELECT id, username, display_name, email, must_change_password,
                   status, last_login_at, is_system
            FROM federation.platform_users WHERE id = @id;
            """, new { id }, cancellationToken: ct));
    }

    /// <summary>Updates the fields a user administrator may change. Never touches the password.</summary>
    public async Task<bool> UpdateAsync(
        Guid id, string displayName, string? email, string status, UnitOfWork work, CancellationToken ct)
    {
        var c = work.Connection;
        var affected = await c.ExecuteAsync(new CommandDefinition("""
            UPDATE federation.platform_users
            SET display_name = @displayName, email = @email, status = @status, updated_at = now()
            WHERE id = @id;
            """, new { id, displayName, email, status }, work.Transaction, cancellationToken: ct));
        return affected > 0;
    }

    /// <summary>The groups a user currently belongs to.</summary>
    public async Task<IReadOnlyList<(Guid GroupId, string Code, string Name, DateTimeOffset? ExpiresAt)>>
        ListGroupsAsync(Guid userId, CancellationToken ct)
    {
        await using var c = await _dataSource.OpenConnectionAsync(ct);
        var rows = await c.QueryAsync<(Guid, string, string, DateTimeOffset?)>(new CommandDefinition("""
            SELECT ag.id, ag.code, ag.name, ug.expires_at
            FROM federation.user_groups ug
            JOIN federation.access_groups ag ON ag.id = ug.group_id
            WHERE ug.user_id = @userId AND ug.status = 'ACTIVE'
              AND (ug.expires_at IS NULL OR ug.expires_at > now())
            ORDER BY ag.code;
            """, new { userId }, cancellationToken: ct));
        return rows.ToList();
    }

    private sealed class LoginRow
    {
        public Guid Id { get; init; }
        public string Username { get; init; } = "";
        public string DisplayName { get; init; } = "";
        public byte[] PasswordHash { get; init; } = [];
        public byte[] PasswordSalt { get; init; } = [];
        public int PasswordIterations { get; init; }
        public string PasswordAlgorithm { get; init; } = "";
        public bool MustChangePassword { get; init; }
        public string Status { get; init; } = "";
        public int FailedLoginCount { get; init; }
        public DateTimeOffset? LockedUntil { get; init; }
    }
}
