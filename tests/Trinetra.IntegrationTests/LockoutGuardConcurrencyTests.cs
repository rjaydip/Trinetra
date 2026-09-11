using Shouldly;
using Trinetra.Federation.Storage;
using Trinetra.Federation.Storage.Repositories;

namespace Trinetra.IntegrationTests;

/// <summary>
/// Finding 6-M4: the "would lock everyone out" guard on <c>PUT /users/{id}</c> and
/// <c>DELETE /users/{id}/groups/{groupId}</c> used to count other <c>user.manage</c> holders
/// BEFORE opening a transaction — two concurrent deactivations of the last two administrators
/// each see "1 other holder" (each other) and both proceed, leaving zero. The fix moves the
/// count inside the transaction under a permission-keyed <c>pg_advisory_xact_lock</c>
/// (<see cref="AccessGroupRepository.CountOtherHoldersInTransactionAsync"/>), serializing the
/// two callers so the second sees the first's already-committed change.
/// </summary>
/// <remarks>
/// Exercises the repository layer directly — <c>UserRepository.UpdateAsync</c> /
/// <c>AccessGroupRepository.RevokeMembershipAsync</c> plus
/// <c>AccessGroupRepository.CountOtherHoldersInTransactionAsync</c>, run inside a
/// <see cref="UnitOfWork"/> exactly as both fixed endpoint handlers now do — rather than through
/// the endpoints themselves, which would additionally require a real administering caller
/// satisfying <c>CanAdministerAsync</c>'s own DB-backed checks; those are a different concern
/// from the race this finding is about. Both call sites the PR fixed are covered: deactivation
/// (<c>TryDeactivateAsync</c>) and group-membership revocation (<c>TryRevokeMembershipAsync</c>).
/// </remarks>
public sealed class LockoutGuardConcurrencyTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly PostgresFixture _fixture;

    private static readonly Guid AdminA = Guid.Parse("d7777777-7777-7777-7777-777777777701");
    private static readonly Guid AdminB = Guid.Parse("d7777777-7777-7777-7777-777777777702");
    private static readonly Guid AdminGroup = Guid.Parse("f7777777-7777-7777-7777-777777777701");

    public LockoutGuardConcurrencyTests(PostgresFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        await _fixture.ExecuteAsync($"""
            DELETE FROM federation.user_groups WHERE user_id IN ('{AdminA}', '{AdminB}');
            DELETE FROM federation.access_groups WHERE id = '{AdminGroup}';
            DELETE FROM federation.role_permissions WHERE role_id IN (
                SELECT id FROM federation.roles WHERE code = 'LOCKOUT_GUARD_TEST');
            DELETE FROM federation.roles WHERE code = 'LOCKOUT_GUARD_TEST';
            DELETE FROM federation.platform_users WHERE id IN ('{AdminA}', '{AdminB}');

            INSERT INTO federation.platform_users
                (id, username, display_name, password_hash, password_salt, password_iterations, status)
            VALUES
                ('{AdminA}', 'lockout-admin-a', 'Admin A', '\x00', '\x00', 600000, 'ACTIVE'),
                ('{AdminB}', 'lockout-admin-b', 'Admin B', '\x00', '\x00', 600000, 'ACTIVE');

            INSERT INTO federation.roles (code, name, description, is_system, status)
            VALUES ('LOCKOUT_GUARD_TEST', 'Lockout Guard Test', 'Integration-test-only role',
                    FALSE, 'ACTIVE');
            INSERT INTO federation.role_permissions (role_id, permission_code)
            SELECT r.id, 'user.manage' FROM federation.roles r WHERE r.code = 'LOCKOUT_GUARD_TEST';

            -- No group_scopes row: an unscoped grant, so both admins hold user.manage
            -- unconditionally, matching the real "last two platform admins" scenario.
            INSERT INTO federation.access_groups (id, code, name, role_id, status)
            SELECT '{AdminGroup}', 'LOCKOUT-GUARD-TEST-GROUP', 'Lockout Guard Test Group', r.id,
                   'ACTIVE'
            FROM federation.roles r WHERE r.code = 'LOCKOUT_GUARD_TEST';
            INSERT INTO federation.user_groups (user_id, group_id)
            VALUES ('{AdminA}', '{AdminGroup}'), ('{AdminB}', '{AdminGroup}');
            """);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<string> StatusOfUser(Guid id) =>
        (await _fixture.ScalarAsync<string>(
            $"SELECT status FROM federation.platform_users WHERE id = '{id}'"))!;

    /// <summary>Mirrors <c>UserEndpoints.UpdateAsync</c>'s own sequence: begin, check, mutate, commit.</summary>
    private async Task<bool> TryDeactivateAsync(Guid targetId)
    {
        await using var work = await UnitOfWork.BeginAsync(_fixture.DataSource, CancellationToken.None);

        if (await AccessGroupRepository.CountOtherHoldersInTransactionAsync(
                "user.manage", targetId, work, CancellationToken.None) == 0)
        {
            return false; // "would lock everyone out" — the transaction rolls back on dispose.
        }

        await new UserRepository(_fixture.DataSource).UpdateAsync(
            targetId, "Unchanged", null, "INACTIVE", work, CancellationToken.None);
        await work.CommitAsync(CancellationToken.None);
        return true;
    }

    /// <summary>Mirrors <c>UserEndpoints.RemoveFromGroupAsync</c>'s own sequence — the PR's second
    /// call site, not exercised by <see cref="TryDeactivateAsync"/>.</summary>
    private async Task<bool> TryRevokeMembershipAsync(Guid userId)
    {
        await using var work = await UnitOfWork.BeginAsync(_fixture.DataSource, CancellationToken.None);

        if (await AccessGroupRepository.CountOtherHoldersInTransactionAsync(
                "user.manage", userId, work, CancellationToken.None) == 0)
        {
            return false;
        }

        await Groups.RevokeMembershipAsync(userId, AdminGroup, work, CancellationToken.None);
        await work.CommitAsync(CancellationToken.None);
        return true;
    }

    private AccessGroupRepository Groups => new(_fixture.DataSource);

    [Fact]
    public async Task ConcurrentDeactivation_OfTheLastTwoAdmins_ExactlyOneSucceeds()
    {
        var results = await Task.WhenAll(
            TryDeactivateAsync(AdminA), TryDeactivateAsync(AdminB));

        results.Count(r => r).ShouldBe(1, "exactly one deactivation should win the race");
        results.Count(r => !r).ShouldBe(1, "the other must be refused as a lockout risk");

        var statuses = new[] { await StatusOfUser(AdminA), await StatusOfUser(AdminB) };
        statuses.Count(s => s == "ACTIVE").ShouldBe(1,
            "exactly one administrator must remain — this is the race the bug used to lose");
    }

    [Fact]
    public async Task SequentialDeactivation_SecondCorrectlyRefused()
    {
        (await TryDeactivateAsync(AdminA)).ShouldBeTrue();
        (await TryDeactivateAsync(AdminB)).ShouldBeFalse(
            "AdminA is already inactive — AdminB is now the only administrator");

        (await StatusOfUser(AdminB)).ShouldBe("ACTIVE");
    }

    /// <summary>The PR's second call site — the same race via group-membership revocation
    /// instead of deactivation, reaching <c>user.manage</c> loss by a different route.</summary>
    [Fact]
    public async Task ConcurrentMembershipRevocation_OfTheLastTwoAdmins_ExactlyOneSucceeds()
    {
        var results = await Task.WhenAll(
            TryRevokeMembershipAsync(AdminA), TryRevokeMembershipAsync(AdminB));

        results.Count(r => r).ShouldBe(1, "exactly one revocation should win the race");
        results.Count(r => !r).ShouldBe(1, "the other must be refused as a lockout risk");

        var remaining = await _fixture.ScalarAsync<long>($"""
            SELECT count(DISTINCT ea.user_id) FROM federation.user_effective_access ea
            WHERE ea.permission_code = 'user.manage'
              AND ea.user_id IN ('{AdminA}', '{AdminB}');
            """);
        remaining.ShouldBe(1, "exactly one administrator must still hold user.manage");
    }
}
