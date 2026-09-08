using Shouldly;

namespace Trinetra.IntegrationTests;

/// <summary>
/// Verifies the authorization engine against the worked examples in RBAC-LOGICAL-FLOW.md.
/// </summary>
/// <remarks>
/// This is the security boundary of the whole platform. Section 20 is explicit that frontend
/// filtering is not one, and the same is true of a handler that forgets a WHERE clause — so the
/// decision lives in SQL and is tested there.
/// </remarks>
public sealed class AuthorizationTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly PostgresFixture _fixture;

    private static readonly Guid Rahul       = Guid.Parse("d1111111-1111-1111-1111-111111111111");
    private static readonly Guid TransportOu = Guid.Parse("a9999999-9999-9999-9999-999999999999");
    private static readonly Guid SuratId     = Guid.Parse("b9999999-9999-9999-9999-999999999999");
    private static readonly Guid PoliceScope = Guid.Parse("e1111111-1111-1111-1111-111111111111");
    private static readonly Guid AhmScope    = Guid.Parse("e2222222-2222-2222-2222-222222222222");
    private static readonly Guid CamOpsGroup = Guid.Parse("f1111111-1111-1111-1111-111111111111");
    private static readonly Guid ReportingKey = Guid.Parse("c1111111-1111-1111-1111-111111111111");

    // Never a real key. Authentication hashes the presented value and compares; these tests
    // start from an already-authenticated key id and exercise scope resolution only.
    private const string KeyHash = "not-a-real-key-hash";

    public AuthorizationTests(PostgresFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        await _fixture.ResetTargetsAsync();

        await _fixture.ExecuteAsync($"""
            -- Before access_groups: an API key references the group it acts through, so
            -- clearing groups first fails on the foreign key.
            DELETE FROM federation.api_key;
            DELETE FROM federation.user_groups;
            DELETE FROM federation.group_scopes;
            DELETE FROM federation.access_groups;
            DELETE FROM federation.scopes;
            DELETE FROM federation.platform_users;

            -- A second organization, to prove cross-organization denial.
            INSERT INTO federation.organizations (id, code, name, organization_type)
            VALUES ('99999999-9999-9999-9999-999999999999','TRANSPORT','Transport Dept','DEPARTMENT')
            ON CONFLICT (id) DO NOTHING;
            INSERT INTO federation.organization_units
                (id, organization_id, code, name, unit_type)
            VALUES ('{TransportOu}','99999999-9999-9999-9999-999999999999','TR-HQ','Transport HQ','DEPARTMENT')
            ON CONFLICT (id) DO NOTHING;

            -- A second district, to prove geographic denial.
            INSERT INTO federation.geographic_areas (id, code, name, area_type)
            VALUES ('{SuratId}','SUR','Surat','DISTRICT')
            ON CONFLICT (id) DO NOTHING;

            INSERT INTO federation.platform_users
                (id, username, display_name, password_hash, password_salt, password_iterations)
            VALUES ('{Rahul}','rahul','Rahul','\x00','\x00',600000);

            INSERT INTO federation.scopes (id, scope_type, organization_unit_id)
            VALUES ('{PoliceScope}','ORGANIZATION','{PostgresFixture.PoliceUnit}');
            INSERT INTO federation.scopes (id, scope_type, geographic_area_id)
            VALUES ('{AhmScope}','GEOGRAPHY','{PostgresFixture.DistrictId}');

            INSERT INTO federation.access_groups (id, code, name, role_id, status)
            SELECT '{CamOpsGroup}','AHM-POL-CAM','Ahmedabad Police Camera Operators', r.id,'ACTIVE'
            FROM federation.roles r WHERE r.code='CAMERA_OPERATOR';

            INSERT INTO federation.group_scopes VALUES
                ('{CamOpsGroup}','{PoliceScope}'), ('{CamOpsGroup}','{AhmScope}');

            INSERT INTO federation.user_groups (user_id, group_id)
            VALUES ('{Rahul}','{CamOpsGroup}');
            """);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<bool> CanAsync(string permission, Guid? orgUnit, Guid? area) =>
        await _fixture.ScalarAsync<bool>(
            $"SELECT federation.has_permission(p_user_id => '{Rahul}', p_api_key_id => NULL, "
            + $"p_permission => '{permission}', p_organization_unit_id => "
            + $"{(orgUnit is null ? "NULL" : $"'{orgUnit}'")}, p_geographic_area_id => "
            + $"{(area is null ? "NULL" : $"'{area}'")});");

    [Fact]
    public async Task Section10_PoliceCameraInAhmedabad_IsAllowed() =>
        (await CanAsync("camera.read", PostgresFixture.PoliceUnit, PostgresFixture.DistrictId))
            .ShouldBeTrue();

    [Fact]
    public async Task Section11_TransportCameraInAhmedabad_IsDenied()
    {
        // The worked denial in the doc: the permission is held and geography matches, but the
        // camera belongs to another department.
        (await CanAsync("camera.read", TransportOu, PostgresFixture.DistrictId)).ShouldBeFalse();
    }

    [Fact]
    public async Task Section12_CameraSeveralLevelsBelowTheScopedDistrict_IsAllowed()
    {
        // A scope names ONE area; everything beneath is reached by walking the tree. This is why
        // scope rows must never enumerate descendants — a new village would otherwise grant
        // nobody access until every scope row was found and updated.
        (await CanAsync("camera.read", PostgresFixture.PoliceUnit, PostgresFixture.VillageId))
            .ShouldBeTrue();
    }

    [Fact]
    public async Task PoliceCameraInAnotherDistrict_IsDenied() =>
        (await CanAsync("camera.read", PostgresFixture.PoliceUnit, SuratId)).ShouldBeFalse();

    [Fact]
    public async Task ChildOrganizationUnit_IsInsideTheParentScope() =>
        (await CanAsync("camera.read", PostgresFixture.AhmedabadCp, PostgresFixture.DistrictId))
            .ShouldBeTrue();

    [Fact]
    public async Task PermissionTheRoleDoesNotHold_IsDenied() =>
        (await CanAsync("vms.update", PostgresFixture.PoliceUnit, PostgresFixture.DistrictId))
            .ShouldBeFalse();

    [Fact]
    public async Task ResourceWithNoOwner_IsDeniedToAScopedUser()
    {
        // An unowned resource is a data defect. Defaulting to allow would leak it to everyone
        // precisely when the data is least trustworthy.
        (await CanAsync("camera.read", null, PostgresFixture.DistrictId)).ShouldBeFalse();
    }

    [Fact]
    public async Task Section26_ExpiredMembership_GrantsNothing()
    {
        await _fixture.ExecuteAsync(
            $"UPDATE federation.user_groups SET expires_at = now() - interval '1 day' "
            + $"WHERE user_id = '{Rahul}';");

        (await CanAsync("camera.read", PostgresFixture.PoliceUnit, PostgresFixture.DistrictId))
            .ShouldBeFalse("temporary access must lapse without anyone revoking it");
    }

    [Fact]
    public async Task Section25_DisabledGroup_GrantsNothing()
    {
        await _fixture.ExecuteAsync(
            $"UPDATE federation.access_groups SET status='INACTIVE' WHERE id='{CamOpsGroup}';");

        (await CanAsync("camera.read", PostgresFixture.PoliceUnit, PostgresFixture.DistrictId))
            .ShouldBeFalse();
    }

    [Fact]
    public async Task DraftGroup_GrantsNothing()
    {
        // A group can be assembled and reviewed before it confers any access.
        await _fixture.ExecuteAsync(
            $"UPDATE federation.access_groups SET status='DRAFT' WHERE id='{CamOpsGroup}';");

        (await CanAsync("camera.read", PostgresFixture.PoliceUnit, PostgresFixture.DistrictId))
            .ShouldBeFalse();
    }

    [Fact]
    public async Task InactiveUser_GrantsNothing()
    {
        await _fixture.ExecuteAsync(
            $"UPDATE federation.platform_users SET status='INACTIVE' WHERE id='{Rahul}';");

        (await CanAsync("camera.read", PostgresFixture.PoliceUnit, PostgresFixture.DistrictId))
            .ShouldBeFalse();
    }

    [Fact]
    public async Task Section13_SecondGroupAddsItsOwnPermissions_WithinItsOwnScope()
    {
        // A user gains VMS access over Transport through a second group, WITHOUT that group's
        // scope leaking into the first group's camera permission.
        await _fixture.ExecuteAsync($"""
            INSERT INTO federation.scopes (id, scope_type, organization_unit_id)
            VALUES ('e3333333-3333-3333-3333-333333333333','ORGANIZATION','{TransportOu}');

            INSERT INTO federation.access_groups (id, code, name, role_id, status)
            SELECT 'f3333333-3333-3333-3333-333333333333','TR-VMS','Transport VMS Team', r.id,'ACTIVE'
            FROM federation.roles r WHERE r.code='VMS_OPERATOR';

            INSERT INTO federation.group_scopes
            VALUES ('f3333333-3333-3333-3333-333333333333','e3333333-3333-3333-3333-333333333333');

            INSERT INTO federation.user_groups (user_id, group_id)
            VALUES ('{Rahul}','f3333333-3333-3333-3333-333333333333');
            """);

        // Granted by the new group, inside its scope.
        (await CanAsync("vms.read", TransportOu, null)).ShouldBeTrue();

        // NOT granted. video.read belongs to CAMERA_OPERATOR (the Police group) and not to
        // VMS_OPERATOR, so it must not reach Transport. If scopes were unioned across groups
        // rather than evaluated per group, this would pass and the user would silently gain
        // video access to another department's cameras.
        //
        // Deliberately not asserted with camera.read: VMS_OPERATOR holds that too, so it would
        // pass for the wrong reason and prove nothing about scope isolation.
        (await CanAsync("video.read", TransportOu, PostgresFixture.DistrictId)).ShouldBeFalse();

        // And the permission still works where the first group's scope does reach.
        (await CanAsync("video.read", PostgresFixture.PoliceUnit, PostgresFixture.DistrictId))
            .ShouldBeTrue();
    }

    [Fact]
    public async Task AuthorizedOrgUnits_ReturnsTheWholeSubtree()
    {
        // What list endpoints join against, so the database returns only authorized rows.
        var count = await _fixture.ScalarAsync<long>(
            $"SELECT count(*) FROM federation.authorized_org_units("
            + $"p_user_id => '{Rahul}', p_api_key_id => NULL, p_permission => 'camera.read');");

        count.ShouldBe(2, "the scoped unit and its child");
    }

    // ---- API keys as principals ------------------------------------------
    //
    // A key acts through an access group exactly as a user does. Before scope resolution took a
    // principal rather than a user id, none of this held: every predicate was keyed on a user
    // id, an API key has none, so a scoped key resolved to the empty set and saw NOTHING while
    // an unscoped key skipped the predicate and saw EVERYTHING. The only key that worked was an
    // estate-wide one, which is exactly the key nobody should be issuing. These tests exist so a
    // future refactor of the scope engine cannot quietly reopen that.

    /// <summary>Creates a key acting through the Police + Ahmedabad group.</summary>
    private Task SeedScopedKeyAsync() => _fixture.ExecuteAsync($"""
        INSERT INTO federation.api_key (id, key_id, key_hash, display_name, group_id)
        VALUES ('{ReportingKey}','reporting-job','{KeyHash}','Reporting job','{CamOpsGroup}');
        """);

    private async Task<bool> KeyCanAsync(string permission, Guid? orgUnit, Guid? area) =>
        await _fixture.ScalarAsync<bool>(
            $"SELECT federation.has_permission(p_user_id => NULL, p_api_key_id => '{ReportingKey}', "
            + $"p_permission => '{permission}', p_organization_unit_id => "
            + $"{(orgUnit is null ? "NULL" : $"'{orgUnit}'")}, p_geographic_area_id => "
            + $"{(area is null ? "NULL" : $"'{area}'")});");

    private Task<long> KeyUnscopedCountAsync() => _fixture.ScalarAsync<long>(
        $"SELECT count(*) FROM federation.unscoped_permissions("
        + $"p_user_id => NULL, p_api_key_id => '{ReportingKey}');");

    private Task<long> KeyReachAsync(string permission) => _fixture.ScalarAsync<long>(
        $"SELECT count(*) FROM federation.authorized_org_units("
        + $"p_user_id => NULL, p_api_key_id => '{ReportingKey}', p_permission => '{permission}');");

    [Fact]
    public async Task ScopedApiKey_ReachesItsOwnDepartmentAndNoOther()
    {
        await SeedScopedKeyAsync();

        (await KeyCanAsync("camera.read", PostgresFixture.PoliceUnit, PostgresFixture.DistrictId))
            .ShouldBeTrue("the key's group is scoped to exactly this department and district");

        (await KeyCanAsync("camera.read", TransportOu, PostgresFixture.DistrictId))
            .ShouldBeFalse("a key must not read another department, section 11 applies to "
                         + "machines exactly as it does to people");
    }

    [Fact]
    public async Task ScopedApiKey_ReturnsItsSubtree_NotAnEmptySet()
    {
        // The regression itself. This returned 0 before the fix — not because the key was
        // unauthorised, but because the predicate was asking about a user that did not exist.
        await SeedScopedKeyAsync();

        (await KeyReachAsync("camera.read")).ShouldBe(2, "the scoped unit and its child");
    }

    [Fact]
    public async Task ScopedApiKey_HoldsNoUnscopedPermission()
    {
        // The other half of the defect: with no scope of its own resolvable, the only way a key
        // could see anything was the unscoped branch, which sees the entire estate.
        await SeedScopedKeyAsync();

        (await KeyUnscopedCountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task UnscopedApiKey_IsUnscopedPerPermission_NotWholesale()
    {
        // A group declaring no organization scope reaches everywhere — but the answer is still
        // computed per permission. The key path previously used one flag for the key as a whole,
        // the same defect already fixed for users.
        await _fixture.ExecuteAsync($"""
            DELETE FROM federation.group_scopes WHERE group_id = '{CamOpsGroup}';
            """);
        await SeedScopedKeyAsync();

        (await KeyUnscopedCountAsync()).ShouldBeGreaterThan(0);
        (await KeyCanAsync("camera.read", TransportOu, SuratId))
            .ShouldBeTrue("an unscoped group is unrestricted by design");

        // Still bounded by the ROLE. Unscoped means no scope limit, never every permission.
        (await KeyCanAsync("vms.update", PostgresFixture.PoliceUnit, null))
            .ShouldBeFalse("CAMERA_OPERATOR does not hold vms.update at any scope");
    }

    [Fact]
    public async Task RevokedApiKey_GrantsNothing()
    {
        await SeedScopedKeyAsync();
        await _fixture.ExecuteAsync(
            $"UPDATE federation.api_key SET revoked_at = now() WHERE id = '{ReportingKey}';");

        (await KeyReachAsync("camera.read")).ShouldBe(0);
        (await KeyCanAsync("camera.read", PostgresFixture.PoliceUnit, PostgresFixture.DistrictId))
            .ShouldBeFalse("revocation is re-checked inside the scope functions, so a key "
                         + "revoked mid-request cannot finish the request it started");
    }

    [Fact]
    public async Task ExpiredApiKey_GrantsNothing()
    {
        await SeedScopedKeyAsync();
        await _fixture.ExecuteAsync(
            $"UPDATE federation.api_key SET expires_at = now() - interval '1 hour' "
            + $"WHERE id = '{ReportingKey}';");

        (await KeyCanAsync("camera.read", PostgresFixture.PoliceUnit, PostgresFixture.DistrictId))
            .ShouldBeFalse();
    }

    [Fact]
    public async Task ApiKeyThroughADisabledGroup_GrantsNothing()
    {
        await SeedScopedKeyAsync();
        await _fixture.ExecuteAsync(
            $"UPDATE federation.access_groups SET status='INACTIVE' WHERE id='{CamOpsGroup}';");

        (await KeyCanAsync("camera.read", PostgresFixture.PoliceUnit, PostgresFixture.DistrictId))
            .ShouldBeFalse();
    }

    [Fact]
    public async Task AmbiguousPrincipal_ResolvesToNothing()
    {
        // Both identities at once is a programming error. It must DENY, never broaden: a union
        // of the two would hand the caller the sum of a user's scopes and a key's, which is the
        // one outcome an authorization function must never produce.
        await SeedScopedKeyAsync();

        var groups = await _fixture.ScalarAsync<long>(
            $"SELECT count(*) FROM federation.principal_groups("
            + $"p_user_id => '{Rahul}', p_api_key_id => '{ReportingKey}');");

        groups.ShouldBe(0);

        (await _fixture.ScalarAsync<bool>(
            $"SELECT federation.has_permission(p_user_id => '{Rahul}', "
            + $"p_api_key_id => '{ReportingKey}', p_permission => 'camera.read', "
            + $"p_organization_unit_id => '{PostgresFixture.PoliceUnit}');"))
            .ShouldBeFalse("neither principal's access survives the ambiguity");
    }

    [Fact]
    public async Task UnknownPrincipal_GrantsNothing()
    {
        // Both ids null is what a malformed or absent subject produces. It must resolve to
        // nothing rather than to a wildcard.
        (await _fixture.ScalarAsync<long>(
            "SELECT count(*) FROM federation.principal_groups("
            + "p_user_id => NULL, p_api_key_id => NULL);"))
            .ShouldBe(0);
    }
}
