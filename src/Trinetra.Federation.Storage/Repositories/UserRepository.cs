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
    bool MustChangePassword, string Status, int FailedLoginCount, DateTimeOffset? LockedUntil,
    int TokenVersion);

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

    /// <summary>
    /// A boolean SQL fragment, true when the platform user aliased <c>pu</c> is visible to a
    /// caller exercising <c>user.read</c>. Same rule as <see cref="CanAdministerAsync"/>
    /// conditions 2 and 3 (organization dimension only), plus the system account excluded.
    /// Parameters: <c>@UserVisUnscoped</c>, <c>@UserVisUserId</c>, <c>@UserVisApiKeyId</c>.
    /// Not user input — a fixed fragment, interpolated the way <c>CameraRepository.Scope</c> is;
    /// reused by <c>AccessGroupRepository.ListMembersAsync</c> so a group's roster is filtered
    /// the same way the user directory is.
    /// </summary>
    public const string VisibleForUserReadPredicate = """
        (@UserVisUnscoped OR (
            NOT pu.is_system
            AND NOT EXISTS (
                SELECT 1
                FROM federation.user_groups ug
                JOIN federation.access_groups ag ON ag.id = ug.group_id AND ag.status = 'ACTIVE'
                WHERE ug.user_id = pu.id AND ug.status = 'ACTIVE'
                  AND (ug.expires_at IS NULL OR ug.expires_at > now())
                  AND NOT EXISTS (
                      SELECT 1 FROM federation.group_scopes gs
                      JOIN federation.scopes s ON s.id = gs.scope_id
                      WHERE gs.group_id = ag.id AND s.scope_type = 'ORGANIZATION'))
            AND NOT EXISTS (
                SELECT 1
                FROM federation.user_effective_access ea
                WHERE ea.user_id = pu.id
                  AND ea.scope_type = 'ORGANIZATION'
                  AND ea.organization_unit_id IS NOT NULL
                  AND ea.organization_unit_id <> ALL (
                      SELECT organization_unit_id
                      FROM federation.authorized_org_units(
                               p_user_id => @UserVisUserId, p_api_key_id => @UserVisApiKeyId,
                               p_permission => 'user.read')))))
        """;

    /// <summary>Parameters <see cref="VisibleForUserReadPredicate"/> needs for <paramref name="caller"/>.</summary>
    public static DynamicParameters UserVisibilityParams(CallerContext caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var p = new DynamicParameters();
        p.Add("UserVisUnscoped", caller.IsUnscopedFor("user.read"));
        p.Add("UserVisUserId", caller.UserId);
        p.Add("UserVisApiKeyId", caller.ApiKeyId);
        return p;
    }

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
                   status, failed_login_count, locked_until, token_version
            FROM federation.platform_users WHERE id = @id;
            """, new { id }, cancellationToken: ct));

        return row is null ? null : new UserCredentials(
            row.Id, row.Username, row.DisplayName,
            new PasswordHash(row.PasswordHash, row.PasswordSalt, row.PasswordIterations,
                row.PasswordAlgorithm),
            row.MustChangePassword, row.Status, row.FailedLoginCount, row.LockedUntil,
            row.TokenVersion);
    }

    /// <summary>
    /// The current <c>token_version</c> for an ACTIVE user, or <see langword="null"/> when the
    /// user is unknown or not ACTIVE (INACTIVE / any non-active status). Called once per
    /// authenticated request by the JWT <c>OnTokenValidated</c> handler; a null or mismatched
    /// result fails validation into the same generic 401 as an expired token. (Lockout is not
    /// checked here — it blocks a new login, not an existing session.)
    /// </summary>
    public async Task<int?> GetTokenVersionAsync(Guid userId, CancellationToken ct)
    {
        await using var c = await _dataSource.OpenConnectionAsync(ct);
        return await c.ExecuteScalarAsync<int?>(new CommandDefinition("""
            SELECT token_version FROM federation.platform_users
            WHERE id = @userId AND status = 'ACTIVE';
            """, new { userId }, cancellationToken: ct));
    }

    /// <summary>
    /// Increments <c>token_version</c> — invalidating every access token already issued to this
    /// user — and returns the new value. Pairs with
    /// <c>RefreshTokenRepository.RevokeAllForUserAsync</c> in the same <see cref="UnitOfWork"/>;
    /// together they are one "end every session" operation (see <c>SessionRevocation</c>). The
    /// return value is <c>RETURNING</c>ed rather than re-read on a fresh connection so a caller
    /// that then issues a token in the same transaction stamps it with the post-bump version.
    /// </summary>
    public async Task<int> BumpTokenVersionAsync(Guid userId, UnitOfWork work, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(work);
        return await work.Connection.ExecuteScalarAsync<int>(new CommandDefinition("""
            UPDATE federation.platform_users
            SET token_version = token_version + 1, updated_at = now()
            WHERE id = @userId
            RETURNING token_version;
            """, new { userId }, work.Transaction, cancellationToken: ct));
    }

    public async Task<UserCredentials?> FindForLoginAsync(
        string username, CancellationToken ct)
    {
        await using var c = await _dataSource.OpenConnectionAsync(ct);

        var row = await c.QuerySingleOrDefaultAsync<LoginRow>(new CommandDefinition("""
            SELECT id, username, display_name, password_hash, password_salt,
                   password_iterations, password_algorithm, must_change_password,
                   status, failed_login_count, locked_until, token_version
            FROM federation.platform_users
            WHERE lower(username) = lower(@username);
            """, new { username }, cancellationToken: ct));

        return row is null ? null : new UserCredentials(
            row.Id, row.Username, row.DisplayName,
            new PasswordHash(row.PasswordHash, row.PasswordSalt, row.PasswordIterations,
                row.PasswordAlgorithm),
            row.MustChangePassword, row.Status, row.FailedLoginCount, row.LockedUntil,
            row.TokenVersion);
    }

    /// <summary>
    /// Records a failed attempt, locking the account once the threshold is reached.
    /// </summary>
    /// <remarks>
    /// Lockout is time-based rather than permanent. A permanent lock turns a forgotten password
    /// into an administrator ticket, and turns an attacker into a denial-of-service against every
    /// account they can name.
    /// <para>
    /// A no-op while the account is already locked (finding 4-L3): otherwise every attempt an
    /// attacker makes during the lockout window pushes <c>locked_until</c> further out, holding a
    /// victim's account locked indefinitely.
    /// </para>
    /// <para>
    /// <c>failed_login_count</c> is only cleared by <see cref="RecordSuccessfulLoginAsync"/>, so
    /// once an account has locked, the first failed attempt after the window re-satisfies the
    /// threshold and re-locks (and emits a fresh lockout audit row). Noisy, not a security
    /// defect; a count-reset-on-expiry is a possible later refinement.
    /// </para>
    /// </remarks>
    /// <returns>
    /// <see langword="true"/> exactly on the attempt that transitions the account into lockout;
    /// <see langword="false"/> otherwise (below threshold, or already locked, or unknown id).
    /// The <c>WHERE</c> already excludes an already-locked row, so a returned <c>true</c> is
    /// unambiguously the transition.
    /// </returns>
    public async Task<bool> RecordFailedLoginAsync(Guid userId, CancellationToken ct)
    {
        await using var c = await _dataSource.OpenConnectionAsync(ct);
        return await c.ExecuteScalarAsync<bool?>(new CommandDefinition("""
            UPDATE federation.platform_users
            SET failed_login_count = failed_login_count + 1,
                locked_until = CASE WHEN failed_login_count + 1 >= @MaxFailed
                                    THEN now() + @Lockout ELSE locked_until END,
                updated_at = now()
            WHERE id = @userId
              AND (locked_until IS NULL OR locked_until <= now())
            RETURNING (locked_until IS NOT NULL AND locked_until > now());
            """, new { userId, MaxFailed = MaxFailedLogins, Lockout = LockoutDuration },
            cancellationToken: ct)) ?? false;
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

    /// <summary>
    /// Changes a user's password, appending the OUTGOING hash to <c>password_history</c> and
    /// pruning to the newest <see cref="PasswordPolicy.HistoryDepth"/> rows — all in
    /// <paramref name="work"/>.
    /// </summary>
    /// <remarks>
    /// The caller has already run the reuse check
    /// (<see cref="LoadPasswordHistoryAsync"/> + <see cref="PasswordHasher.Verify"/>) and, for a
    /// self-service change, the minimum-age check. A silent cost-upgrade rehash uses
    /// <see cref="RehashPasswordAsync"/> instead — it does not change the password so it writes
    /// no history row.
    /// </remarks>
    public async Task SetPasswordAsync(
        Guid userId, PasswordHash password, bool mustChange, UnitOfWork work, CancellationToken ct)
    {
        var c = work.Connection;
        var tx = work.Transaction;

        // Snapshot the outgoing hash into history. FOR UPDATE so two concurrent changes for the
        // same user serialize on the row BEFORE either snapshots — without it, the second could
        // read the pre-first-change hash and the genuinely-used intermediate password would
        // never reach history.
        await c.ExecuteAsync(new CommandDefinition("""
            INSERT INTO federation.password_history
                (user_id, password_hash, password_salt, password_iterations, password_algorithm)
            SELECT id, password_hash, password_salt, password_iterations, password_algorithm
            FROM federation.platform_users
            WHERE id = @userId
            FOR UPDATE;
            """, new { userId }, tx, cancellationToken: ct));

        await c.ExecuteAsync(new CommandDefinition("""
            DELETE FROM federation.password_history
            WHERE user_id = @userId
              AND id NOT IN (
                  SELECT id FROM federation.password_history
                  WHERE user_id = @userId
                  ORDER BY set_at DESC, id DESC
                  LIMIT @depth);
            """, new { userId, depth = PasswordPolicy.HistoryDepth }, tx, cancellationToken: ct));

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
        }, tx, cancellationToken: ct));
    }

    /// <summary>
    /// Silently re-stores the same password at the current cost. Writes no history row — the
    /// password did not change. The login rehash path only.
    /// </summary>
    public async Task RehashPasswordAsync(
        Guid userId, PasswordHash password, UnitOfWork work, CancellationToken ct)
    {
        await work.Connection.ExecuteAsync(new CommandDefinition("""
            UPDATE federation.platform_users
            SET password_hash = @Hash, password_salt = @Salt,
                password_iterations = @Iterations, password_algorithm = @Algorithm,
                updated_at = now()
            WHERE id = @userId;
            """, new
        {
            userId, password.Hash, password.Salt, password.Iterations, password.Algorithm,
        }, work.Transaction, cancellationToken: ct));
    }

    /// <summary>
    /// The newest <see cref="PasswordPolicy.HistoryDepth"/> historical password hashes for a
    /// user, newest first, each with its <c>set_at</c>. Empty if the user has never changed
    /// their password.
    /// </summary>
    /// <remarks>
    /// Keyed on one principal's own primary key and only ever called for the account being
    /// changed, after that account has been authenticated (self) or passed
    /// <c>UserAuthorityGuard</c> (admin) — the documented invariant-11 carve-out, same as
    /// <see cref="FindForLoginByIdAsync"/>.
    /// </remarks>
    public async Task<IReadOnlyList<PasswordHistoryEntry>> LoadPasswordHistoryAsync(
        Guid userId, CancellationToken ct)
    {
        await using var c = await _dataSource.OpenConnectionAsync(ct);
        var rows = await c.QueryAsync<PasswordHistoryRow>(new CommandDefinition("""
            SELECT password_hash, password_salt, password_iterations, password_algorithm, set_at
            FROM federation.password_history
            WHERE user_id = @userId
            ORDER BY set_at DESC
            LIMIT @depth;
            """, new { userId, depth = PasswordPolicy.HistoryDepth }, cancellationToken: ct));

        return [.. rows.Select(r => new PasswordHistoryEntry(
            new PasswordHash(r.PasswordHash, r.PasswordSalt, r.PasswordIterations, r.PasswordAlgorithm),
            r.SetAt))];
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

    /// <summary>
    /// The user directory, filtered to the accounts <paramref name="caller"/> may administer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A caller unscoped for <c>user.read</c> sees every account. A scoped caller sees only the
    /// accounts that pass the same reachability test as <see cref="CanAdministerAsync"/>
    /// conditions 2 and 3 — the target holds nothing through an organization-unrestricted group,
    /// and every organization unit it reaches is one the caller administers — with the seeded
    /// system account excluded outright. The permission-subset test (condition 1) is deliberately
    /// not applied here: visibility tracks organizational reach, not whether the caller
    /// out-ranks the target on every permission. An account with no groups grants nothing and is
    /// visible to any <c>user.read</c> holder, matching <c>CanAdministerAsync</c>.
    /// </para>
    /// <para>
    /// Organization dimension only. User authority is org-scoped throughout
    /// (<see cref="CanAdministerAsync"/>); adding the geography dimension to user visibility is
    /// tracked separately as the F6 geography-asymmetry item.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<PlatformUser>> ListAsync(CallerContext caller, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);

        await using var c = await _dataSource.OpenConnectionAsync(ct);
        var rows = await c.QueryAsync<PlatformUser>(new CommandDefinition($"""
            SELECT pu.id, pu.username, pu.display_name, pu.email, pu.must_change_password,
                   pu.status, pu.last_login_at, pu.is_system
            FROM federation.platform_users pu
            WHERE {VisibleForUserReadPredicate}
            ORDER BY pu.username;
            """, UserVisibilityParams(caller), cancellationToken: ct));
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
    /// Whether the caller may <b>see</b> this one user — the single-record counterpart of the
    /// filter <see cref="ListAsync"/> applies, so a listed account and its detail route agree.
    /// </summary>
    /// <remarks>
    /// Runs exactly <see cref="VisibleForUserReadPredicate"/> (conditions 2 and 3 of
    /// <see cref="CanAdministerAsync"/> plus the system-account exclusion — organization dimension
    /// only) against the one row. The permission-subset check (condition 1) is deliberately not
    /// here: read visibility tracks organizational reach, not whether the caller out-ranks the
    /// target. A nonexistent id resolves to <see langword="true"/> so the endpoint falls through
    /// to its own 404. API-key callers resolve through <c>authorized_org_units</c> like anyone
    /// else — unlike <see cref="CanAdministerAsync"/>, which fail-closes them because administering
    /// people is a human act.
    /// </remarks>
    public async Task<bool> CanReadAsync(
        Guid targetUserId, CallerContext caller, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);

        var p = UserVisibilityParams(caller);
        p.Add("targetUserId", targetUserId);

        await using var c = await _dataSource.OpenConnectionAsync(ct);
        return await c.ExecuteScalarAsync<bool>(new CommandDefinition($"""
            SELECT COALESCE((
                SELECT {VisibleForUserReadPredicate}
                FROM federation.platform_users pu
                WHERE pu.id = @targetUserId), TRUE);
            """, p, cancellationToken: ct));
    }

    /// <summary>
    /// Whether the caller may administer this user at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Holding <paramref name="permission"/> (<c>user.manage</c>) is necessary but nowhere near
    /// sufficient. Without this check a department-scoped administrator could reset the bootstrap
    /// administrator's password, log in as it, and hold the whole platform — a takeover from the
    /// lowest privilege that can manage users at all. Read guards use <see cref="CanReadAsync"/>
    /// instead (conditions 2+3 only).
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
        Guid targetUserId, Guid? callerUserId, bool callerIsUnscoped, string permission,
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
                                   p_permission => @permission)));
            """, new { targetUserId, callerUserId, callerIsUnscoped, permission }, cancellationToken: ct));
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
        public int TokenVersion { get; init; }
    }

    private sealed class PasswordHistoryRow
    {
        public byte[] PasswordHash { get; init; } = [];
        public byte[] PasswordSalt { get; init; } = [];
        public int PasswordIterations { get; init; }
        public string PasswordAlgorithm { get; init; } = "";
        public DateTimeOffset SetAt { get; init; }
    }
}

/// <summary>One retained prior password, from <see cref="UserRepository.LoadPasswordHistoryAsync"/>.</summary>
public readonly record struct PasswordHistoryEntry(PasswordHash Hash, DateTimeOffset SetAt);
