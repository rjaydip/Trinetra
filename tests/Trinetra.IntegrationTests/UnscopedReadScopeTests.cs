using Shouldly;
using Trinetra.Federation.Api.Auth;
using Trinetra.Federation.Storage;
using Trinetra.Federation.Storage.Repositories;

namespace Trinetra.IntegrationTests;

/// <summary>
/// PR5: the user, access-group and API-key read surfaces are scoped to what the caller may
/// administer — the fix for findings 6-H1, 8-H1 and 8-H2. Covers
/// <see cref="UserRepository.ListAsync"/>, <see cref="AccessGroupRepository.ListAsync"/> /
/// <see cref="AccessGroupRepository.IsVisibleToAsync"/> /
/// <see cref="AccessGroupRepository.ListMembersAsync"/>, and
/// <see cref="ApiKeyRepository.ListAsync"/> / <see cref="ApiKeyRepository.RevokeAsync"/>.
/// </summary>
/// <remarks>
/// One scoped caller, in a bespoke <c>READ_SCOPE_TEST</c> group scoped to
/// <c>PostgresFixture.PoliceUnit</c> organizationally and <c>PostgresFixture.DistrictId</c>
/// geographically — so both <c>IsUnscopedFor</c> and <c>IsUnscopedForGeography</c> are false and
/// every filter actually runs. Reachability is resolved entirely from the DB seed, exactly as it
/// would be for a real logged-in user. An <c>UnscopedCaller</c> control (<c>IsSystem</c>) proves
/// the fast path still returns everything — that account is the one the platform bootstraps from.
/// </remarks>
public sealed class UnscopedReadScopeTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly PostgresFixture _fixture;

    private static readonly Guid Caller          = Guid.Parse("d7777777-7777-7777-7777-777777777777");
    private static readonly Guid CallerGroup     = Guid.Parse("f7777777-7777-7777-7777-777777777777");
    private static readonly Guid CallerOrgScope  = Guid.Parse("e7000000-0000-0000-0000-000000000001");
    private static readonly Guid CallerGeoScope  = Guid.Parse("e7000000-0000-0000-0000-000000000002");

    private static readonly Guid GroupInReach      = Guid.Parse("f7000000-0000-0000-0000-00000000a001");
    private static readonly Guid GroupInReachBroad = Guid.Parse("f7000000-0000-0000-0000-00000000a009");
    private static readonly Guid GroupOutOfReach   = Guid.Parse("f7000000-0000-0000-0000-00000000a003");
    private static readonly Guid GroupEstateWide  = Guid.Parse("f7000000-0000-0000-0000-00000000a005");
    private static readonly Guid GroupGeoOutOfReach = Guid.Parse("f7000000-0000-0000-0000-00000000a007");

    private static readonly Guid OtherUnit     = Guid.Parse("a7000000-0000-0000-0000-00000000c001");
    private static readonly Guid OtherDistrict = Guid.Parse("b7000000-0000-0000-0000-00000000c002");

    private static readonly Guid InReachOrgScope     = Guid.Parse("e7000000-0000-0000-0000-00000000a001");
    private static readonly Guid InReachGeoScope     = Guid.Parse("e7000000-0000-0000-0000-00000000a002");
    private static readonly Guid OutOfReachOrgScope  = Guid.Parse("e7000000-0000-0000-0000-00000000a003");
    private static readonly Guid OutOfReachGeoScope  = Guid.Parse("e7000000-0000-0000-0000-00000000a004");
    private static readonly Guid GeoOutReachOrgScope = Guid.Parse("e7000000-0000-0000-0000-00000000a007");
    private static readonly Guid GeoOutReachGeoScope = Guid.Parse("e7000000-0000-0000-0000-00000000a008");
    private static readonly Guid InReachBroadOrgScope = Guid.Parse("e7000000-0000-0000-0000-00000000a009");
    private static readonly Guid InReachBroadGeoScope = Guid.Parse("e7000000-0000-0000-0000-00000000a00a");

    private static readonly Guid UserInReach     = Guid.Parse("d7000000-0000-0000-0000-00000000b001");
    private static readonly Guid UserOutOfReach   = Guid.Parse("d7000000-0000-0000-0000-00000000b002");
    private static readonly Guid UserSystem       = Guid.Parse("d7000000-0000-0000-0000-00000000b003");
    private static readonly Guid UserEstateWide   = Guid.Parse("d7000000-0000-0000-0000-00000000b004");
    private static readonly Guid UserNoGroups     = Guid.Parse("d7000000-0000-0000-0000-00000000b005");

    // In the caller's org reach, but holds a permission the caller lacks. The list shows it
    // (visibility is org-reach only); the single-GET guard must ALSO show it — read visibility
    // does not apply the permission-subset check. If the guard regressed to full
    // CanAdministerAsync this user would 404 on its detail route while appearing in the list.
    private static readonly Guid UserInReachHigherPriv = Guid.Parse("d7000000-0000-0000-0000-00000000b006");

    private static readonly Guid KeyInReach       = Guid.Parse("c7000000-0000-4000-8000-00000000d001");
    private static readonly Guid KeyOutOfReach     = Guid.Parse("c7000000-0000-4000-8000-00000000d002");
    private static readonly Guid KeyEstateWide     = Guid.Parse("c7000000-0000-4000-8000-00000000d003");

    public UnscopedReadScopeTests(PostgresFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        await _fixture.ResetTargetsAsync();

        await _fixture.ExecuteAsync($"""
            DELETE FROM federation.api_key;
            DELETE FROM federation.user_groups;
            DELETE FROM federation.group_scopes;
            DELETE FROM federation.access_groups;
            DELETE FROM federation.scopes;
            DELETE FROM federation.role_permissions WHERE role_id IN (
                SELECT id FROM federation.roles WHERE code = 'READ_SCOPE_TEST');
            DELETE FROM federation.roles WHERE code = 'READ_SCOPE_TEST';
            DELETE FROM federation.organization_units WHERE id = '{OtherUnit}';
            DELETE FROM federation.geographic_areas WHERE id = '{OtherDistrict}';
            DELETE FROM federation.role_permissions WHERE role_id IN (
                SELECT id FROM federation.roles WHERE code = 'READ_SCOPE_TEST_BROAD');
            DELETE FROM federation.roles WHERE code = 'READ_SCOPE_TEST_BROAD';
            DELETE FROM federation.platform_users
            WHERE id IN ('{Caller}', '{UserInReach}', '{UserOutOfReach}', '{UserSystem}',
                         '{UserEstateWide}', '{UserNoGroups}', '{UserInReachHigherPriv}');

            -- A unit the caller is NOT scoped to: a root unit in the same organization, so it is
            -- a sibling of PoliceUnit and absent from authorized_org_units(PoliceUnit).
            INSERT INTO federation.organization_units
                (id, organization_id, parent_unit_id, code, name, unit_type)
            VALUES ('{OtherUnit}', '{PostgresFixture.OrgId}', NULL, 'RS-OTHER-UNIT',
                    'RS Other Unit', 'DEPARTMENT');

            -- A district the caller is NOT scoped to — for the "org in reach, geo out of reach"
            -- group case (proves GroupVisiblePredicate ANDs the two dimensions).
            INSERT INTO federation.geographic_areas (id, parent_area_id, code, name, area_type)
            VALUES ('{OtherDistrict}', NULL, 'RS-OTH-DIST', 'RS Other District', 'DISTRICT');

            -- A bespoke role carrying exactly the read/manage permissions PR5's filters key on.
            INSERT INTO federation.roles (code, name, description, is_system, status)
            VALUES ('READ_SCOPE_TEST', 'Read Scope Test', 'Integration-test-only role', FALSE, 'ACTIVE');
            INSERT INTO federation.role_permissions (role_id, permission_code)
            SELECT r.id, p.code FROM federation.roles r, unnest(ARRAY[
                'user.read', 'group.read', 'apikey.read', 'apikey.manage'
            ]) AS p(code)
            WHERE r.code = 'READ_SCOPE_TEST';

            -- A role holding a permission the caller's role does not (camera.delete).
            INSERT INTO federation.roles (code, name, description, is_system, status)
            VALUES ('READ_SCOPE_TEST_BROAD', 'Read Scope Test Broad', 'Integration-test-only', FALSE, 'ACTIVE');
            INSERT INTO federation.role_permissions (role_id, permission_code)
            SELECT r.id, 'camera.delete' FROM federation.roles r WHERE r.code = 'READ_SCOPE_TEST_BROAD';

            -- Users.
            INSERT INTO federation.platform_users
                (id, username, display_name, password_hash, password_salt, password_iterations, is_system)
            VALUES
                ('{Caller}',        'readscope',  'Read Scope Caller', '\x00', '\x00', 600000, FALSE),
                ('{UserInReach}',   'in-reach',   'In Reach',          '\x00', '\x00', 600000, FALSE),
                ('{UserOutOfReach}','out-reach',  'Out Of Reach',      '\x00', '\x00', 600000, FALSE),
                ('{UserSystem}',    'seed-acct',  'Seed Account',      '\x00', '\x00', 600000, TRUE),
                ('{UserEstateWide}','estate',     'Estate Wide',       '\x00', '\x00', 600000, FALSE),
                ('{UserNoGroups}',  'no-groups',  'No Groups',         '\x00', '\x00', 600000, FALSE),
                ('{UserInReachHigherPriv}', 'higher-priv', 'Higher Priv', '\x00', '\x00', 600000, FALSE);

            -- Scope rows. The caller reaches PoliceUnit (+ descendants) and DistrictId.
            -- GroupOutOfReach is scoped to a sibling unit the caller does not administer.
            INSERT INTO federation.scopes (id, scope_type, organization_unit_id) VALUES
                ('{CallerOrgScope}',     'ORGANIZATION', '{PostgresFixture.PoliceUnit}'),
                ('{InReachOrgScope}',    'ORGANIZATION', '{PostgresFixture.PoliceUnit}'),
                ('{OutOfReachOrgScope}', 'ORGANIZATION', '{OtherUnit}'),
                ('{GeoOutReachOrgScope}','ORGANIZATION', '{PostgresFixture.PoliceUnit}');
            INSERT INTO federation.scopes (id, scope_type, geographic_area_id) VALUES
                ('{CallerGeoScope}',     'GEOGRAPHY', '{PostgresFixture.DistrictId}'),
                ('{InReachGeoScope}',    'GEOGRAPHY', '{PostgresFixture.DistrictId}'),
                ('{OutOfReachGeoScope}', 'GEOGRAPHY', '{PostgresFixture.DistrictId}'),
                ('{GeoOutReachGeoScope}','GEOGRAPHY', '{OtherDistrict}'),
                ('{InReachBroadGeoScope}','GEOGRAPHY', '{PostgresFixture.DistrictId}');
            INSERT INTO federation.scopes (id, scope_type, organization_unit_id) VALUES
                ('{InReachBroadOrgScope}','ORGANIZATION', '{PostgresFixture.PoliceUnit}');

            -- Groups. All ACTIVE. Role does not matter for visibility (scope reach does), so all
            -- reuse READ_SCOPE_TEST.
            INSERT INTO federation.access_groups (id, code, name, role_id, status)
            SELECT g.id, g.code, g.code, r.id, 'ACTIVE'
            FROM federation.roles r, (VALUES
                ('{CallerGroup}'::uuid,         'RS-CALLER'),
                ('{GroupInReach}'::uuid,        'RS-IN-REACH'),
                ('{GroupOutOfReach}'::uuid,     'RS-OUT-OF-REACH'),
                ('{GroupGeoOutOfReach}'::uuid,  'RS-GEO-OUT-OF-REACH'),
                ('{GroupEstateWide}'::uuid,     'RS-ESTATE-WIDE')
            ) AS g(id, code)
            WHERE r.code = 'READ_SCOPE_TEST';

            -- One more in-reach group, on the broader role, for UserInReachHigherPriv.
            INSERT INTO federation.access_groups (id, code, name, role_id, status)
            SELECT '{GroupInReachBroad}', 'RS-IN-REACH-BROAD', 'RS-IN-REACH-BROAD', r.id, 'ACTIVE'
            FROM federation.roles r WHERE r.code = 'READ_SCOPE_TEST_BROAD';

            INSERT INTO federation.group_scopes (group_id, scope_id) VALUES
                ('{CallerGroup}',        '{CallerOrgScope}'),
                ('{CallerGroup}',        '{CallerGeoScope}'),
                ('{GroupInReach}',       '{InReachOrgScope}'),
                ('{GroupInReach}',       '{InReachGeoScope}'),
                ('{GroupInReachBroad}',  '{InReachBroadOrgScope}'),
                ('{GroupInReachBroad}',  '{InReachBroadGeoScope}'),
                ('{GroupOutOfReach}',    '{OutOfReachOrgScope}'),
                ('{GroupOutOfReach}',    '{OutOfReachGeoScope}'),
                -- org scope IS in the caller's reach; geo scope is NOT → group still hidden.
                ('{GroupGeoOutOfReach}', '{GeoOutReachOrgScope}'),
                ('{GroupGeoOutOfReach}', '{GeoOutReachGeoScope}');
            -- GroupEstateWide: deliberately no scope rows.

            -- Memberships.
            INSERT INTO federation.user_groups (user_id, group_id) VALUES
                ('{Caller}',         '{CallerGroup}'),
                ('{UserInReach}',    '{GroupInReach}'),
                ('{UserOutOfReach}', '{GroupOutOfReach}'),
                ('{UserOutOfReach}', '{GroupInReach}'),
                ('{UserEstateWide}', '{GroupEstateWide}'),
                ('{UserInReachHigherPriv}', '{GroupInReachBroad}');
            -- UserSystem, UserNoGroups: no memberships.

            -- API keys, one per group.
            INSERT INTO federation.api_key (id, key_id, key_hash, display_name, group_id) VALUES
                ('{KeyInReach}',   'ak_in',     'h1', 'In-reach key',    '{GroupInReach}'),
                ('{KeyOutOfReach}','ak_out',    'h2', 'Out-of-reach key', '{GroupOutOfReach}'),
                ('{KeyEstateWide}','ak_estate', 'h3', 'Estate-wide key',  '{GroupEstateWide}');
            """);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static CallerContext ScopedCaller() => new()
    {
        UserId = Caller,
        Actor = "readscope",
        Permissions = new HashSet<string>(StringComparer.Ordinal)
        {
            "user.read", "group.read", "apikey.read", "apikey.manage",
        },
    };

    private static CallerContext UnscopedCaller() => new()
    {
        UserId = Caller,
        Actor = "readscope",
        Permissions = new HashSet<string>(StringComparer.Ordinal)
        {
            "user.read", "group.read", "apikey.read", "apikey.manage",
        },
        IsSystem = true,
    };

    private UserRepository Users => new(_fixture.DataSource);
    private AccessGroupRepository Groups => new(_fixture.DataSource);
    private ApiKeyRepository Keys => new(_fixture.DataSource);

    // ---- Pagination (PR9: 6-M6 / 8-M5) -----------------------------------

    [Fact]
    public async Task Users_ListPage_Paginates_AndReportsTheFullCountAtEachPage()
    {
        var unscoped = UnscopedCaller();
        var all = (await Users.ListAsync(unscoped, CancellationToken.None)).Select(u => u.Id).ToList();
        all.Count.ShouldBeGreaterThan(3, "the fixture seeds several users");

        var page1 = await Users.ListPageAsync(unscoped, new PageWindow(2, 0), CancellationToken.None);
        var page2 = await Users.ListPageAsync(unscoped, new PageWindow(2, 2), CancellationToken.None);

        page1.Total.ShouldBe(all.Count);
        page2.Total.ShouldBe(all.Count);
        page1.Items.Count.ShouldBe(2);
        page1.Items.Select(u => u.Id).ShouldBe(all.Take(2));            // ORDER BY username, id
        page2.Items.Select(u => u.Id).ShouldBe(all.Skip(2).Take(2));
    }

    [Fact]
    public async Task Users_ListPage_HardCap_ReportsTotalGreaterThanReturned()
    {
        var unscoped = UnscopedCaller();
        var all = await Users.ListAsync(unscoped, CancellationToken.None);

        // A cap of 1 stands in for the endpoint's real cap: Total still reflects everything, so
        // the endpoint can set X-Result-Capped.
        var capped = await Users.ListPageAsync(unscoped, new PageWindow(1, 0), CancellationToken.None);

        capped.Items.Count.ShouldBe(1);
        capped.Total.ShouldBe(all.Count);
        capped.Total.ShouldBeGreaterThan(capped.Items.Count);
    }

    // ---- Users (6-H1) -------------------------------------------------------

    [Fact]
    public async Task Users_ScopedCaller_SeesOnlyAdministrableAccounts()
    {
        var ids = (await Users.ListAsync(ScopedCaller(), CancellationToken.None))
            .Select(u => u.Id).ToHashSet();

        ids.ShouldContain(UserInReach);
        ids.ShouldContain(UserNoGroups, "a groupless account grants nothing and is administrable");
        ids.ShouldNotContain(UserOutOfReach, "reaches an organization unit the caller does not");
        ids.ShouldNotContain(UserSystem, "the seed account is never shown to a scoped caller");
        ids.ShouldNotContain(UserEstateWide, "held through an organization-unrestricted group");
    }

    [Fact]
    public async Task Users_UnscopedCaller_SeesEveryAccount()
    {
        var ids = (await Users.ListAsync(UnscopedCaller(), CancellationToken.None))
            .Select(u => u.Id).ToHashSet();

        ids.ShouldContain(UserInReach);
        ids.ShouldContain(UserOutOfReach);
        ids.ShouldContain(UserSystem);
        ids.ShouldContain(UserEstateWide);
        ids.ShouldContain(UserNoGroups);
    }

    // ---- Access groups & members (6-H1) -----------------------------------

    [Fact]
    public async Task Groups_ScopedCaller_SeesOnlyGroupsWithinReach()
    {
        var codes = (await Groups.ListAsync(ScopedCaller(), CancellationToken.None))
            .Select(g => g.Code).ToHashSet();

        codes.ShouldContain("RS-IN-REACH");
        codes.ShouldContain("RS-CALLER");
        codes.ShouldNotContain("RS-OUT-OF-REACH", "organization scope outside the caller's reach");
        codes.ShouldNotContain(
            "RS-GEO-OUT-OF-REACH",
            "org scope IS in reach but geo scope is not — the two dimensions are ANDed");
        codes.ShouldNotContain("RS-ESTATE-WIDE", "no scope at all — the PLATFORM-ADMINS case");
    }

    [Fact]
    public async Task Groups_UnscopedCaller_SeesEveryGroup()
    {
        var codes = (await Groups.ListAsync(UnscopedCaller(), CancellationToken.None))
            .Select(g => g.Code).ToHashSet();

        codes.ShouldContain("RS-OUT-OF-REACH");
        codes.ShouldContain("RS-ESTATE-WIDE");
    }

    [Fact]
    public async Task Group_IsVisibleTo_TracksReach()
    {
        var scoped = ScopedCaller();
        (await Groups.IsVisibleToAsync(GroupInReach, scoped, "group.read", CancellationToken.None))
            .ShouldBeTrue();
        (await Groups.IsVisibleToAsync(GroupOutOfReach, scoped, "group.read", CancellationToken.None))
            .ShouldBeFalse();
        (await Groups.IsVisibleToAsync(GroupGeoOutOfReach, scoped, "group.read", CancellationToken.None))
            .ShouldBeFalse("org in reach, geo out of reach — hidden by the ANDed dimensions");
        (await Groups.IsVisibleToAsync(GroupEstateWide, scoped, "group.read", CancellationToken.None))
            .ShouldBeFalse();
    }

    [Fact]
    public async Task GroupMembers_ScopedCaller_SeesOnlyMembersItCouldSeeAsUsers()
    {
        // GroupInReach is visible to the caller; UserOutOfReach is one of its members but is out
        // of reach via its other group, so it must not appear in the roster.
        var members = (await Groups.ListMembersAsync(GroupInReach, ScopedCaller(), CancellationToken.None))
            .Select(m => m.UserId).ToHashSet();

        members.ShouldContain(UserInReach);
        members.ShouldNotContain(UserOutOfReach);
    }

    [Fact]
    public async Task GroupMembers_ListPage_NonEmptyRoster_Maps()
    {
        // Regression: the paged member query's splitOn was wrong and threw on any non-empty
        // roster — the suite stayed green only because every fixture group had an empty page.
        var page = await Groups.ListMembersPageAsync(
            GroupInReach, UnscopedCaller(), new PageWindow(10, 0), CancellationToken.None);

        page.Total.ShouldBeGreaterThan(0);
        page.Items.Count.ShouldBe(page.Total);
        page.Items.Select(m => m.UserId).ShouldContain(UserInReach);
        page.Items.ShouldAllBe(m => !string.IsNullOrEmpty(m.Username));
    }

    [Fact]
    public async Task GroupMembers_UnscopedCaller_SeesEveryMember()
    {
        var members = (await Groups.ListMembersAsync(GroupInReach, UnscopedCaller(), CancellationToken.None))
            .Select(m => m.UserId).ToHashSet();

        members.ShouldContain(UserInReach);
        members.ShouldContain(UserOutOfReach);
    }

    // ---- API keys (8-H1, 8-H2) -------------------------------------------

    [Fact]
    public async Task ApiKeys_ScopedCaller_ListsOnlyKeysWithinReach()
    {
        var ids = (await Keys.ListAsync(ScopedCaller(), CancellationToken.None))
            .Select(k => k.Id).ToHashSet();

        ids.ShouldContain(KeyInReach);
        ids.ShouldNotContain(KeyOutOfReach);
        ids.ShouldNotContain(KeyEstateWide, "bound to a group with no scope — the AI-worker-key case");
    }

    [Fact]
    public async Task ApiKeys_UnscopedCaller_ListsEveryKey()
    {
        var ids = (await Keys.ListAsync(UnscopedCaller(), CancellationToken.None))
            .Select(k => k.Id).ToHashSet();

        ids.ShouldContain(KeyInReach);
        ids.ShouldContain(KeyOutOfReach);
        ids.ShouldContain(KeyEstateWide);
    }

    [Fact]
    public async Task ApiKeys_ScopedCaller_CannotRevokeKeyOutOfReach_And404MatchesUnknown()
    {
        await using var work = await UnitOfWork.BeginAsync(_fixture.DataSource, CancellationToken.None);

        var result = await ApiKeyRepository.RevokeAsync(
            KeyOutOfReach, ScopedCaller(), work, CancellationToken.None);

        result.Outcome.ShouldBe(RevokeOutcome.NotFound);
        await work.CommitAsync(CancellationToken.None);

        var stillLive = await _fixture.ScalarAsync<bool>(
            $"SELECT revoked_at IS NULL FROM federation.api_key WHERE id = '{KeyOutOfReach}';");
        stillLive.ShouldBeTrue("an unreachable key must be left completely untouched");
    }

    [Fact]
    public async Task ApiKeys_ScopedCaller_CannotRevokeEstateWideKey()
    {
        await using var work = await UnitOfWork.BeginAsync(_fixture.DataSource, CancellationToken.None);

        var result = await ApiKeyRepository.RevokeAsync(
            KeyEstateWide, ScopedCaller(), work, CancellationToken.None);

        result.Outcome.ShouldBe(RevokeOutcome.NotFound);
    }

    [Fact]
    public async Task ApiKeys_ScopedCaller_RevokesKeyWithinReach()
    {
        await using var work = await UnitOfWork.BeginAsync(_fixture.DataSource, CancellationToken.None);

        var result = await ApiKeyRepository.RevokeAsync(
            KeyInReach, ScopedCaller(), work, CancellationToken.None);

        result.Outcome.ShouldBe(RevokeOutcome.Revoked);
        await work.CommitAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ApiKeys_ListAndRevoke_UseTheSamePredicate()
    {
        // A key absent from the scoped list must also be unrevocable by the same caller — or
        // 8-H1 is reachable by revoking what you cannot see.
        var listed = (await Keys.ListAsync(ScopedCaller(), CancellationToken.None))
            .Select(k => k.Id).ToHashSet();
        listed.ShouldNotContain(KeyOutOfReach);

        await using var work = await UnitOfWork.BeginAsync(_fixture.DataSource, CancellationToken.None);
        (await ApiKeyRepository.RevokeAsync(KeyOutOfReach, ScopedCaller(), work, CancellationToken.None))
            .Outcome.ShouldBe(RevokeOutcome.NotFound);
    }

    // ---- Single-record read guard (6-H1, decision D1) --------------------
    // No WebApplicationFactory in this suite (same accepted gap as TokenRevocationTests); the
    // guard UserAuthorityGuard.RefuseAsync and its repo predicate CanReadAsync are exercised
    // directly. These are what GET /users/{id}, /{id}/groups, /{id}/permissions call.

    [Fact]
    public async Task UserRead_Guard_404sForOutOfReachAndSystem_PassesForInReach()
    {
        var scoped = ScopedCaller();

        (await UserAuthorityGuard.RefuseAsync(UserInReach, scoped, "user.read", Users, CancellationToken.None))
            .ShouldBeNull("in reach — the endpoint proceeds");

        (await UserAuthorityGuard.RefuseAsync(UserNoGroups, scoped, "user.read", Users, CancellationToken.None))
            .ShouldBeNull("a groupless account is administrable");

        (await UserAuthorityGuard.RefuseAsync(UserOutOfReach, scoped, "user.read", Users, CancellationToken.None))
            .ShouldNotBeNull("reaches a unit the caller does not — 404");

        (await UserAuthorityGuard.RefuseAsync(UserSystem, scoped, "user.read", Users, CancellationToken.None))
            .ShouldNotBeNull("the seed account is never visible to a scoped caller — 404");

        (await UserAuthorityGuard.RefuseAsync(UserEstateWide, scoped, "user.read", Users, CancellationToken.None))
            .ShouldNotBeNull("held through an estate-wide group — 404");
    }

    [Fact]
    public async Task UserRead_Guard_UnscopedCaller_PassesForEveryTargetIncludingSystem()
    {
        var unscoped = UnscopedCaller();

        foreach (var id in new[] { UserInReach, UserOutOfReach, UserSystem, UserEstateWide, UserNoGroups })
        {
            (await UserAuthorityGuard.RefuseAsync(id, unscoped, "user.read", Users, CancellationToken.None))
                .ShouldBeNull($"unscoped caller sees {id}");
        }
    }

    [Fact]
    public async Task UserRead_ListAndSingleGet_Agree_NoListedRow404sOnItsDetail()
    {
        // The list-vs-single-GET consistency the reviewers flagged: every account the scoped
        // caller can list must also pass the single-record guard, and vice versa.
        var scoped = ScopedCaller();
        var listed = (await Users.ListAsync(scoped, CancellationToken.None)).Select(u => u.Id).ToHashSet();

        foreach (var id in new[]
        {
            UserInReach, UserOutOfReach, UserSystem, UserEstateWide, UserNoGroups,
            UserInReachHigherPriv,
        })
        {
            var guardRefused =
                await UserAuthorityGuard.RefuseAsync(id, scoped, "user.read", Users, CancellationToken.None)
                is not null;
            listed.Contains(id).ShouldBe(!guardRefused,
                $"list membership and single-GET visibility must agree for {id}");
        }

        // The specific reviewer concern: this account is in the caller's org reach but holds
        // camera.delete, which the caller lacks. It must be in the list AND pass the guard.
        listed.ShouldContain(UserInReachHigherPriv);
        (await UserAuthorityGuard.RefuseAsync(
            UserInReachHigherPriv, scoped, "user.read", Users, CancellationToken.None))
            .ShouldBeNull("read visibility is org-reach only, not a permission-subset check");
    }

    [Fact]
    public async Task Group_SingleGet_Guard_404sForOutOfReach()
    {
        var scoped = ScopedCaller();

        (await Groups.IsVisibleToAsync(GroupInReach, scoped, "group.read", CancellationToken.None))
            .ShouldBeTrue();
        (await Groups.IsVisibleToAsync(GroupOutOfReach, scoped, "group.read", CancellationToken.None))
            .ShouldBeFalse("→ endpoint returns 404");
        (await Groups.IsVisibleToAsync(GroupEstateWide, scoped, "group.read", CancellationToken.None))
            .ShouldBeFalse("→ endpoint returns 404, the PLATFORM-ADMINS case");
    }
}
