using Shouldly;
using Trinetra.Federation.Core.Model;
using Trinetra.Federation.Storage;
using Trinetra.Federation.Storage.Repositories;

namespace Trinetra.IntegrationTests;

/// <summary>
/// Verifies that <see cref="ConnectorTargetRepository"/>, <see cref="FederationQueryRepository"/>,
/// <see cref="DetectionRepository"/> and <see cref="EventQueryRepository"/> enforce the
/// GEOGRAPHY scope dimension, not only organization (invariant 12) — the fix for findings 9-H1,
/// 13-H1 (transitively), 15-M1 and 14-M1.
/// </summary>
/// <remarks>
/// One caller (<c>TestUser</c>) is scoped to <c>PoliceUnit</c> organizationally and
/// <c>PostgresFixture.DistrictId</c> geographically, via a bespoke role carrying exactly the
/// permissions these repositories check. Two targets exist: one in-district
/// (<c>TargetIn</c>, at <c>PostgresFixture.SiteId</c>), one in a different district
/// (<c>TargetOut</c>, at <c>OtherSiteId</c>), and one with no site at all (<c>TargetNoSite</c>) —
/// the case CameraRepository's placement rule says a geo dimension cannot constrain.
/// </remarks>
public sealed class GeographyScopeTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly PostgresFixture _fixture;

    private static readonly Guid TestUser      = Guid.Parse("d2222222-2222-2222-2222-222222222222");
    private static readonly Guid TestGroup     = Guid.Parse("f2222222-2222-2222-2222-222222222222");
    private static readonly Guid OrgScope      = Guid.Parse("e3333333-3333-3333-3333-333333333333");
    private static readonly Guid GeoScope      = Guid.Parse("e4444444-4444-4444-4444-444444444444");
    private static readonly Guid OtherDistrict = Guid.Parse("b3333333-3333-3333-3333-333333333333");
    private static readonly Guid OtherSiteId   = Guid.Parse("c3333333-3333-3333-3333-333333333333");

    private static readonly Guid TargetIn     = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid TargetOut    = Guid.Parse("00000000-0000-0000-0000-0000000000a2");
    private static readonly Guid TargetNoSite = Guid.Parse("00000000-0000-0000-0000-0000000000a3");

    public GeographyScopeTests(PostgresFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        await _fixture.ResetTargetsAsync();

        await _fixture.ExecuteAsync($"""
            DELETE FROM federation.federation_event;
            DELETE FROM federation.detection_event;
            DELETE FROM federation.federated_camera;
            DELETE FROM federation.api_key;
            DELETE FROM federation.user_groups;
            DELETE FROM federation.group_scopes;
            DELETE FROM federation.access_groups;
            DELETE FROM federation.role_permissions WHERE role_id IN (
                SELECT id FROM federation.roles WHERE code = 'GEO_SCOPE_TEST');
            DELETE FROM federation.roles WHERE code = 'GEO_SCOPE_TEST';
            DELETE FROM federation.scopes WHERE id IN ('{OrgScope}', '{GeoScope}');
            DELETE FROM federation.platform_users WHERE id = '{TestUser}';

            -- A district the test caller is NOT scoped to, with a site inside it.
            INSERT INTO federation.geographic_areas (id, code, name, area_type)
            VALUES ('{OtherDistrict}', 'OTH', 'Other District', 'DISTRICT')
            ON CONFLICT (id) DO NOTHING;
            INSERT INTO federation.sites (id, code, name, geographic_area_id)
            VALUES ('{OtherSiteId}', 'SITE-OTHER', 'Other Site', '{OtherDistrict}')
            ON CONFLICT (id) DO NOTHING;

            -- A bespoke role carrying exactly the permissions these repositories exercise, so
            -- the test does not depend on which seeded role happens to carry which permission.
            INSERT INTO federation.roles (code, name, description, is_system)
            VALUES ('GEO_SCOPE_TEST', 'Geo Scope Test', 'Integration-test-only role', FALSE);
            INSERT INTO federation.role_permissions (role_id, permission_code)
            SELECT r.id, p.code FROM federation.roles r, unnest(ARRAY[
                'vms.read', 'vms.create', 'vms.update', 'vms.delete',
                'observation.read', 'observation.write', 'event.read'
            ]) AS p(code)
            WHERE r.code = 'GEO_SCOPE_TEST';

            INSERT INTO federation.platform_users
                (id, username, display_name, password_hash, password_salt, password_iterations)
            VALUES ('{TestUser}', 'geoscope', 'Geo Scope Tester', '\x00', '\x00', 600000);

            -- Scoped to PoliceUnit organizationally and DistrictId geographically — both
            -- dimensions declared, so IsUnscopedFor/IsUnscopedForGeography are both false for
            -- this caller and every check in the repositories under test actually runs.
            INSERT INTO federation.scopes (id, scope_type, organization_unit_id)
            VALUES ('{OrgScope}', 'ORGANIZATION', '{PostgresFixture.PoliceUnit}');
            INSERT INTO federation.scopes (id, scope_type, geographic_area_id)
            VALUES ('{GeoScope}', 'GEOGRAPHY', '{PostgresFixture.DistrictId}');

            INSERT INTO federation.access_groups (id, code, name, role_id, status)
            SELECT '{TestGroup}', 'GEO-SCOPE-TEST-GROUP', 'Geo Scope Test Group', r.id, 'ACTIVE'
            FROM federation.roles r WHERE r.code = 'GEO_SCOPE_TEST';
            INSERT INTO federation.group_scopes VALUES
                ('{TestGroup}', '{OrgScope}'), ('{TestGroup}', '{GeoScope}');
            INSERT INTO federation.user_groups (user_id, group_id)
            VALUES ('{TestUser}', '{TestGroup}');

            -- Three targets: in-district, out-of-district, and site-less.
            INSERT INTO federation.connector_target
                (id, code, organization_unit_id, site_id, display_name, vendor, endpoint,
                 credential_reference)
            VALUES
                ('{TargetIn}', 'TGT-IN', '{PostgresFixture.PoliceUnit}', '{PostgresFixture.SiteId}',
                 'In-district target', 'Onvif'::federation.vendor_kind, 'http://10.0.0.1',
                 'vault://in'),
                ('{TargetOut}', 'TGT-OUT', '{PostgresFixture.PoliceUnit}', '{OtherSiteId}',
                 'Out-of-district target', 'Onvif'::federation.vendor_kind, 'http://10.0.0.2',
                 'vault://out'),
                ('{TargetNoSite}', 'TGT-NOSITE', '{PostgresFixture.PoliceUnit}', NULL,
                 'Site-less target', 'Onvif'::federation.vendor_kind, 'http://10.0.0.3',
                 'vault://nosite');

            -- One discovered camera per target, for DetectionRepository.ResolveCameraAsync.
            INSERT INTO federation.federated_camera
                (target_id, native_camera_id, organization_unit_id, site_id)
            VALUES
                ('{TargetIn}', 'ch1', '{PostgresFixture.PoliceUnit}', '{PostgresFixture.SiteId}'),
                ('{TargetOut}', 'ch1', '{PostgresFixture.PoliceUnit}', '{OtherSiteId}');

            SELECT federation.ensure_event_partitions(CURRENT_DATE, 2);
            """);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// The scoped caller under test: <c>UserId</c> only, no unscoped permissions on either
    /// dimension — reachability is resolved entirely from the DB seed above via
    /// <c>authorized_org_units</c> / <c>authorized_geographic_areas</c>, exactly as it would be
    /// for a real logged-in user.
    /// </summary>
    private static CallerContext ScopedCaller() => new()
    {
        UserId = TestUser,
        Actor = "geoscope",
        Permissions = new HashSet<string>(StringComparer.Ordinal)
        {
            "vms.read", "vms.create", "vms.update", "vms.delete",
            "observation.read", "observation.write", "event.read",
        },
    };

    // ---- ConnectorTargetRepository (9-H1) -------------------------------

    [Fact]
    public async Task Vms_GetAsync_InDistrictTarget_IsReachable()
    {
        var repo = new ConnectorTargetRepository(_fixture.DataSource);
        (await repo.GetAsync(TargetIn, ScopedCaller(), CancellationToken.None)).ShouldNotBeNull();
    }

    [Fact]
    public async Task Vms_GetAsync_OutOfDistrictTarget_IsNotReachable()
    {
        var repo = new ConnectorTargetRepository(_fixture.DataSource);
        (await repo.GetAsync(TargetOut, ScopedCaller(), CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task Vms_GetAsync_SiteLessTarget_IsReachable()
    {
        // No site means no geographic key, so the geo dimension cannot constrain it — the
        // organization dimension still does, and this caller is in that organization.
        var repo = new ConnectorTargetRepository(_fixture.DataSource);
        (await repo.GetAsync(TargetNoSite, ScopedCaller(), CancellationToken.None)).ShouldNotBeNull();
    }

    [Fact]
    public async Task Vms_ListAsync_ReturnsInDistrictAndSiteLess_NotOutOfDistrict()
    {
        var repo = new ConnectorTargetRepository(_fixture.DataSource);
        var ids = (await repo.ListAsync(ScopedCaller(), CancellationToken.None))
            .Select(t => t.Id).ToHashSet();

        ids.ShouldContain(TargetIn);
        ids.ShouldContain(TargetNoSite);
        ids.ShouldNotContain(TargetOut);
    }

    [Fact]
    public async Task Vms_SetStateAsync_OutOfDistrictTarget_AffectsNoRows()
    {
        var repo = new ConnectorTargetRepository(_fixture.DataSource);
        await using var work = await UnitOfWork.BeginAsync(_fixture.DataSource, CancellationToken.None);

        (await repo.SetStateAsync(TargetOut, TargetState.Quarantined, ScopedCaller(), work,
            CancellationToken.None)).ShouldBeFalse();
    }

    [Fact]
    public async Task Vms_DeleteAsync_OutOfDistrictTarget_AffectsNoRows()
    {
        var repo = new ConnectorTargetRepository(_fixture.DataSource);
        await using var work = await UnitOfWork.BeginAsync(_fixture.DataSource, CancellationToken.None);

        (await repo.DeleteAsync(TargetOut, ScopedCaller(), work, CancellationToken.None)).ShouldBeFalse();
    }

    [Fact]
    public async Task Vms_UpsertAsync_RegisteringInAnotherDistrict_ThrowsForbidden()
    {
        var repo = new ConnectorTargetRepository(_fixture.DataSource);
        await using var work = await UnitOfWork.BeginAsync(_fixture.DataSource, CancellationToken.None);

        var target = new ConnectorTarget
        {
            Id = Guid.Empty,
            Code = "TGT-NEW-OUT",
            OrganizationUnitId = PostgresFixture.PoliceUnit,
            SiteId = OtherSiteId,
            DisplayName = "Attempted escalation",
            Vendor = VendorKind.Onvif,
            Endpoint = "http://10.0.0.9",
            CredentialReference = "vault://escalation",
        };

        await Should.ThrowAsync<ForbiddenException>(
            () => repo.UpsertAsync(target, ScopedCaller(), work, CancellationToken.None));
    }

    [Fact]
    public async Task Vms_UpsertAsync_RegisteringInOwnDistrict_Succeeds()
    {
        var repo = new ConnectorTargetRepository(_fixture.DataSource);
        await using var work = await UnitOfWork.BeginAsync(_fixture.DataSource, CancellationToken.None);

        var target = new ConnectorTarget
        {
            Id = Guid.Empty,
            Code = "TGT-NEW-IN",
            OrganizationUnitId = PostgresFixture.PoliceUnit,
            SiteId = PostgresFixture.SiteId,
            DisplayName = "Legitimate registration",
            Vendor = VendorKind.Onvif,
            Endpoint = "http://10.0.0.10",
            CredentialReference = "vault://legitimate",
        };

        var id = await repo.UpsertAsync(target, ScopedCaller(), work, CancellationToken.None);
        id.ShouldNotBe(Guid.Empty);
    }

    // ---- FederationQueryRepository.OverviewAsync -------------------------

    [Fact]
    public async Task Overview_CountsOnlyInScopeTargets()
    {
        var repo = new FederationQueryRepository(_fixture.DataSource);
        var overview = await repo.OverviewAsync(ScopedCaller(), CancellationToken.None);

        // TargetIn + TargetNoSite are in scope; TargetOut is not.
        overview.Targets.ShouldBe(2);
    }

    // ---- DetectionRepository (15-M1) --------------------------------------

    [Fact]
    public async Task Detections_IngestForOutOfDistrictCamera_ThrowsForbidden()
    {
        var detections = new DetectionRepository(_fixture.DataSource);
        await using var work = await UnitOfWork.BeginAsync(_fixture.DataSource, CancellationToken.None);

        var resolved = await detections.ResolveCameraAsync($"{TargetOut}:ch1", CancellationToken.None);
        resolved.ShouldNotBeNull();

        var evt = new DetectionEvent
        {
            Id = "evt-forbidden-test",
            CameraId = $"{TargetOut}:ch1",
            EventType = "AnprDetection",
            Timestamp = DateTimeOffset.UtcNow,
            Confidence = 0.9,
        };

        await Should.ThrowAsync<ForbiddenException>(() => detections.IngestAsync(
            evt, resolved!.Value.TargetId, resolved.Value.NativeCameraId, resolved.Value.CameraId,
            resolved.Value.OrganizationUnitId, resolved.Value.SiteId, plateNumberNormalized: null,
            ScopedCaller(), work, CancellationToken.None));
    }

    [Fact]
    public async Task Detections_IngestForInDistrictCamera_SucceedsAndIsFoundBySearch()
    {
        var detections = new DetectionRepository(_fixture.DataSource);
        await using var work = await UnitOfWork.BeginAsync(_fixture.DataSource, CancellationToken.None);

        var resolved = await detections.ResolveCameraAsync($"{TargetIn}:ch1", CancellationToken.None);
        resolved.ShouldNotBeNull();

        var evt = new DetectionEvent
        {
            Id = "evt-allowed-test",
            CameraId = $"{TargetIn}:ch1",
            EventType = "AnprDetection",
            Timestamp = DateTimeOffset.UtcNow,
            Confidence = 0.9,
        };

        var inserted = await detections.IngestAsync(
            evt, resolved!.Value.TargetId, resolved.Value.NativeCameraId, resolved.Value.CameraId,
            resolved.Value.OrganizationUnitId, resolved.Value.SiteId, plateNumberNormalized: null,
            ScopedCaller(), work, CancellationToken.None);
        inserted.ShouldBeTrue();
        await work.CommitAsync(CancellationToken.None);

        var found = await detections.SearchAsync(
            plateNumberNormalized: null, targetId: null,
            from: DateTimeOffset.UtcNow.AddMinutes(-5), to: DateTimeOffset.UtcNow.AddMinutes(5),
            limit: 100, ScopedCaller(), CancellationToken.None);

        found.ShouldContain(r => r.EventId == "evt-allowed-test");
    }

    [Fact]
    public async Task Detections_SearchAsync_DoesNotReturnOutOfDistrictRows()
    {
        // Insert directly (bypassing the ingest guard) to prove SearchAsync itself filters,
        // not just IngestAsync.
        await _fixture.ExecuteAsync($"""
            INSERT INTO federation.detection_event
                (event_id, occurred_at, target_id, native_camera_id, camera_id,
                 organization_unit_id, site_id, event_type, confidence)
            VALUES ('evt-out-of-scope', now(), '{TargetOut}', 'ch1', NULL,
                    '{PostgresFixture.PoliceUnit}', '{OtherSiteId}', 'AnprDetection', 0.9)
            ON CONFLICT (event_id, occurred_at) DO NOTHING;
            """);

        var detections = new DetectionRepository(_fixture.DataSource);
        var found = await detections.SearchAsync(
            plateNumberNormalized: null, targetId: null,
            from: DateTimeOffset.UtcNow.AddMinutes(-5), to: DateTimeOffset.UtcNow.AddMinutes(5),
            limit: 100, ScopedCaller(), CancellationToken.None);

        found.ShouldNotContain(r => r.EventId == "evt-out-of-scope");
    }

    // ---- EventQueryRepository (14-M1) --------------------------------------

    [Fact]
    public async Task Events_QueryAsync_FiltersOnGeographyAsWellAsOrganization()
    {
        await _fixture.ExecuteAsync($"""
            INSERT INTO federation.federation_event
                (event_id, source_vms_id, camera_id, organization_unit_id, site_id,
                 event_type, occurred_at)
            VALUES
                ('evt-geo-in', '{TargetIn}', 'ch1', '{PostgresFixture.PoliceUnit}',
                 '{PostgresFixture.SiteId}', 'AnprDetection', now()),
                ('evt-geo-out', '{TargetOut}', 'ch1', '{PostgresFixture.PoliceUnit}',
                 '{OtherSiteId}', 'AnprDetection', now())
            ON CONFLICT (source_vms_id, source_event_id, occurred_at) DO NOTHING;
            """);

        var events = new EventQueryRepository(_fixture.DataSource);
        var rows = await events.QueryAsync(
            new EventQuery(
                DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddMinutes(5),
                PageSize: 100, CameraId: null, EventType: null, ObjectReference: null,
                CursorTime: null, CursorId: null),
            ScopedCaller(), CancellationToken.None);

        var ids = rows.Select(r => r.EventId).ToHashSet();
        ids.ShouldContain("evt-geo-in");
        ids.ShouldNotContain("evt-geo-out");
    }
}
