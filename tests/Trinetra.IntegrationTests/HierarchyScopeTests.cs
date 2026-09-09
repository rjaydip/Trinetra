using Shouldly;
using Trinetra.Federation.Storage;
using Trinetra.Federation.Storage.Repositories;

namespace Trinetra.IntegrationTests;

/// <summary>
/// Verifies that <see cref="OrganizationRepository"/> and <see cref="GeographyRepository"/> run
/// their reach checks inside the caller's transaction (5-H1 — a scoped <c>*.manage</c> caller
/// previously got a 500 on every create and deactivate), resolve organization reach through
/// <c>authorized_org_units</c> set-membership rather than <c>has_permission</c> (5-H1 secondary —
/// a caller scoped on both dimensions was falsely denied), serialize concurrent deactivations of
/// one hierarchy (5-H2), and that <see cref="OrganizationRepository.ListAsync"/> needs only
/// <c>organization.read</c> (5-M1 — the two <c>GET /organizations</c> routes advertised
/// <c>geography.read</c>).
/// </summary>
/// <remarks>
/// The primary caller (<c>ScopedCaller</c>) is scoped to <see cref="PostgresFixture.PoliceUnit"/>
/// organizationally and <see cref="PostgresFixture.DistrictId"/> geographically, via a bespoke
/// role carrying exactly the four hierarchy permissions — both dimensions declared, so every
/// guard branch actually runs and every test here is also a regression test for the
/// <c>has_permission</c> false-denial. There is no <c>WebApplicationFactory</c> in this suite
/// (an accepted gap); the permission-mismatch finding 5-M1 is covered at the repository level
/// via <c>ReadOnlyCaller</c>, whose role has <c>organization.read</c> but not
/// <c>geography.read</c>.
/// </remarks>
public sealed class HierarchyScopeTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly PostgresFixture _fixture;

    private static readonly Guid ScopeUser  = Guid.Parse("d5555555-5555-5555-5555-555555555555");
    private static readonly Guid ScopeGroup = Guid.Parse("f5555555-5555-5555-5555-555555555555");
    private static readonly Guid OrgScope   = Guid.Parse("e5000000-0000-0000-0000-000000000001");
    private static readonly Guid GeoScope   = Guid.Parse("e5000000-0000-0000-0000-000000000002");

    private static readonly Guid ReadUser   = Guid.Parse("d5555555-0000-0000-0000-0000000000b1");
    private static readonly Guid ReadGroup  = Guid.Parse("f5555555-0000-0000-0000-0000000000b1");
    private static readonly Guid ReadScope  = Guid.Parse("e5000000-0000-0000-0000-0000000000b1");

    // Out of reach: root nodes that sit outside PoliceUnit / DistrictId.
    private static readonly Guid OtherOrgUnit = Guid.Parse("a5000000-0000-0000-0000-0000000000c1");
    private static readonly Guid OtherArea    = Guid.Parse("b5000000-0000-0000-0000-0000000000c1");

    // In reach: leaves under the scoped subtree, used as deactivation targets.
    private static readonly Guid UnitLeaf = Guid.Parse("a5000000-0000-0000-0000-0000000000d1");
    private static readonly Guid AreaLeaf = Guid.Parse("b5000000-0000-0000-0000-0000000000d1");

    // In reach: an already-INACTIVE unit that still has an ACTIVE child — re-deactivating it
    // must be a clean no-op, never a spurious children conflict.
    private static readonly Guid UnitInactive      = Guid.Parse("a5000000-0000-0000-0000-0000000000d8");
    private static readonly Guid UnitInactiveChild = Guid.Parse("a5000000-0000-0000-0000-0000000000d9");
    private static readonly Guid AreaInactive      = Guid.Parse("b5000000-0000-0000-0000-0000000000d8");

    // In reach: a three-level chain under AhmedabadCp for the 5-H2 concurrency test.
    private static readonly Guid CcRoot = Guid.Parse("a5000000-0000-0000-0000-0000000000e1");
    private static readonly Guid CcMid  = Guid.Parse("a5000000-0000-0000-0000-0000000000e2");
    private static readonly Guid CcLeaf = Guid.Parse("a5000000-0000-0000-0000-0000000000e3");

    public HierarchyScopeTests(PostgresFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        await _fixture.ResetTargetsAsync();

        await _fixture.ExecuteAsync($"""
            DELETE FROM federation.user_groups
                WHERE user_id IN ('{ScopeUser}', '{ReadUser}');
            DELETE FROM federation.group_scopes
                WHERE group_id IN ('{ScopeGroup}', '{ReadGroup}');
            DELETE FROM federation.access_groups
                WHERE id IN ('{ScopeGroup}', '{ReadGroup}');
            DELETE FROM federation.scopes
                WHERE id IN ('{OrgScope}', '{GeoScope}', '{ReadScope}');
            DELETE FROM federation.platform_users
                WHERE id IN ('{ScopeUser}', '{ReadUser}');
            DELETE FROM federation.role_permissions WHERE role_id IN (
                SELECT id FROM federation.roles
                WHERE code IN ('HIER_SCOPE_TEST', 'HIER_READ_TEST'));
            DELETE FROM federation.roles
                WHERE code IN ('HIER_SCOPE_TEST', 'HIER_READ_TEST');

            DELETE FROM federation.organization_units
                WHERE id IN ('{CcLeaf}', '{CcMid}', '{CcRoot}', '{UnitLeaf}',
                             '{UnitInactiveChild}', '{UnitInactive}', '{OtherOrgUnit}');
            DELETE FROM federation.geographic_areas
                WHERE id IN ('{AreaLeaf}', '{AreaInactive}', '{OtherArea}');

            -- Bespoke roles: one with the full hierarchy set, one read-only WITHOUT geography.read.
            INSERT INTO federation.roles (code, name, description, is_system, status)
            VALUES ('HIER_SCOPE_TEST', 'Hierarchy Scope Test', 'Integration-test-only role', FALSE, 'ACTIVE'),
                   ('HIER_READ_TEST', 'Hierarchy Read Test', 'Integration-test-only role', FALSE, 'ACTIVE');
            INSERT INTO federation.role_permissions (role_id, permission_code)
            SELECT r.id, p.code FROM federation.roles r, unnest(ARRAY[
                'organization.read', 'organization.manage', 'geography.read', 'geography.manage'
            ]) AS p(code)
            WHERE r.code = 'HIER_SCOPE_TEST';
            INSERT INTO federation.role_permissions (role_id, permission_code)
            SELECT r.id, 'organization.read' FROM federation.roles r
            WHERE r.code = 'HIER_READ_TEST';

            INSERT INTO federation.platform_users
                (id, username, display_name, password_hash, password_salt, password_iterations)
            VALUES
                ('{ScopeUser}', 'hierscope', 'Hierarchy Scope Tester', '\x00', '\x00', 600000),
                ('{ReadUser}', 'hierread', 'Hierarchy Read Tester', '\x00', '\x00', 600000);

            -- Scoped on BOTH dimensions, so IsUnscopedFor / IsUnscopedForGeography are both false
            -- and every guard branch under test runs.
            INSERT INTO federation.scopes (id, scope_type, organization_unit_id)
            VALUES ('{OrgScope}', 'ORGANIZATION', '{PostgresFixture.PoliceUnit}'),
                   ('{ReadScope}', 'ORGANIZATION', '{PostgresFixture.PoliceUnit}');
            INSERT INTO federation.scopes (id, scope_type, geographic_area_id)
            VALUES ('{GeoScope}', 'GEOGRAPHY', '{PostgresFixture.DistrictId}');

            INSERT INTO federation.access_groups (id, code, name, role_id, status)
            SELECT '{ScopeGroup}', 'HIER-SCOPE-TEST-GROUP', 'Hierarchy Scope Test Group', r.id, 'ACTIVE'
            FROM federation.roles r WHERE r.code = 'HIER_SCOPE_TEST';
            INSERT INTO federation.access_groups (id, code, name, role_id, status)
            SELECT '{ReadGroup}', 'HIER-READ-TEST-GROUP', 'Hierarchy Read Test Group', r.id, 'ACTIVE'
            FROM federation.roles r WHERE r.code = 'HIER_READ_TEST';
            INSERT INTO federation.group_scopes VALUES
                ('{ScopeGroup}', '{OrgScope}'), ('{ScopeGroup}', '{GeoScope}'),
                ('{ReadGroup}', '{ReadScope}');
            INSERT INTO federation.user_groups (user_id, group_id)
            VALUES ('{ScopeUser}', '{ScopeGroup}'), ('{ReadUser}', '{ReadGroup}');

            -- Out-of-reach roots (same organization, different branch / no scope points at them).
            INSERT INTO federation.organization_units
                (id, organization_id, parent_unit_id, code, name, unit_type, status)
            VALUES ('{OtherOrgUnit}', '{PostgresFixture.OrgId}', NULL, 'OTH-U', 'Other Root Unit',
                    'DEPARTMENT', 'ACTIVE');
            INSERT INTO federation.geographic_areas (id, parent_area_id, code, name, area_type)
            VALUES ('{OtherArea}', NULL, 'OTH-A', 'Other Root Area', 'DISTRICT');

            -- In-reach leaves for the deactivate tests.
            INSERT INTO federation.organization_units
                (id, organization_id, parent_unit_id, code, name, unit_type, status)
            VALUES ('{UnitLeaf}', '{PostgresFixture.OrgId}', '{PostgresFixture.AhmedabadCp}',
                    'U-LEAF', 'Unit Leaf', 'STATION', 'ACTIVE');
            INSERT INTO federation.geographic_areas (id, parent_area_id, code, name, area_type)
            VALUES ('{AreaLeaf}', '{PostgresFixture.DistrictId}', 'A-LEAF', 'Area Leaf', 'VILLAGE'),
                   ('{AreaInactive}', '{PostgresFixture.DistrictId}', 'A-INACT', 'Inactive Area',
                    'VILLAGE');
            UPDATE federation.geographic_areas SET status = 'INACTIVE' WHERE id = '{AreaInactive}';

            INSERT INTO federation.organization_units
                (id, organization_id, parent_unit_id, code, name, unit_type, status)
            VALUES
                ('{UnitInactive}', '{PostgresFixture.OrgId}', '{PostgresFixture.AhmedabadCp}',
                 'U-INACT', 'Already Inactive Unit', 'STATION', 'INACTIVE'),
                ('{UnitInactiveChild}', '{PostgresFixture.OrgId}', '{UnitInactive}',
                 'U-INACT-C', 'Active Child Of Inactive', 'STATION', 'ACTIVE');

            -- In-reach three-level chain for the concurrency test.
            INSERT INTO federation.organization_units
                (id, organization_id, parent_unit_id, code, name, unit_type, status)
            VALUES
                ('{CcRoot}', '{PostgresFixture.OrgId}', '{PostgresFixture.AhmedabadCp}',
                 'CC-ROOT', 'Concurrency Root', 'ZONE', 'ACTIVE'),
                ('{CcMid}', '{PostgresFixture.OrgId}', '{CcRoot}',
                 'CC-MID', 'Concurrency Mid', 'DIVISION', 'ACTIVE'),
                ('{CcLeaf}', '{PostgresFixture.OrgId}', '{CcMid}',
                 'CC-LEAF', 'Concurrency Leaf', 'STATION', 'ACTIVE');
            """);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>Scoped on both dimensions; reach resolved entirely from the DB seed.</summary>
    private static CallerContext ScopedCaller() => new()
    {
        UserId = ScopeUser,
        Actor = "hierscope",
        Permissions = new HashSet<string>(StringComparer.Ordinal)
        {
            "organization.read", "organization.manage", "geography.read", "geography.manage",
        },
    };

    /// <summary>Has <c>organization.read</c> only — no <c>geography.read</c>.</summary>
    private static CallerContext ReadOnlyCaller() => new()
    {
        UserId = ReadUser,
        Actor = "hierread",
        Permissions = new HashSet<string>(StringComparer.Ordinal) { "organization.read" },
    };

    private static CallerContext UnscopedCaller() => new()
    {
        Actor = "hier-system",
        Permissions = new HashSet<string>(StringComparer.Ordinal)
        {
            "organization.read", "organization.manage", "geography.read", "geography.manage",
        },
        IsSystem = true,
    };

    private OrganizationRepository Orgs => new(_fixture.DataSource);
    private GeographyRepository Geo => new(_fixture.DataSource);

    // ---- 5-H1: reach check runs inside the transaction ---------------------

    [Fact]
    public async Task Org_UpsertUnit_UnderReachableParent_DoesNotThrow()
    {
        await using var work = await UnitOfWork.BeginAsync(_fixture.DataSource, CancellationToken.None);

        // Pre-fix this threw InvalidOperationException (→ 500): the reach check ran on the
        // transaction's connection without enrolling in the transaction. It is also the
        // has_permission → authorized_org_units regression: the caller is geo-scoped, so the old
        // has_permission call denied unconditionally.
        var id = await Orgs.UpsertUnitAsync(new OrganizationUnit
        {
            OrganizationId = PostgresFixture.OrgId,
            ParentUnitId = PostgresFixture.AhmedabadCp,
            Code = "U-NEW-IN",
            Name = "New In-Reach Unit",
            UnitType = "STATION",
        }, ScopedCaller(), work, CancellationToken.None);

        id.ShouldNotBe(Guid.Empty);
        await work.CommitAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Org_UpsertUnit_UnderUnreachableParent_ThrowsForbidden()
    {
        await using var work = await UnitOfWork.BeginAsync(_fixture.DataSource, CancellationToken.None);

        await Should.ThrowAsync<ForbiddenException>(() => Orgs.UpsertUnitAsync(new OrganizationUnit
        {
            OrganizationId = PostgresFixture.OrgId,
            ParentUnitId = OtherOrgUnit,
            Code = "U-NEW-OUT",
            Name = "New Out-Of-Reach Unit",
            UnitType = "STATION",
        }, ScopedCaller(), work, CancellationToken.None));
    }

    [Fact]
    public async Task Geo_UpsertArea_UnderReachableParent_DoesNotThrow()
    {
        await using var work = await UnitOfWork.BeginAsync(_fixture.DataSource, CancellationToken.None);

        var id = await Geo.UpsertAreaAsync(new GeographicArea
        {
            ParentAreaId = PostgresFixture.DistrictId,
            Code = "A-NEW-IN",
            Name = "New In-Reach Area",
            AreaType = "VILLAGE",
        }, ScopedCaller(), work, CancellationToken.None);

        id.ShouldNotBe(Guid.Empty);
        await work.CommitAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Geo_UpsertArea_UnderUnreachableParent_ThrowsForbidden()
    {
        await using var work = await UnitOfWork.BeginAsync(_fixture.DataSource, CancellationToken.None);

        await Should.ThrowAsync<ForbiddenException>(() => Geo.UpsertAreaAsync(new GeographicArea
        {
            ParentAreaId = OtherArea,
            Code = "A-NEW-OUT",
            Name = "New Out-Of-Reach Area",
            AreaType = "VILLAGE",
        }, ScopedCaller(), work, CancellationToken.None));
    }

    [Fact]
    public async Task Geo_UpsertChildArea_InReachableArea_DoesNotThrow()
    {
        // UpsertAreaAsync → RequireAreaAsync is the third path with the un-enrolled-command bug.
        await using var work = await UnitOfWork.BeginAsync(_fixture.DataSource, CancellationToken.None);

        var id = await Geo.UpsertAreaAsync(new GeographicArea
        {
            ParentAreaId = PostgresFixture.VillageId,
            Code = "S-NEW-IN",
            Name = "New In-Reach Sector",
            AreaType = "SECTOR",
        }, ScopedCaller(), work, CancellationToken.None);

        id.ShouldNotBe(Guid.Empty);
        await work.CommitAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Geo_UpsertChildArea_InUnreachableArea_ThrowsForbidden()
    {
        await using var work = await UnitOfWork.BeginAsync(_fixture.DataSource, CancellationToken.None);

        await Should.ThrowAsync<ForbiddenException>(() => Geo.UpsertAreaAsync(new GeographicArea
        {
            ParentAreaId = OtherArea,
            Code = "S-NEW-OUT",
            Name = "New Out-Of-Reach Sector",
            AreaType = "SECTOR",
        }, ScopedCaller(), work, CancellationToken.None));
    }

    [Fact]
    public async Task Geo_UpsertArea_CoarserThanParent_IsRejected()
    {
        // DistrictId is a DISTRICT (level_order 30); a DISTRICT cannot sit inside it.
        await using var work = await UnitOfWork.BeginAsync(_fixture.DataSource, CancellationToken.None);

        var ex = await Should.ThrowAsync<Exception>(() => Geo.UpsertAreaAsync(new GeographicArea
        {
            ParentAreaId = PostgresFixture.VillageId,   // VILLAGE, level_order 60
            Code = "A-BACKWARDS",
            Name = "District under a village",
            AreaType = "DISTRICT",
        }, UnscopedCaller(), work, CancellationToken.None));

        ex.Message.ShouldContain("finer level");
    }

    [Fact]
    public async Task Geo_UpsertArea_SameLevelAsParent_IsRejected()
    {
        await using var work = await UnitOfWork.BeginAsync(_fixture.DataSource, CancellationToken.None);

        var ex = await Should.ThrowAsync<Exception>(() => Geo.UpsertAreaAsync(new GeographicArea
        {
            ParentAreaId = PostgresFixture.VillageId,   // VILLAGE
            Code = "A-SAMELEVEL",
            Name = "Village under a village",
            AreaType = "VILLAGE",
        }, UnscopedCaller(), work, CancellationToken.None));

        ex.Message.ShouldContain("finer level");
    }

    [Fact]
    public async Task Org_DeactivateUnit_InScope_NoChildren_Deactivates()
    {
        await using var work = await UnitOfWork.BeginAsync(_fixture.DataSource, CancellationToken.None);

        var result = await Orgs.DeactivateUnitAsync(
            UnitLeaf, ChildStrategy.Refuse, null, ScopedCaller(), work, CancellationToken.None);
        result.Outcome.ShouldBe(DeactivationOutcome.Deactivated);
        await work.CommitAsync(CancellationToken.None);

        var status = await _fixture.ScalarAsync<string>(
            $"SELECT status FROM federation.organization_units WHERE id = '{UnitLeaf}';");
        status.ShouldBe("INACTIVE");
    }

    [Fact]
    public async Task Org_DeactivateUnit_OutOfScope_ThrowsForbidden()
    {
        await using var work = await UnitOfWork.BeginAsync(_fixture.DataSource, CancellationToken.None);

        await Should.ThrowAsync<ForbiddenException>(() => Orgs.DeactivateUnitAsync(
            OtherOrgUnit, ChildStrategy.Refuse, null, ScopedCaller(), work, CancellationToken.None));
    }

    [Fact]
    public async Task Geo_DeactivateArea_InScope_Deactivates()
    {
        await using var work = await UnitOfWork.BeginAsync(_fixture.DataSource, CancellationToken.None);

        var result = await Geo.DeactivateAreaAsync(
            AreaLeaf, ChildStrategy.Refuse, null, ScopedCaller(), work, CancellationToken.None);
        result.Outcome.ShouldBe(DeactivationOutcome.Deactivated);
        await work.CommitAsync(CancellationToken.None);

        var status = await _fixture.ScalarAsync<string>(
            $"SELECT status FROM federation.geographic_areas WHERE id = '{AreaLeaf}';");
        status.ShouldBe("INACTIVE");
    }

    // ---- 5-H2: already-inactive deactivate is an idempotent no-op ---------

    [Fact]
    public async Task Org_DeactivateUnit_AlreadyInactive_ReportsAlreadyInactive_NotAChildrenConflict()
    {
        // UnitInactive is seeded INACTIVE with an ACTIVE child. Without the "already inactive"
        // early return, the Refuse path would surface that child as a 409 conflict on a node
        // that is already retired. Post-5-M10 the repo distinguishes this from a real
        // deactivation, so the endpoint returns 409 "already inactive" and writes no audit row.
        await using var work = await UnitOfWork.BeginAsync(_fixture.DataSource, CancellationToken.None);

        var result = await Orgs.DeactivateUnitAsync(
            UnitInactive, ChildStrategy.Refuse, null, ScopedCaller(), work, CancellationToken.None);

        result.Outcome.ShouldBe(DeactivationOutcome.AlreadyInactive);
        await work.CommitAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Org_DeactivateUnit_NonexistentId_ReportsNotFound_NoWrite()
    {
        // 5-M10: an unscoped caller deactivating an id that does not exist used to fall through
        // to an UPDATE that hit zero rows — a phantom 204 and a false audit row. The repo now
        // reports NotFound before touching anything.
        await using var work = await UnitOfWork.BeginAsync(_fixture.DataSource, CancellationToken.None);

        var result = await Orgs.DeactivateUnitAsync(
            Guid.NewGuid(), ChildStrategy.Refuse, null, UnscopedCaller(), work, CancellationToken.None);

        result.Outcome.ShouldBe(DeactivationOutcome.NotFound);
        await work.CommitAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Geo_DeactivateArea_NonexistentId_ReportsNotFound()
    {
        await using var work = await UnitOfWork.BeginAsync(_fixture.DataSource, CancellationToken.None);

        var result = await Geo.DeactivateAreaAsync(
            Guid.NewGuid(), ChildStrategy.Refuse, null, UnscopedCaller(), work, CancellationToken.None);

        result.Outcome.ShouldBe(DeactivationOutcome.NotFound);
        await work.CommitAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Geo_DeactivateArea_AlreadyInactive_ReportsAlreadyInactive()
    {
        await using (var first = await UnitOfWork.BeginAsync(_fixture.DataSource, CancellationToken.None))
        {
            await Geo.DeactivateAreaAsync(
                AreaLeaf, ChildStrategy.Refuse, null, ScopedCaller(), first, CancellationToken.None);
            await first.CommitAsync(CancellationToken.None);
        }

        await using var second = await UnitOfWork.BeginAsync(_fixture.DataSource, CancellationToken.None);
        (await Geo.DeactivateAreaAsync(
            AreaLeaf, ChildStrategy.Cascade, null, ScopedCaller(), second, CancellationToken.None))
            .Outcome.ShouldBe(DeactivationOutcome.AlreadyInactive);
        await second.CommitAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Org_DeactivateUnit_Reparent_InScope_MovesChildThenDeactivates()
    {
        // Exercises the reparent branch and its newParentId reach check (pre-5-H1 this 500'd for
        // a scoped caller like every other deactivate). CcRoot → CcMid → CcLeaf; deactivate
        // CcMid, reparent its children onto CcRoot (in reach, not inside the branch).
        await using var work = await UnitOfWork.BeginAsync(_fixture.DataSource, CancellationToken.None);

        var result = await Orgs.DeactivateUnitAsync(
            CcMid, ChildStrategy.Reparent, CcRoot, ScopedCaller(), work, CancellationToken.None);
        result.Outcome.ShouldBe(DeactivationOutcome.Deactivated);
        await work.CommitAsync(CancellationToken.None);

        var leafParent = await _fixture.ScalarAsync<Guid>(
            $"SELECT parent_unit_id FROM federation.organization_units WHERE id = '{CcLeaf}';");
        leafParent.ShouldBe(CcRoot);

        var midStatus = await _fixture.ScalarAsync<string>(
            $"SELECT status FROM federation.organization_units WHERE id = '{CcMid}';");
        midStatus.ShouldBe("INACTIVE");

        var leafStatus = await _fixture.ScalarAsync<string>(
            $"SELECT status FROM federation.organization_units WHERE id = '{CcLeaf}';");
        leafStatus.ShouldBe("ACTIVE");
    }

    [Fact]
    public async Task Org_DeactivateUnit_Reparent_TargetOutOfScope_ThrowsForbidden()
    {
        await using var work = await UnitOfWork.BeginAsync(_fixture.DataSource, CancellationToken.None);

        await Should.ThrowAsync<ForbiddenException>(() => Orgs.DeactivateUnitAsync(
            CcMid, ChildStrategy.Reparent, OtherOrgUnit, ScopedCaller(), work, CancellationToken.None));
    }

    // ---- 5-M8: nothing live may attach under a retired parent -------------

    [Fact]
    public async Task Org_UpsertUnit_UnderInactiveParent_ThrowsInvalidReference()
    {
        await using var work = await UnitOfWork.BeginAsync(_fixture.DataSource, CancellationToken.None);

        await Should.ThrowAsync<InvalidReferenceException>(() => Orgs.UpsertUnitAsync(new OrganizationUnit
        {
            OrganizationId = PostgresFixture.OrgId,
            ParentUnitId = UnitInactive,
            Code = "U-UNDER-DEAD",
            Name = "Child Of A Retired Unit",
            UnitType = "STATION",
        }, ScopedCaller(), work, CancellationToken.None));
    }

    [Fact]
    public async Task Org_DeactivateUnit_ReparentOntoInactiveTarget_ThrowsInvalidReference()
    {
        await using var work = await UnitOfWork.BeginAsync(_fixture.DataSource, CancellationToken.None);

        await Should.ThrowAsync<InvalidReferenceException>(() => Orgs.DeactivateUnitAsync(
            CcMid, ChildStrategy.Reparent, UnitInactive, ScopedCaller(), work, CancellationToken.None));
    }

    [Fact]
    public async Task Geo_UpsertArea_UnderInactiveParent_ThrowsInvalidReference()
    {
        await using var work = await UnitOfWork.BeginAsync(_fixture.DataSource, CancellationToken.None);

        await Should.ThrowAsync<InvalidReferenceException>(() => Geo.UpsertAreaAsync(new GeographicArea
        {
            ParentAreaId = AreaInactive,
            Code = "A-UNDER-DEAD",
            Name = "Child Of A Retired Area",
            AreaType = "VILLAGE",
        }, ScopedCaller(), work, CancellationToken.None));
    }

    [Fact]
    public async Task Geo_UpsertChildArea_UnderInactiveArea_ThrowsInvalidReference()
    {
        await using var work = await UnitOfWork.BeginAsync(_fixture.DataSource, CancellationToken.None);

        await Should.ThrowAsync<InvalidReferenceException>(() => Geo.UpsertAreaAsync(new GeographicArea
        {
            ParentAreaId = AreaInactive,
            Code = "S-UNDER-DEAD",
            Name = "Sector In A Retired Area",
            AreaType = "SECTOR",
        }, ScopedCaller(), work, CancellationToken.None));
    }

    // ---- 5-H2: the hierarchy-wide advisory lock serializes deactivations --

    [Fact]
    public async Task Org_DeactivateUnit_HeldByAnotherTransaction_BlocksUntilCommit()
    {
        // CcLeaf and UnitLeaf are in different subtrees with no row overlap, so nothing but the
        // coarse per-hierarchy advisory lock can serialize them. A holds it; B must wait.
        await using var workA = await UnitOfWork.BeginAsync(_fixture.DataSource, CancellationToken.None);

        (await Orgs.DeactivateUnitAsync(
            CcLeaf, ChildStrategy.Cascade, null, ScopedCaller(), workA, CancellationToken.None))
            .Outcome.ShouldBe(DeactivationOutcome.Deactivated);

        var bCompleted = false;
        var taskB = Task.Run(async () =>
        {
            await using var workB = await UnitOfWork.BeginAsync(_fixture.DataSource, CancellationToken.None);
            await Orgs.DeactivateUnitAsync(
                UnitLeaf, ChildStrategy.Cascade, null, ScopedCaller(), workB, CancellationToken.None);
            await workB.CommitAsync(CancellationToken.None);
            bCompleted = true;
        });

        var raced = await Task.WhenAny(taskB, Task.Delay(TimeSpan.FromMilliseconds(750)));
        raced.ShouldNotBe((Task)taskB, "B must block while A holds the advisory lock");
        bCompleted.ShouldBeFalse();

        await workA.CommitAsync(CancellationToken.None);
        await taskB; // unblocks once A's transaction releases the lock

        var leafStatus = await _fixture.ScalarAsync<string>(
            $"SELECT status FROM federation.organization_units WHERE id = '{UnitLeaf}';");
        leafStatus.ShouldBe("INACTIVE");
    }

    // ---- 5-M1: GET /organizations needs only organization.read ------------

    [Fact]
    public async Task Org_ListAndGet_CallerWithoutGeographyRead_StillWorks()
    {
        // ReadOnlyCaller's role has organization.read but NOT geography.read. The repo path only
        // ever required organization.read; the endpoint filter (HierarchyEndpoints.cs GET
        // "/organizations" and "/{id}") advertised geography.read and 403'd this caller before
        // the handler ran. No WebApplicationFactory here — this asserts the repo contract the
        // corrected filter now matches.
        var list = await Orgs.ListAsync(ReadOnlyCaller(), CancellationToken.None);
        list.Select(o => o.Id).ShouldContain(PostgresFixture.OrgId);

        (await Orgs.GetAsync(PostgresFixture.OrgId, ReadOnlyCaller(), CancellationToken.None))
            .ShouldNotBeNull();
    }

    // ---- update: GET one unit + edit organization / unit by id -----------

    [Fact]
    public async Task Org_GetUnit_InScope_Returns_OutOfScope_Null()
    {
        (await Orgs.GetUnitAsync(PostgresFixture.AhmedabadCp, ScopedCaller(), CancellationToken.None))
            .ShouldNotBeNull();

        (await Orgs.GetUnitAsync(OtherOrgUnit, ScopedCaller(), CancellationToken.None))
            .ShouldBeNull();
    }

    [Fact]
    public async Task Org_UpsertUnit_WithId_UpdatesInPlace_OrganizationUnchanged()
    {
        await using var work = await UnitOfWork.BeginAsync(_fixture.DataSource, CancellationToken.None);

        var returned = await Orgs.UpsertUnitAsync(new OrganizationUnit
        {
            Id = UnitLeaf,
            OrganizationId = PostgresFixture.OrgId,
            ParentUnitId = PostgresFixture.AhmedabadCp,
            Code = "U-LEAF",
            Name = "Unit Leaf Renamed",
            UnitType = "ZONE",
        }, ScopedCaller(), work, CancellationToken.None);
        returned.ShouldBe(UnitLeaf);
        await work.CommitAsync(CancellationToken.None);

        var after = await Orgs.GetUnitAsync(UnitLeaf, ScopedCaller(), CancellationToken.None);
        after!.Name.ShouldBe("Unit Leaf Renamed");
        after.UnitType.ShouldBe("ZONE");
        after.OrganizationId.ShouldBe(PostgresFixture.OrgId);
    }

    [Fact]
    public async Task Org_UpsertUnit_FieldEdit_NotBlockedByInactiveParent()
    {
        // UnitInactiveChild is ACTIVE under UnitInactive (INACTIVE). A rename must go through —
        // parentIsChanging: false suppresses the parent-ACTIVE assertion.
        await using var work = await UnitOfWork.BeginAsync(_fixture.DataSource, CancellationToken.None);

        await Orgs.UpsertUnitAsync(new OrganizationUnit
        {
            Id = UnitInactiveChild,
            OrganizationId = PostgresFixture.OrgId,
            ParentUnitId = UnitInactive,
            Code = "U-INACT-C",
            Name = "Renamed Under A Dead Parent",
            UnitType = "STATION",
        }, ScopedCaller(), work, CancellationToken.None, parentIsChanging: false);
        await work.CommitAsync(CancellationToken.None);

        (await Orgs.GetUnitAsync(UnitInactiveChild, ScopedCaller(), CancellationToken.None))!
            .Name.ShouldBe("Renamed Under A Dead Parent");
    }

    [Fact]
    public async Task Org_UpsertUnit_AttachUnderInactiveParent_StillRejected()
    {
        // The default (parentIsChanging: true) must still refuse attaching a new unit under a
        // retired parent — 5-M8.
        await using var work = await UnitOfWork.BeginAsync(_fixture.DataSource, CancellationToken.None);

        await Should.ThrowAsync<InvalidReferenceException>(() => Orgs.UpsertUnitAsync(
            new OrganizationUnit
            {
                OrganizationId = PostgresFixture.OrgId,
                ParentUnitId = UnitInactive,
                Code = "U-NEW-UNDER-DEAD",
                Name = "Should Not Attach",
                UnitType = "STATION",
            }, ScopedCaller(), work, CancellationToken.None));
    }

    [Fact]
    public async Task Org_UpsertOrganization_WithId_Updates()
    {
        await using var work = await UnitOfWork.BeginAsync(_fixture.DataSource, CancellationToken.None);

        await Orgs.UpsertAsync(new Organization
        {
            Id = PostgresFixture.OrgId,
            Code = "POLICE",
            Name = "Police Department (renamed)",
            OrganizationType = "DEPARTMENT",
            Description = "edited",
        }, UnscopedCaller(), work, CancellationToken.None);
        await work.CommitAsync(CancellationToken.None);

        var after = await Orgs.GetAsync(PostgresFixture.OrgId, UnscopedCaller(), CancellationToken.None);
        after!.Name.ShouldBe("Police Department (renamed)");
        after.Description.ShouldBe("edited");
    }
}
