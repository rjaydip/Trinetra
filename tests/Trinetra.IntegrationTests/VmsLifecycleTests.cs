using Shouldly;
using Trinetra.Federation.Core.Model;
using Trinetra.Federation.Storage;
using Trinetra.Federation.Storage.Repositories;

namespace Trinetra.IntegrationTests;

/// <summary>
/// Verifies the PR3 VMS-lifecycle fixes: a replace never touches state (9-NEW-H), delete refuses
/// an Active target (9-H2) and is gated on its own permission (9-H3), and register/replace
/// validate that the organization unit and geographic area are real and ACTIVE (9-M2).
/// </summary>
/// <remarks>
/// Most of these are business-rule tests, not scope tests — <see cref="GeographyScopeTests"/>
/// already covers organization/geography scoping for this repository, so the happy-path callers
/// here use <see cref="CallerContext.System"/> (full rights, no HTTP caller) to keep the focus on
/// the lifecycle rule under test rather than re-proving scope resolution. 9-H3 is the one
/// permission-gate test, and it needs no database seeding at all: <c>caller.Require(...)</c> is
/// an in-memory check on the claims already in hand, checked before any SQL runs.
/// </remarks>
public sealed class VmsLifecycleTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly PostgresFixture _fixture;

    private static readonly Guid TargetId = Guid.Parse("00000000-0000-0000-0000-0000000000b1");
    private static readonly Guid InactiveOrgUnit = Guid.Parse("a4444444-4444-4444-4444-444444444444");
    private static readonly Guid InactiveAreaId = Guid.Parse("b4444444-4444-4444-4444-444444444444");

    public VmsLifecycleTests(PostgresFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        await _fixture.ResetTargetsAsync();

        await _fixture.ExecuteAsync($"""
            INSERT INTO federation.connector_target
                (id, code, organization_unit_id, geographic_area_id, display_name, vendor, endpoint,
                 credential_reference)
            VALUES ('{TargetId}', 'TGT-LIFECYCLE', '{PostgresFixture.PoliceUnit}',
                    '{PostgresFixture.VillageId}', 'Lifecycle test target',
                    'Onvif'::federation.vendor_kind, 'http://10.0.0.1', 'vault://lifecycle');

            -- A deactivated unit and area, for 9-M2.
            INSERT INTO federation.organization_units
                (id, organization_id, parent_unit_id, code, name, unit_type, status)
            VALUES ('{InactiveOrgUnit}', '{PostgresFixture.OrgId}', NULL, 'PD-RETIRED',
                    'Retired Unit', 'DEPARTMENT', 'INACTIVE')
            ON CONFLICT (id) DO NOTHING;
            INSERT INTO federation.geographic_areas (id, parent_area_id, code, name, area_type, status)
            VALUES ('{InactiveAreaId}', '{PostgresFixture.DistrictId}', 'AHM-RETIRED', 'Retired Area',
                    'VILLAGE', 'INACTIVE')
            ON CONFLICT (id) DO NOTHING;
            """);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static CallerContext SystemCaller() => CallerContext.System("vms-lifecycle-test");

    private static ConnectorTarget NewTarget(string code, Guid orgUnit, Guid? areaId) => new()
    {
        Id = Guid.Empty,
        Code = code,
        OrganizationUnitId = orgUnit,
        GeographicAreaId = areaId,
        DisplayName = code,
        Vendor = VendorKind.Onvif,
        Endpoint = "http://10.0.0.99",
        CredentialReference = "vault://new",
    };

    // ---- 9-NEW-H: replace never touches state ----------------------------

    [Fact]
    public async Task Upsert_OnAQuarantinedTarget_LeavesStateQuarantined()
    {
        var repo = new ConnectorTargetRepository(_fixture.DataSource);

        await using (var work = await UnitOfWork.BeginAsync(_fixture.DataSource, CancellationToken.None))
        {
            (await repo.SetStateAsync(TargetId, TargetState.Quarantined, SystemCaller(), work,
                CancellationToken.None)).ShouldBeTrue();
            await work.CommitAsync(CancellationToken.None);
        }

        // Simulates PUT /vms/{id}: re-upsert the same id with an unrelated field changed and
        // State left at the model default (Active) — exactly what TryBuild produces today.
        await using (var work = await UnitOfWork.BeginAsync(_fixture.DataSource, CancellationToken.None))
        {
            var replacement = NewTarget("TGT-LIFECYCLE", PostgresFixture.PoliceUnit, PostgresFixture.VillageId)
                with
            {
                Id = TargetId,
                DisplayName = "Renamed via PUT",
            };

            await repo.UpsertAsync(replacement, SystemCaller(), work, CancellationToken.None);
            await work.CommitAsync(CancellationToken.None);
        }

        var after = await repo.GetAsync(TargetId, SystemCaller(), CancellationToken.None);
        after.ShouldNotBeNull();
        after.State.ShouldBe(TargetState.Quarantined,
            "a replace must never re-arm a target that was deliberately quarantined");
        after.DisplayName.ShouldBe("Renamed via PUT", "the replace itself must still apply");
    }

    [Fact]
    public async Task Upsert_OnAnActiveTarget_LeavesStateActive()
    {
        // Regression case: the ordinary path must be unaffected.
        var repo = new ConnectorTargetRepository(_fixture.DataSource);

        await using var work = await UnitOfWork.BeginAsync(_fixture.DataSource, CancellationToken.None);
        var replacement = NewTarget("TGT-LIFECYCLE", PostgresFixture.PoliceUnit, PostgresFixture.VillageId)
            with
        { Id = TargetId };

        await repo.UpsertAsync(replacement, SystemCaller(), work, CancellationToken.None);
        await work.CommitAsync(CancellationToken.None);

        var after = await repo.GetAsync(TargetId, SystemCaller(), CancellationToken.None);
        after.ShouldNotBeNull();
        after.State.ShouldBe(TargetState.Active);
    }

    // ---- 9-H2: delete refuses an Active target ----------------------------

    [Fact]
    public async Task Delete_ActiveTarget_HasNothingRepositoryLevelToStopIt_GuardLivesInTheEndpoint()
    {
        // The 9-H2 refusal itself lives in VmsEndpoints.RemoveAsync (before.State == Active =>
        // 409), which needs an HTTP-level harness this repo does not have to exercise directly.
        // What this test locks down is the precondition that guard depends on: GetAsync must
        // still report the real state so the endpoint has something correct to check. If this
        // ever silently returns Disabled/Quarantined for an Active row, the endpoint's guard
        // would silently stop protecting anything.
        var repo = new ConnectorTargetRepository(_fixture.DataSource);

        var before = await repo.GetAsync(TargetId, SystemCaller(), CancellationToken.None);
        before.ShouldNotBeNull();
        before.State.ShouldBe(TargetState.Active, "the fixture registers this target Active");
    }

    [Fact]
    public async Task Delete_NonActiveTarget_Succeeds()
    {
        var repo = new ConnectorTargetRepository(_fixture.DataSource);

        await using (var work = await UnitOfWork.BeginAsync(_fixture.DataSource, CancellationToken.None))
        {
            await repo.SetStateAsync(TargetId, TargetState.Disabled, SystemCaller(), work,
                CancellationToken.None);
            await work.CommitAsync(CancellationToken.None);
        }

        await using (var work = await UnitOfWork.BeginAsync(_fixture.DataSource, CancellationToken.None))
        {
            (await repo.DeleteAsync(TargetId, SystemCaller(), work, CancellationToken.None))
                .ShouldBeTrue();
            await work.CommitAsync(CancellationToken.None);
        }

        (await repo.GetAsync(TargetId, SystemCaller(), CancellationToken.None)).ShouldBeNull();
    }

    // ---- 9-H3: delete is gated on vms.delete, not vms.update --------------

    [Fact]
    public async Task Delete_CallerWithoutVmsDelete_IsForbidden()
    {
        // No database seeding needed: caller.Require(...) is an in-memory check on the claims
        // already in hand, and it runs before DeleteAsync issues any SQL.
        var caller = new CallerContext
        {
            Actor = "no-delete",
            Permissions = new HashSet<string>(StringComparer.Ordinal) { "vms.read", "vms.update" },
        };

        var repo = new ConnectorTargetRepository(_fixture.DataSource);
        await using var work = await UnitOfWork.BeginAsync(_fixture.DataSource, CancellationToken.None);

        await Should.ThrowAsync<ForbiddenException>(
            () => repo.DeleteAsync(TargetId, caller, work, CancellationToken.None));
    }

    // ---- 9-M2: organizationUnitId / geographicAreaId must be ACTIVE -------

    [Fact]
    public async Task Upsert_ReferencingAnInactiveOrganizationUnit_ThrowsInvalidReference()
    {
        var repo = new ConnectorTargetRepository(_fixture.DataSource);
        await using var work = await UnitOfWork.BeginAsync(_fixture.DataSource, CancellationToken.None);

        var target = NewTarget("TGT-BAD-ORG", InactiveOrgUnit, PostgresFixture.VillageId);

        await Should.ThrowAsync<InvalidReferenceException>(
            () => repo.UpsertAsync(target, SystemCaller(), work, CancellationToken.None));
    }

    [Fact]
    public async Task Upsert_ReferencingAnInactiveArea_ThrowsInvalidReference()
    {
        var repo = new ConnectorTargetRepository(_fixture.DataSource);
        await using var work = await UnitOfWork.BeginAsync(_fixture.DataSource, CancellationToken.None);

        var target = NewTarget("TGT-BAD-AREA", PostgresFixture.PoliceUnit, InactiveAreaId);

        await Should.ThrowAsync<InvalidReferenceException>(
            () => repo.UpsertAsync(target, SystemCaller(), work, CancellationToken.None));
    }

    [Fact]
    public async Task Upsert_ReferencingActiveOrganizationUnitAndArea_Succeeds()
    {
        // Regression case: the fixture's own PoliceUnit/VillageId must still register cleanly —
        // this is the same pair GeographyScopeTests' Vms_UpsertAsync_* tests rely on being ACTIVE.
        var repo = new ConnectorTargetRepository(_fixture.DataSource);
        await using var work = await UnitOfWork.BeginAsync(_fixture.DataSource, CancellationToken.None);

        var target = NewTarget("TGT-GOOD-REF", PostgresFixture.PoliceUnit, PostgresFixture.VillageId);

        var id = await repo.UpsertAsync(target, SystemCaller(), work, CancellationToken.None);
        id.ShouldNotBe(Guid.Empty);
    }

    [Fact]
    public async Task Upsert_WithNoArea_StillValidatesOrganizationUnit()
    {
        var repo = new ConnectorTargetRepository(_fixture.DataSource);
        await using var work = await UnitOfWork.BeginAsync(_fixture.DataSource, CancellationToken.None);

        var target = NewTarget("TGT-BAD-ORG-NOAREA", InactiveOrgUnit, areaId: null);

        await Should.ThrowAsync<InvalidReferenceException>(
            () => repo.UpsertAsync(target, SystemCaller(), work, CancellationToken.None));
    }
}
