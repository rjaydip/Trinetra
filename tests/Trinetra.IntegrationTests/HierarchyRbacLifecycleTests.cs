using Shouldly;
using Trinetra.Federation.Storage;
using Trinetra.Federation.Storage.Repositories;

namespace Trinetra.IntegrationTests;

/// <summary>
/// The hierarchy + role / access-group lifecycle wave (docs/API-PLAN-HIERARCHY-RBAC.md):
/// activate routes and the PUT-on-INACTIVE guard (P3), direct re-parent (P1 / P2a),
/// cross-organization move (P2b), role DRAFT / soft-delete (P7 / P8) and access-group
/// role-status / timestamp surfacing (P11 / P13). Repository level, matching the rest of this
/// suite — the endpoint-only P11 / P12 409 shaping is an accepted gap (no WebApplicationFactory).
/// </summary>
public sealed class HierarchyRbacLifecycleTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly PostgresFixture _fixture;

    private static readonly Guid OrgB       = Guid.Parse("11111111-2222-2222-2222-111111111111");
    private static readonly Guid OrgBRoot   = Guid.Parse("a3333333-0000-0000-0000-00000000b001");
    private static readonly Guid MoveRoot   = Guid.Parse("a3333333-0000-0000-0000-00000000c001");
    private static readonly Guid MoveChild  = Guid.Parse("a3333333-0000-0000-0000-00000000c002");
    private static readonly Guid InactiveUnit = Guid.Parse("a3333333-0000-0000-0000-00000000d001");
    private static readonly Guid ChildOfInactive = Guid.Parse("a3333333-0000-0000-0000-00000000d002");
    private static readonly Guid SiblingUnit = Guid.Parse("a3333333-0000-0000-0000-00000000e001");
    private static readonly Guid OtherRootUnit = Guid.Parse("a3333333-0000-0000-0000-00000000e002");

    private static readonly Guid InactiveArea = Guid.Parse("b3333333-0000-0000-0000-00000000d001");
    private static readonly Guid ZoneA = Guid.Parse("b3333333-0000-0000-0000-00000000f001");
    private static readonly Guid ZoneB = Guid.Parse("b3333333-0000-0000-0000-00000000f002");
    private static readonly Guid WardUnderA = Guid.Parse("b3333333-0000-0000-0000-00000000f003");

    private static readonly Guid ScopedUser  = Guid.Parse("d3333333-0000-0000-0000-00000000a001");
    private static readonly Guid ScopedGroup = Guid.Parse("f3333333-0000-0000-0000-00000000a001");
    private static readonly Guid ScopedOrgScope = Guid.Parse("e3333333-0000-0000-0000-00000000a001");
    private static readonly Guid ScopedGeoScope = Guid.Parse("e3333333-0000-0000-0000-00000000a002");

    public HierarchyRbacLifecycleTests(PostgresFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        await _fixture.ResetTargetsAsync();

        await _fixture.ExecuteAsync($"""
            DELETE FROM federation.user_groups WHERE user_id = '{ScopedUser}'
                OR group_id IN (SELECT id FROM federation.access_groups WHERE code LIKE 'HRL-%');
            DELETE FROM federation.group_scopes WHERE group_id IN
                (SELECT id FROM federation.access_groups WHERE code LIKE 'HRL-%');
            DELETE FROM federation.access_groups WHERE code LIKE 'HRL-%';
            DELETE FROM federation.scopes WHERE id IN ('{ScopedOrgScope}', '{ScopedGeoScope}')
                OR organization_unit_id IN ('{MoveChild}', '{MoveRoot}', '{SiblingUnit}',
                    '{InactiveUnit}', '{ChildOfInactive}', '{OrgBRoot}')
                OR geographic_area_id IN ('{ZoneA}', '{ZoneB}', '{WardUnderA}', '{InactiveArea}');
            DELETE FROM federation.platform_users WHERE id = '{ScopedUser}';
            DELETE FROM federation.role_permissions WHERE role_id IN
                (SELECT id FROM federation.roles WHERE code LIKE 'HRL-%');
            DELETE FROM federation.roles WHERE code LIKE 'HRL-%';

            DELETE FROM federation.organization_units WHERE id IN
                ('{MoveChild}', '{MoveRoot}', '{ChildOfInactive}', '{InactiveUnit}',
                 '{SiblingUnit}', '{OtherRootUnit}', '{OrgBRoot}');
            DELETE FROM federation.geographic_areas WHERE id IN
                ('{WardUnderA}', '{ZoneA}', '{ZoneB}', '{InactiveArea}',
                 'b3333333-0000-0000-0000-00000000d901')
                OR parent_area_id IN ('{WardUnderA}', '{ZoneA}', '{ZoneB}', '{InactiveArea}');
            DELETE FROM federation.organizations WHERE id = '{OrgB}';

            INSERT INTO federation.organizations (id, code, name, organization_type)
            VALUES ('{OrgB}', 'ORG-B', 'Second Org', 'DEPARTMENT');
            INSERT INTO federation.organization_units (id, organization_id, parent_unit_id, code, name, unit_type)
            VALUES ('{OrgBRoot}', '{OrgB}', NULL, 'ORG-B-ROOT', 'Org B Root', 'DEPARTMENT');

            -- A small subtree under AhmedabadCp to move / re-parent.
            INSERT INTO federation.organization_units (id, organization_id, parent_unit_id, code, name, unit_type)
            VALUES
                ('{MoveRoot}', '{PostgresFixture.OrgId}', '{PostgresFixture.AhmedabadCp}', 'MV-ROOT', 'Move Root', 'ZONE'),
                ('{MoveChild}', '{PostgresFixture.OrgId}', '{MoveRoot}', 'MV-CHILD', 'Move Child', 'STATION'),
                ('{SiblingUnit}', '{PostgresFixture.OrgId}', '{PostgresFixture.AhmedabadCp}', 'MV-SIB', 'Sibling', 'ZONE'),
                ('{OtherRootUnit}', '{PostgresFixture.OrgId}', NULL, 'MV-OTHER-ROOT', 'Other Root (same org, out of reach)', 'DEPARTMENT'),
                ('{InactiveUnit}', '{PostgresFixture.OrgId}', '{PostgresFixture.AhmedabadCp}', 'MV-INACT', 'Inactive', 'ZONE'),
                ('{ChildOfInactive}', '{PostgresFixture.OrgId}', '{InactiveUnit}', 'MV-INACT-C', 'Child Of Inactive', 'STATION');
            UPDATE federation.organization_units SET status = 'INACTIVE' WHERE id = '{InactiveUnit}';
            UPDATE federation.organization_units SET status = 'INACTIVE' WHERE id = '{ChildOfInactive}';

            INSERT INTO federation.geographic_areas (id, parent_area_id, code, name, area_type)
            VALUES
                ('{ZoneA}', '{PostgresFixture.DistrictId}', 'ZA', 'Zone A', 'ZONE'),
                ('{ZoneB}', '{PostgresFixture.DistrictId}', 'ZB', 'Zone B', 'ZONE'),
                ('{WardUnderA}', '{ZoneA}', 'W1', 'Ward 1', 'WARD'),
                ('{InactiveArea}', '{PostgresFixture.DistrictId}', 'IA', 'Inactive Area', 'ZONE');
            UPDATE federation.geographic_areas SET status = 'INACTIVE' WHERE id = '{InactiveArea}';

            -- A caller scoped to PoliceUnit + DistrictId on both dimensions.
            INSERT INTO federation.roles (code, name, description, is_system, status)
            VALUES ('HRL-SCOPED', 'HRL Scoped', 'test', FALSE, 'ACTIVE');
            INSERT INTO federation.role_permissions (role_id, permission_code)
            SELECT r.id, p.code FROM federation.roles r, unnest(ARRAY[
                'organization.read','organization.manage','geography.read','geography.manage'
            ]) AS p(code) WHERE r.code = 'HRL-SCOPED';

            INSERT INTO federation.platform_users
                (id, username, display_name, password_hash, password_salt, password_iterations)
            VALUES ('{ScopedUser}', 'hrl-scoped', 'HRL Scoped', '\x00', '\x00', 600000);

            INSERT INTO federation.scopes (id, scope_type, organization_unit_id)
            VALUES ('{ScopedOrgScope}', 'ORGANIZATION', '{PostgresFixture.PoliceUnit}');
            INSERT INTO federation.scopes (id, scope_type, geographic_area_id)
            VALUES ('{ScopedGeoScope}', 'GEOGRAPHY', '{PostgresFixture.DistrictId}');
            INSERT INTO federation.access_groups (id, code, name, role_id, status)
            SELECT '{ScopedGroup}', 'HRL-SCOPED-GROUP', 'HRL Scoped Group', r.id, 'ACTIVE'
            FROM federation.roles r WHERE r.code = 'HRL-SCOPED';
            INSERT INTO federation.group_scopes VALUES
                ('{ScopedGroup}', '{ScopedOrgScope}'), ('{ScopedGroup}', '{ScopedGeoScope}');
            INSERT INTO federation.user_groups (user_id, group_id) VALUES ('{ScopedUser}', '{ScopedGroup}');
            """);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private OrganizationRepository Orgs => new(_fixture.DataSource);
    private GeographyRepository Geo => new(_fixture.DataSource);
    private AccessGroupRepository Groups => new(_fixture.DataSource);
    private RoleRepository Roles => new(_fixture.DataSource);

    private Task<UnitOfWork> Begin() => UnitOfWork.BeginAsync(_fixture.DataSource, CancellationToken.None);

    private static CallerContext Unscoped() => new()
    {
        Actor = "hrl-system",
        Permissions = new HashSet<string>(StringComparer.Ordinal)
        {
            "organization.read", "organization.manage", "geography.read", "geography.manage",
            "role.manage", "group.manage", "group.read", "role.read",
        },
        IsSystem = true,
    };

    private static CallerContext Scoped() => new()
    {
        UserId = ScopedUser,
        Actor = "hrl-scoped",
        Permissions = new HashSet<string>(StringComparer.Ordinal)
        {
            "organization.read", "organization.manage", "geography.read", "geography.manage",
        },
    };

    // ---- P3: activate --------------------------------------------------------

    [Fact]
    public async Task P3_ActivateUnit_UnderActiveParent_Activates()
    {
        await using var work = await Begin();
        var r = await Orgs.ActivateUnitAsync(InactiveUnit, Unscoped(), work, CancellationToken.None);
        r.ShouldBe(ActivateResult.Activated);
        await work.CommitAsync(CancellationToken.None);

        (await _fixture.ScalarAsync<string>(
            $"SELECT status FROM federation.organization_units WHERE id = '{InactiveUnit}';"))
            .ShouldBe("ACTIVE");
        // Does not cascade.
        (await _fixture.ScalarAsync<string>(
            $"SELECT status FROM federation.organization_units WHERE id = '{ChildOfInactive}';"))
            .ShouldBe("INACTIVE");
    }

    [Fact]
    public async Task P3_ActivateUnit_UnderInactiveParent_IsRefused()
    {
        await using var work = await Begin();
        (await Orgs.ActivateUnitAsync(ChildOfInactive, Unscoped(), work, CancellationToken.None))
            .ShouldBe(ActivateResult.ParentInactive);
    }

    [Fact]
    public async Task P3_ActivateUnit_AlreadyActive_IsNoOp()
    {
        await using var work = await Begin();
        (await Orgs.ActivateUnitAsync(SiblingUnit, Unscoped(), work, CancellationToken.None))
            .ShouldBe(ActivateResult.AlreadyActive);
    }

    [Fact]
    public async Task P3_ActivateUnit_Unknown_IsNotFound()
    {
        await using var work = await Begin();
        (await Orgs.ActivateUnitAsync(Guid.NewGuid(), Unscoped(), work, CancellationToken.None))
            .ShouldBe(ActivateResult.NotFound);
    }

    [Fact]
    public async Task P3_ActivateArea_UnderInactiveParent_IsRefused()
    {
        // A child seeded directly under the inactive area (the repo would refuse to attach one),
        // itself INACTIVE — activating it must be refused because its parent is dead.
        var child = Guid.Parse("b3333333-0000-0000-0000-00000000d901");
        await _fixture.ExecuteAsync($"""
            INSERT INTO federation.geographic_areas (id, parent_area_id, code, name, area_type, status)
            VALUES ('{child}', '{InactiveArea}', 'IA-C', 'Child', 'WARD', 'INACTIVE');
            """);

        await using var work = await Begin();
        (await Geo.ActivateAreaAsync(child, Unscoped(), work, CancellationToken.None))
            .ShouldBe(ActivateResult.ParentInactive);
    }

    // ---- P1: direct re-parent (organization units) --------------------------

    [Fact]
    public async Task P1_Reparent_SameOrg_MovesTheSubtree()
    {
        await using var work = await Begin();
        await Orgs.UpsertUnitAsync(new OrganizationUnit
        {
            Id = MoveRoot,
            OrganizationId = PostgresFixture.OrgId,
            ParentUnitId = SiblingUnit,
            Code = "MV-ROOT", Name = "Move Root", UnitType = "ZONE",
        }, Unscoped(), work, CancellationToken.None);
        await work.CommitAsync(CancellationToken.None);

        (await _fixture.ScalarAsync<Guid>(
            $"SELECT parent_unit_id FROM federation.organization_units WHERE id = '{MoveRoot}';"))
            .ShouldBe(SiblingUnit);
        // Child moved implicitly — still points at MoveRoot.
        (await _fixture.ScalarAsync<Guid>(
            $"SELECT parent_unit_id FROM federation.organization_units WHERE id = '{MoveChild}';"))
            .ShouldBe(MoveRoot);
    }

    [Fact]
    public async Task P1_Reparent_UnderOwnDescendant_IsRejected()
    {
        await using var work = await Begin();
        var ex = await Should.ThrowAsync<InvalidOperationException>(() => Orgs.UpsertUnitAsync(
            new OrganizationUnit
            {
                Id = MoveRoot,
                OrganizationId = PostgresFixture.OrgId,
                ParentUnitId = MoveChild,
                Code = "MV-ROOT", Name = "Move Root", UnitType = "ZONE",
            }, Unscoped(), work, CancellationToken.None));
        ex.Message.ShouldContain("beneath itself");
    }

    [Fact]
    public async Task P1_Reparent_UnderSelf_IsRejected()
    {
        await using var work = await Begin();
        var ex = await Should.ThrowAsync<InvalidOperationException>(() => Orgs.UpsertUnitAsync(
            new OrganizationUnit
            {
                Id = MoveRoot,
                OrganizationId = PostgresFixture.OrgId,
                ParentUnitId = MoveRoot,
                Code = "MV-ROOT", Name = "Move Root", UnitType = "ZONE",
            }, Unscoped(), work, CancellationToken.None));
        ex.Message.ShouldContain("its own parent");
    }

    [Fact]
    public async Task P1_Reparent_IntoADifferentOrganization_IsRejected()
    {
        await using var work = await Begin();
        var ex = await Should.ThrowAsync<InvalidOperationException>(() => Orgs.UpsertUnitAsync(
            new OrganizationUnit
            {
                Id = MoveRoot,
                OrganizationId = PostgresFixture.OrgId,
                ParentUnitId = OrgBRoot,
                Code = "MV-ROOT", Name = "Move Root", UnitType = "ZONE",
            }, Unscoped(), work, CancellationToken.None));
        ex.Message.ShouldContain("between organizations");
    }

    [Fact]
    public async Task P1_Reparent_OntoInactiveParent_IsRejected()
    {
        await using var work = await Begin();
        await Should.ThrowAsync<InvalidReferenceException>(() => Orgs.UpsertUnitAsync(
            new OrganizationUnit
            {
                Id = MoveRoot,
                OrganizationId = PostgresFixture.OrgId,
                ParentUnitId = InactiveUnit,
                Code = "MV-ROOT", Name = "Move Root", UnitType = "ZONE",
            }, Unscoped(), work, CancellationToken.None));
    }

    [Fact]
    public async Task P1_Reparent_ScopedCaller_CannotReachDestination()
    {
        // OtherRootUnit is in the same organization but outside the scoped caller's reach.
        await using var work = await Begin();
        await Should.ThrowAsync<ForbiddenException>(() => Orgs.UpsertUnitAsync(
            new OrganizationUnit
            {
                Id = MoveRoot,
                OrganizationId = PostgresFixture.OrgId,
                ParentUnitId = OtherRootUnit,
                Code = "MV-ROOT", Name = "Move Root", UnitType = "ZONE",
            }, Scoped(), work, CancellationToken.None));
    }

    [Fact]
    public async Task P1_Reparent_ToRoot_ByScopedCaller_IsForbidden()
    {
        await using var work = await Begin();
        await Should.ThrowAsync<ForbiddenException>(() => Orgs.UpsertUnitAsync(
            new OrganizationUnit
            {
                Id = MoveChild,
                OrganizationId = PostgresFixture.OrgId,
                ParentUnitId = null,
                Code = "MV-CHILD", Name = "Move Child", UnitType = "STATION",
            }, Scoped(), work, CancellationToken.None));
    }

    // ---- P2a: direct re-parent (geographic areas) --------------------------

    [Fact]
    public async Task P2a_Reparent_Valid()
    {
        await using var work = await Begin();
        await Geo.UpsertAreaAsync(new GeographicArea
        {
            Id = WardUnderA, ParentAreaId = ZoneB, Code = "W1", Name = "Ward 1", AreaType = "WARD",
        }, Unscoped(), work, CancellationToken.None);
        await work.CommitAsync(CancellationToken.None);

        (await _fixture.ScalarAsync<Guid>(
            $"SELECT parent_area_id FROM federation.geographic_areas WHERE id = '{WardUnderA}';"))
            .ShouldBe(ZoneB);
    }

    [Fact]
    public async Task P2a_Reparent_LevelOrderViolation_IsAClean400Signal()
    {
        // ZoneB (ZONE, order 50) under WardUnderA (WARD, order 60) — coarser under finer, and
        // ZoneB is not a descendant of WardUnderA so this exercises the trigger, not the
        // descendant pre-check.
        await using var work = await Begin();
        var ex = await Should.ThrowAsync<Exception>(() => Geo.UpsertAreaAsync(new GeographicArea
        {
            Id = ZoneB, ParentAreaId = WardUnderA, Code = "ZB", Name = "Zone B", AreaType = "ZONE",
        }, Unscoped(), work, CancellationToken.None));
        ex.Message.ShouldContain("finer level");
    }

    [Fact]
    public async Task P2a_Reparent_UnderOwnDescendant_IsRejected()
    {
        await using var work = await Begin();
        var ex = await Should.ThrowAsync<InvalidOperationException>(() => Geo.UpsertAreaAsync(
            new GeographicArea
            {
                Id = ZoneA, ParentAreaId = WardUnderA, Code = "ZA", Name = "Zone A", AreaType = "ZONE",
            }, Unscoped(), work, CancellationToken.None));
        ex.Message.ShouldContain("beneath itself");
    }

    // ---- P2b: cross-organization move ------------------------------------------

    [Fact]
    public async Task P2b_Move_NoAffectedGroups_RewritesTheSubtreeOrganization()
    {
        await using var work = await Begin();
        var r = await Orgs.MoveUnitToOrganizationAsync(
            MoveRoot, OrgBRoot, confirmScopeImpact: false, Unscoped(), work, CancellationToken.None);
        r.Error.ShouldBe(MoveOrgError.None);
        r.SubtreeSize.ShouldBe(2);
        await work.CommitAsync(CancellationToken.None);

        (await _fixture.ScalarAsync<Guid>(
            $"SELECT organization_id FROM federation.organization_units WHERE id = '{MoveRoot}';"))
            .ShouldBe(OrgB);
        (await _fixture.ScalarAsync<Guid>(
            $"SELECT organization_id FROM federation.organization_units WHERE id = '{MoveChild}';"))
            .ShouldBe(OrgB);
    }

    [Fact]
    public async Task P2b_Move_WithAffectedGroup_NeedsConfirmationThenSucceeds()
    {
        // A group scoped to MoveChild.
        await _fixture.ExecuteAsync($"""
            INSERT INTO federation.scopes (id, scope_type, organization_unit_id)
            VALUES ('e3333333-0000-0000-0000-00000000b901', 'ORGANIZATION', '{MoveChild}');
            INSERT INTO federation.access_groups (id, code, name, role_id, status)
            SELECT 'f3333333-0000-0000-0000-00000000b901', 'HRL-AFFECTED', 'Affected', r.id, 'ACTIVE'
            FROM federation.roles r WHERE r.code = 'HRL-SCOPED';
            INSERT INTO federation.group_scopes VALUES
                ('f3333333-0000-0000-0000-00000000b901', 'e3333333-0000-0000-0000-00000000b901');
            """);

        await using (var work = await Begin())
        {
            var blocked = await Orgs.MoveUnitToOrganizationAsync(
                MoveRoot, OrgBRoot, confirmScopeImpact: false, Unscoped(), work, CancellationToken.None);
            blocked.Error.ShouldBe(MoveOrgError.NeedsConfirmation);
            blocked.AffectedGroups.ShouldContain(g => g.Code == "HRL-AFFECTED");
        }

        await using (var work = await Begin())
        {
            var ok = await Orgs.MoveUnitToOrganizationAsync(
                MoveRoot, OrgBRoot, confirmScopeImpact: true, Unscoped(), work, CancellationToken.None);
            ok.Error.ShouldBe(MoveOrgError.None);
            await work.CommitAsync(CancellationToken.None);
        }

        (await _fixture.ScalarAsync<Guid>(
            $"SELECT organization_id FROM federation.organization_units WHERE id = '{MoveChild}';"))
            .ShouldBe(OrgB);
    }

    [Fact]
    public async Task P2b_Move_ByScopedCaller_IsRefused()
    {
        await using var work = await Begin();
        (await Orgs.MoveUnitToOrganizationAsync(
            MoveRoot, OrgBRoot, confirmScopeImpact: true, Scoped(), work, CancellationToken.None))
            .Error.ShouldBe(MoveOrgError.NotUnscoped);
    }

    [Fact]
    public async Task P2b_Move_SameOrganization_IsRejected()
    {
        await using var work = await Begin();
        (await Orgs.MoveUnitToOrganizationAsync(
            MoveRoot, SiblingUnit, confirmScopeImpact: true, Unscoped(), work, CancellationToken.None))
            .Error.ShouldBe(MoveOrgError.SameOrganization);
    }

    [Fact]
    public async Task P2b_DirectMalformedUpdate_IsStillBlockedByTheConstraint()
    {
        // AC5: changing a unit's organization_id while its parent stays put must still fail.
        var ex = await Should.ThrowAsync<Npgsql.PostgresException>(() => _fixture.ExecuteAsync(
            $"UPDATE federation.organization_units SET organization_id = '{OrgB}' WHERE id = '{MoveChild}';"));
        ex.MessageText.ShouldContain("same organization as its parent");
    }

    // ---- P11 / P13: access-group role status + timestamps ------------------

    [Fact]
    public async Task P11_GroupDetail_CarriesRoleStatus_AndGrantsEffectiveTracksIt()
    {
        await _fixture.ExecuteAsync($"""
            INSERT INTO federation.roles (code, name, is_system, status)
            VALUES ('HRL-DRAFT-ROLE', 'Draft Role', FALSE, 'DRAFT');
            INSERT INTO federation.access_groups (id, code, name, role_id, status)
            SELECT 'f3333333-0000-0000-0000-00000000c901', 'HRL-ON-DRAFT', 'On Draft', r.id, 'ACTIVE'
            FROM federation.roles r WHERE r.code = 'HRL-DRAFT-ROLE';
            """);

        var g = await Groups.GetAsync(
            Guid.Parse("f3333333-0000-0000-0000-00000000c901"), CancellationToken.None);
        g.ShouldNotBeNull();
        g!.RoleStatus.ShouldBe("DRAFT");
        g.GrantsEffective.ShouldBeFalse();
    }

    [Fact]
    public async Task P13_GroupDetail_CarriesTimestamps_AndScopeAddBumpsUpdatedAt()
    {
        var groupId = Guid.Parse("f3333333-0000-0000-0000-00000000d901");
        await _fixture.ExecuteAsync($"""
            INSERT INTO federation.access_groups (id, code, name, role_id, status, created_at, updated_at)
            SELECT '{groupId}', 'HRL-TS', 'Timestamps', r.id, 'DRAFT',
                   now() - interval '1 hour', now() - interval '1 hour'
            FROM federation.roles r WHERE r.code = 'HRL-SCOPED';
            """);

        var before = await Groups.GetAsync(groupId, CancellationToken.None);
        before!.CreatedAt.ShouldBeLessThan(DateTimeOffset.UtcNow.AddMinutes(-30));

        await using (var work = await Begin())
        {
            await Groups.AddScopeAsync(
                groupId, "ORGANIZATION", PostgresFixture.PoliceUnit, null, null, null, null,
                work, CancellationToken.None);
            await work.CommitAsync(CancellationToken.None);
        }

        var after = await Groups.GetAsync(groupId, CancellationToken.None);
        after!.UpdatedAt.ShouldBeGreaterThan(before.UpdatedAt);
    }

    // ---- P8: role soft-delete keeps a referencing group intact -------------

    [Fact]
    public async Task P8_SoftDeleteRoleInUse_LeavesGroupPointingAtItButGrantingNothing()
    {
        Guid roleId;
        await using (var work = await Begin())
        {
            roleId = (await Roles.CreateAsync(
                "HRL-DEL", "Delete Me", null, "ACTIVE", ["organization.read"], Unscoped(), work,
                CancellationToken.None)).Id;
            await work.CommitAsync(CancellationToken.None);
        }
        await _fixture.ExecuteAsync($"""
            INSERT INTO federation.access_groups (code, name, role_id, status)
            VALUES ('HRL-DEL-GRP', 'Uses Deleted', '{roleId}', 'ACTIVE');
            """);

        await using (var work = await Begin())
        {
            (await Roles.DeleteAsync(roleId, Unscoped(), work, CancellationToken.None))
                .Error.ShouldBe(RoleWriteError.None);
            await work.CommitAsync(CancellationToken.None);
        }

        (await Roles.GetStatusAsync(roleId, CancellationToken.None)).ShouldBe("INACTIVE");
        (await _fixture.ScalarAsync<string>(
            $"SELECT status FROM federation.access_groups WHERE code = 'HRL-DEL-GRP';"))
            .ShouldBe("ACTIVE");
    }
}
