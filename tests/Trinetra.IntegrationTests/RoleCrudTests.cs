using Shouldly;
using Trinetra.Federation.Storage;
using Trinetra.Federation.Storage.Repositories;

namespace Trinetra.IntegrationTests;

/// <summary>
/// Verifies <see cref="RoleRepository"/> — the composable-role write path added with
/// <c>role.manage</c>. Editing a preset requires <c>role.manage</c> held unscoped; the
/// escalation guard blocks composing a role out of permissions the caller lacks and runs
/// against the <c>FOR UPDATE</c> row; <c>SUPER_ADMIN</c> is locked; a preset cannot be deleted;
/// a custom role in use by a group cannot be deleted; <c>customized_at</c> is set only on a real
/// content change.
/// </summary>
/// <remarks>
/// The "preset" cases run against a private <c>ROLE-TEST-PRESET</c> row (created with
/// <c>is_system = TRUE</c> in setup), never a real seeded preset — editing e.g. <c>VIEWER</c>
/// mid-suite would corrupt every parallel test that resolves a user through it.
/// </remarks>
public sealed class RoleCrudTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly PostgresFixture _fixture;

    private static readonly Guid TestPreset = Guid.Parse("d5555555-5555-5555-5555-555555555555");

    public RoleCrudTests(PostgresFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        await _fixture.ResetTargetsAsync();
        await _fixture.ExecuteAsync($"""
            DELETE FROM federation.access_groups WHERE code LIKE 'ROLE-TEST-%';
            DELETE FROM federation.roles WHERE code LIKE 'ROLE-TEST-%';

            INSERT INTO federation.roles (id, code, name, description, is_system, status)
            VALUES ('{TestPreset}', 'ROLE-TEST-PRESET', 'Test Preset',
                    'a stand-in preset', TRUE, 'ACTIVE');
            INSERT INTO federation.role_permissions (role_id, permission_code)
            VALUES ('{TestPreset}', 'vms.read'), ('{TestPreset}', 'event.read');
            """);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private RoleRepository Repo => new(_fixture.DataSource);

    private async Task<UnitOfWork> BeginAsync() =>
        await UnitOfWork.BeginAsync(_fixture.DataSource, CancellationToken.None);

    // A caller unrestricted for everything (matches an unscoped SUPER_ADMIN member).
    private static CallerContext Unscoped() => CallerContext.System("role-crud-test");

    // A caller who holds role.manage + a fixed permission set, but scoped (no unscoped flags).
    private static CallerContext Scoped(params string[] permissions) => new()
    {
        UserId = Guid.Parse("d6666666-6666-6666-6666-666666666666"),
        Actor = "scoped-tester",
        Permissions = new HashSet<string>([.. permissions, "role.manage"], StringComparer.Ordinal),
    };

    [Fact]
    public async Task Create_ThenGet_RoundTripsThePermissionSet()
    {
        Guid id;
        await using (var work = await BeginAsync())
        {
            var r = await Repo.CreateAsync(
                "ROLE-TEST-CUSTOM", "Custom", "hand-built", null,
                ["vms.read", "event.read"], Unscoped(), work, CancellationToken.None);
            r.Error.ShouldBe(RoleWriteError.None);
            id = r.Id;
            await work.CommitAsync(CancellationToken.None);
        }

        var role = await Repo.GetAsync(id, CancellationToken.None);
        role.ShouldNotBeNull();
        role!.IsSystem.ShouldBeFalse();
        role.Permissions.ShouldBe(["event.read", "vms.read"], ignoreOrder: true);
    }

    [Fact]
    public async Task Create_DuplicateCode_IsRejected()
    {
        await using var work = await BeginAsync();
        var r = await Repo.CreateAsync(
            "ROLE-TEST-PRESET", "Dup", null, null, ["vms.read"], Unscoped(), work,
            CancellationToken.None);
        r.Error.ShouldBe(RoleWriteError.CodeInUse);
    }

    [Fact]
    public async Task Create_WithUnknownPermission_IsRejected()
    {
        await using var work = await BeginAsync();
        var r = await Repo.CreateAsync(
            "ROLE-TEST-BAD", "Bad", null, null,
            ["vms.read", "not.a.real.permission"], Unscoped(), work, CancellationToken.None);
        r.Error.ShouldBe(RoleWriteError.UnknownPermission);
    }

    [Fact]
    public async Task Create_ByScopedCaller_ExceedingTheirGrant_IsEscalation()
    {
        await using var work = await BeginAsync();
        var r = await Repo.CreateAsync(
            "ROLE-TEST-ESC", "Esc", null, null,
            ["vms.read", "credential.write"], Scoped("vms.read"), work, CancellationToken.None);

        r.Error.ShouldBe(RoleWriteError.Escalation);
        r.Exceeding.ShouldBe(["credential.write"]);
    }

    [Fact]
    public async Task Update_ReplacesTheEntirePermissionSet()
    {
        Guid id;
        await using (var work = await BeginAsync())
        {
            id = (await Repo.CreateAsync(
                "ROLE-TEST-REPL", "Repl", null, null,
                ["vms.read", "vms.create", "vms.update"], Unscoped(), work,
                CancellationToken.None)).Id;
            await work.CommitAsync(CancellationToken.None);
        }

        await using (var work = await BeginAsync())
        {
            var result = await Repo.UpdateAsync(
                id, "Repl v2", null, null, ["event.read"], Unscoped(), work,
                CancellationToken.None);
            result.Error.ShouldBe(RoleWriteError.None);

            // result.Detail reflects the POST-update state, read inside this transaction — a
            // fresh-connection read here would still see the pre-update row.
            result.Detail.ShouldNotBeNull();
            result.Detail!.Name.ShouldBe("Repl v2");
            result.Detail.Permissions.ShouldBe(["event.read"]);

            await work.CommitAsync(CancellationToken.None);
        }

        (await Repo.GetAsync(id, CancellationToken.None))!.Permissions.ShouldBe(["event.read"]);
    }

    [Fact]
    public async Task Update_ByScopedCaller_TouchingAPermissionOnlyInThePriorSet_IsEscalation()
    {
        // A concurrent-safe check: the role already carries credential.write (put there by
        // someone else); a scoped caller who lacks it must not be able to reshape the role,
        // even to strip it.
        Guid id;
        await using (var work = await BeginAsync())
        {
            id = (await Repo.CreateAsync(
                "ROLE-TEST-PRIOR", "Prior", null, null,
                ["vms.read", "credential.write"], Unscoped(), work, CancellationToken.None)).Id;
            await work.CommitAsync(CancellationToken.None);
        }

        await using (var work = await BeginAsync())
        {
            var r = await Repo.UpdateAsync(
                id, "Prior", null, null, ["vms.read"], Scoped("vms.read"), work,
                CancellationToken.None);
            r.Error.ShouldBe(RoleWriteError.Escalation);
            r.Exceeding.ShouldBe(["credential.write"]);
        }
    }

    [Fact]
    public async Task Update_APreset_ByScopedCaller_RequiresUnscoped()
    {
        await using var work = await BeginAsync();
        var r = await Repo.UpdateAsync(
            TestPreset, "Renamed", null, null, ["vms.read"],
            Scoped("vms.read", "event.read"), work, CancellationToken.None);

        r.Error.ShouldBe(RoleWriteError.PresetRequiresUnscoped);
    }

    [Fact]
    public async Task Update_APreset_ByUnscopedCaller_EditsInPlaceAndMarksCustomized()
    {
        await using (var work = await BeginAsync())
        {
            var r = await Repo.UpdateAsync(
                TestPreset, "Preset (tuned)", "now also writes observations", null,
                ["vms.read", "event.read", "observation.write"], Unscoped(), work,
                CancellationToken.None);
            r.Error.ShouldBe(RoleWriteError.None);
            await work.CommitAsync(CancellationToken.None);
        }

        var after = await Repo.GetAsync(TestPreset, CancellationToken.None);
        after!.Name.ShouldBe("Preset (tuned)");
        after.IsSystem.ShouldBeTrue();
        after.Customized.ShouldBeTrue();
        after.Permissions.ShouldContain("observation.write");
    }

    [Fact]
    public async Task Update_APreset_NoOp_DoesNotMarkCustomized()
    {
        await using (var work = await BeginAsync())
        {
            var r = await Repo.UpdateAsync(
                TestPreset, "Test Preset", "a stand-in preset", null,
                ["event.read", "vms.read"], Unscoped(), work, CancellationToken.None);
            r.Error.ShouldBe(RoleWriteError.None);
            await work.CommitAsync(CancellationToken.None);
        }

        (await Repo.GetAsync(TestPreset, CancellationToken.None))!.Customized.ShouldBeFalse();
    }

    [Fact]
    public async Task Update_SuperAdmin_IsLocked()
    {
        var superAdmin = (await Repo.ListAsync(CancellationToken.None))
            .Single(r => r.Code == "SUPER_ADMIN");

        await using var work = await BeginAsync();
        var r = await Repo.UpdateAsync(
            superAdmin.Id, "nope", null, null, ["vms.read"], Unscoped(), work,
            CancellationToken.None);

        r.Error.ShouldBe(RoleWriteError.Locked);
    }

    [Fact]
    public async Task Delete_APreset_IsRefused()
    {
        await using var work = await BeginAsync();
        (await Repo.DeleteAsync(TestPreset, Unscoped(), work, CancellationToken.None))
            .Error.ShouldBe(RoleWriteError.PresetShapeFixed);
    }

    [Fact]
    public async Task Delete_SoftDeletesToInactive_KeepsPermissionRows_AndIsIdempotent()
    {
        Guid id;
        await using (var work = await BeginAsync())
        {
            id = (await Repo.CreateAsync(
                "ROLE-TEST-SOFT", "Soft", null, "ACTIVE", ["vms.read"], Unscoped(), work,
                CancellationToken.None)).Id;
            await work.CommitAsync(CancellationToken.None);
        }

        await using (var work = await BeginAsync())
        {
            (await Repo.DeleteAsync(id, Unscoped(), work, CancellationToken.None))
                .Error.ShouldBe(RoleWriteError.None);
            await work.CommitAsync(CancellationToken.None);
        }

        var after = await Repo.GetAsync(id, CancellationToken.None);
        after.ShouldNotBeNull();
        after!.Status.ShouldBe("INACTIVE");
        after.Permissions.ShouldBe(["vms.read"]);

        // Idempotent on an already-INACTIVE role.
        await using (var work = await BeginAsync())
        {
            (await Repo.DeleteAsync(id, Unscoped(), work, CancellationToken.None))
                .Error.ShouldBe(RoleWriteError.None);
            await work.CommitAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Delete_ACustomRoleInUse_SoftDeletes_GroupKeepsRoleButGrantsNothing()
    {
        Guid usedId;
        await using (var work = await BeginAsync())
        {
            usedId = (await Repo.CreateAsync(
                "ROLE-TEST-USED", "Used", null, "ACTIVE", ["vms.read"], Unscoped(), work,
                CancellationToken.None)).Id;
            await work.CommitAsync(CancellationToken.None);
        }

        await _fixture.ExecuteAsync($"""
            INSERT INTO federation.access_groups (code, name, role_id, status)
            VALUES ('ROLE-TEST-GRP', 'Uses the used role', '{usedId}', 'ACTIVE');
            """);

        await using (var work = await BeginAsync())
        {
            (await Repo.DeleteAsync(usedId, Unscoped(), work, CancellationToken.None))
                .Error.ShouldBe(RoleWriteError.None);
            await work.CommitAsync(CancellationToken.None);
        }

        (await Repo.GetAsync(usedId, CancellationToken.None))!.Status.ShouldBe("INACTIVE");

        var stillReferenced = await _fixture.ScalarAsync<string>(
            $"SELECT status FROM federation.access_groups WHERE role_id = '{usedId}';");
        stillReferenced.ShouldBe("ACTIVE");

        var granted = await _fixture.ScalarAsync<long>($"""
            SELECT count(*) FROM federation.access_groups ag
            JOIN federation.roles r ON r.id = ag.role_id
            WHERE ag.role_id = '{usedId}' AND r.status = 'ACTIVE';
            """);
        granted.ShouldBe(0);
    }

    [Fact]
    public async Task Delete_ByScopedCaller_ExceedingTheirGrant_IsEscalation()
    {
        Guid id;
        await using (var work = await BeginAsync())
        {
            id = (await Repo.CreateAsync(
                "ROLE-TEST-DESC", "Desc", null, "ACTIVE", ["vms.read", "credential.write"],
                Unscoped(), work, CancellationToken.None)).Id;
            await work.CommitAsync(CancellationToken.None);
        }

        await using var work2 = await BeginAsync();
        var r = await Repo.DeleteAsync(id, Scoped("vms.read"), work2, CancellationToken.None);
        r.Error.ShouldBe(RoleWriteError.Escalation);
        r.Exceeding.ShouldBe(["credential.write"]);
    }

    [Fact]
    public async Task Create_DefaultsToDraft_AndDraftGrantsNothing()
    {
        Guid id;
        await using (var work = await BeginAsync())
        {
            id = (await Repo.CreateAsync(
                "ROLE-TEST-DRAFT", "Draft", null, null, ["vms.read"], Unscoped(), work,
                CancellationToken.None)).Id;
            await work.CommitAsync(CancellationToken.None);
        }

        (await Repo.GetAsync(id, CancellationToken.None))!.Status.ShouldBe("DRAFT");

        // Hidden from the default list, visible with includeInactive.
        (await Repo.ListAsync(false, CancellationToken.None)).ShouldNotContain(r => r.Id == id);
        (await Repo.ListAsync(true, CancellationToken.None)).ShouldContain(r => r.Id == id);
    }

    [Fact]
    public async Task Update_CannotReturnALiveRoleToDraft()
    {
        Guid id;
        await using (var work = await BeginAsync())
        {
            id = (await Repo.CreateAsync(
                "ROLE-TEST-NOBACK", "NoBack", null, "ACTIVE", ["vms.read"], Unscoped(), work,
                CancellationToken.None)).Id;
            await work.CommitAsync(CancellationToken.None);
        }

        await using var work2 = await BeginAsync();
        (await Repo.UpdateAsync(
            id, "NoBack", null, "DRAFT", ["vms.read"], Unscoped(), work2, CancellationToken.None))
            .Error.ShouldBe(RoleWriteError.InvalidStatus);
    }
}
