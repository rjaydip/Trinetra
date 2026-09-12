using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Trinetra.Federation.Api.Auth;
using Trinetra.Federation.Api.Contracts;
using Trinetra.Federation.Api.Endpoints;
using Trinetra.Federation.Core.Model;
using Trinetra.Federation.Storage;
using Trinetra.Federation.Storage.Repositories;

namespace Trinetra.IntegrationTests;

/// <summary>
/// Finding 10-M7: <c>ReconcileAsync</c> checked that the caller could reach both the registry
/// camera and the federated row, but never that the two AGREE with each other — a registry
/// camera in one org/geo could be linked to a VMS camera the federation layer places in another,
/// leaving the estate with a camera whose two placements permanently contradict.
/// </summary>
public sealed class ReconcileConsistencyTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly PostgresFixture _fixture;

    private static readonly Guid TargetId = Guid.Parse("00000000-0000-0000-0000-0000000000c1");
    private static readonly Guid OtherDistrict = Guid.Parse("00000000-0000-0000-0000-0000000000c2");

    public ReconcileConsistencyTests(PostgresFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        await _fixture.ResetTargetsAsync();

        await _fixture.ExecuteAsync($"""
            DELETE FROM federation.federated_camera WHERE target_id = '{TargetId}';
            DELETE FROM federation.cameras WHERE camera_code LIKE 'RECON-%';
            DELETE FROM federation.connector_target WHERE id = '{TargetId}';

            INSERT INTO federation.geographic_areas (id, code, name, area_type)
            VALUES ('{OtherDistrict}', 'RECON-OTH', 'Reconcile Other District', 'DISTRICT')
            ON CONFLICT (id) DO NOTHING;

            INSERT INTO federation.connector_target
                (id, code, organization_unit_id, geographic_area_id, display_name, vendor, endpoint,
                 credential_reference)
            VALUES ('{TargetId}', 'RECON-TGT', '{PostgresFixture.PoliceUnit}',
                    '{PostgresFixture.VillageId}', 'Reconcile Target',
                    'Onvif'::federation.vendor_kind, 'http://10.0.0.9', 'vault://recon');
            """);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private ReconciliationRepository Repo => new(_fixture.DataSource);

    private static readonly CallerContext SystemCaller = new()
    {
        UserId = Guid.NewGuid(),
        IsSystem = true,
        Actor = "reconcile-consistency-test",
        Permissions = new HashSet<string>(StringComparer.Ordinal),
    };

    private async Task<Guid> CreateCameraAsync(string code, Guid orgUnitId, Guid geoAreaId)
    {
        await using var work = await UnitOfWork.BeginAsync(_fixture.DataSource, CancellationToken.None);
        var id = await new CameraRepository(_fixture.DataSource).CreateAsync(
            new Camera
            {
                Id = Guid.Empty, Code = code, Name = $"Camera {code}",
                OrganizationUnitId = orgUnitId, GeographicAreaId = geoAreaId,
                CameraType = "FIXED", Latitude = 23.03, Longitude = 72.58,
            },
            SystemCaller, work, CancellationToken.None);
        await work.CommitAsync(CancellationToken.None);
        return id;
    }

    private async Task SeedFederatedAsync(string nativeId, Guid orgUnitId, Guid geoAreaId) =>
        await _fixture.ExecuteAsync($"""
            INSERT INTO federation.federated_camera
                (target_id, native_camera_id, organization_unit_id, geographic_area_id)
            VALUES ('{TargetId}', '{nativeId}', '{orgUnitId}', '{geoAreaId}');
            """);

    [Fact]
    public async Task ReconcileAsync_MatchingOrgAndGeo_Links()
    {
        var cameraId = await CreateCameraAsync(
            "RECON-OK", PostgresFixture.PoliceUnit, PostgresFixture.VillageId);
        await SeedFederatedAsync("ch-ok", PostgresFixture.PoliceUnit, PostgresFixture.VillageId);

        await using var work = await UnitOfWork.BeginAsync(_fixture.DataSource, CancellationToken.None);
        var result = await Repo.ReconcileAsync(
            cameraId, TargetId, "ch-ok", adoptStreamReference: false, adoptVmsId: false,
            SystemCaller, work, CancellationToken.None);
        await work.CommitAsync(CancellationToken.None);

        result.Status.ShouldBe(ReconcileStatus.Linked);
    }

    [Fact]
    public async Task ReconcileAsync_DifferentOrganization_IsInconsistentNotLinked()
    {
        // Registry camera under PoliceUnit; federated row reports a DIFFERENT org unit entirely.
        var otherOrgUnit = PostgresFixture.AhmedabadCp;
        var cameraId = await CreateCameraAsync(
            "RECON-ORG-MISMATCH", PostgresFixture.PoliceUnit, PostgresFixture.VillageId);
        await SeedFederatedAsync("ch-org-mismatch", otherOrgUnit, PostgresFixture.VillageId);

        await using var work = await UnitOfWork.BeginAsync(_fixture.DataSource, CancellationToken.None);
        var result = await Repo.ReconcileAsync(
            cameraId, TargetId, "ch-org-mismatch", adoptStreamReference: false, adoptVmsId: false,
            SystemCaller, work, CancellationToken.None);
        await work.CommitAsync(CancellationToken.None);

        result.Status.ShouldBe(ReconcileStatus.Inconsistent);

        var linkedCameraId = await _fixture.ScalarAsync<Guid?>($"""
            SELECT camera_id FROM federation.federated_camera
            WHERE target_id = '{TargetId}' AND native_camera_id = 'ch-org-mismatch';
            """);
        linkedCameraId.ShouldBeNull("a rejected reconcile must not have linked the row anyway");
    }

    [Fact]
    public async Task ReconcileAsync_DifferentGeography_IsInconsistentNotLinked()
    {
        var cameraId = await CreateCameraAsync(
            "RECON-GEO-MISMATCH", PostgresFixture.PoliceUnit, PostgresFixture.VillageId);
        await SeedFederatedAsync("ch-geo-mismatch", PostgresFixture.PoliceUnit, OtherDistrict);

        await using var work = await UnitOfWork.BeginAsync(_fixture.DataSource, CancellationToken.None);
        var result = await Repo.ReconcileAsync(
            cameraId, TargetId, "ch-geo-mismatch", adoptStreamReference: false, adoptVmsId: false,
            SystemCaller, work, CancellationToken.None);
        await work.CommitAsync(CancellationToken.None);

        result.Status.ShouldBe(ReconcileStatus.Inconsistent);
    }

    /// <summary>A federated row the VMS hasn't sited yet has nothing to disagree with.</summary>
    [Fact]
    public async Task ReconcileAsync_FederatedRowHasNoGeography_OrgMatchIsEnough()
    {
        var cameraId = await CreateCameraAsync(
            "RECON-NO-GEO", PostgresFixture.PoliceUnit, PostgresFixture.VillageId);
        await _fixture.ExecuteAsync($"""
            INSERT INTO federation.federated_camera
                (target_id, native_camera_id, organization_unit_id, geographic_area_id)
            VALUES ('{TargetId}', 'ch-no-geo', '{PostgresFixture.PoliceUnit}', NULL);
            """);

        await using var work = await UnitOfWork.BeginAsync(_fixture.DataSource, CancellationToken.None);
        var result = await Repo.ReconcileAsync(
            cameraId, TargetId, "ch-no-geo", adoptStreamReference: false, adoptVmsId: false,
            SystemCaller, work, CancellationToken.None);
        await work.CommitAsync(CancellationToken.None);

        result.Status.ShouldBe(ReconcileStatus.Linked);
    }

    // ---- CameraReconciliationEndpoints.FromFederatedAsync (endpoint-level) ----------

    private static readonly IServiceProvider Services = new ServiceCollection()
        .AddLogging()
        .AddProblemDetails()
        .BuildServiceProvider();

    /// <summary>
    /// <c>FromFederatedAsync</c> reads its caller from <c>CallerContextFactory.From(HttpContext)</c>,
    /// so the test drives real claims rather than a <see cref="CallerContext"/> object directly.
    /// Unscoped for `camera.create`/`camera.reconcile`/`camera.read` — what the handler and the
    /// repository methods it calls actually check.
    /// </summary>
    private static DefaultHttpContext EndpointContext() => new()
    {
        User = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("sub", Guid.NewGuid().ToString()),
            new Claim(TrinetraClaims.Permission, "camera.create"),
            new Claim(TrinetraClaims.Permission, "camera.reconcile"),
            new Claim(TrinetraClaims.Permission, "camera.read"),
            new Claim(TrinetraClaims.UnscopedPermission, "camera.create"),
            new Claim(TrinetraClaims.UnscopedPermission, "camera.reconcile"),
            new Claim(TrinetraClaims.UnscopedPermission, "camera.read"),
            new Claim(TrinetraClaims.UnscopedGeography, "camera.create"),
            new Claim(TrinetraClaims.UnscopedGeography, "camera.reconcile"),
            new Claim(TrinetraClaims.UnscopedGeography, "camera.read"),
        ], "test")),
        RequestServices = Services,
    };

    /// <summary>
    /// dotnet-expert review of this PR asked for direct coverage of `FromFederatedAsync`'s own
    /// new code — the `Inconsistent` → `409` mapping and, more importantly, that the camera row
    /// it had already inserted in the same transaction is genuinely rolled back, not left behind
    /// as an orphan half-created record.
    /// </summary>
    [Fact]
    public async Task FromFederatedAsync_OrganizationOverrideDisagreesWithVms_Returns409_NoOrphanCameraRow()
    {
        await SeedFederatedAsync("ch-from-fed-mismatch", PostgresFixture.PoliceUnit, PostgresFixture.VillageId);

        var request = new CreateFromFederatedRequest(
            TargetId: TargetId,
            NativeCameraId: "ch-from-fed-mismatch",
            CameraCode: "RECON-FROM-FED-MISMATCH",
            CameraType: "FIXED",
            // Disagrees with the federated row's own PoliceUnit — the override this PR now rejects.
            OrganizationUnitId: PostgresFixture.AhmedabadCp,
            // SeedFederatedAsync doesn't set these; the request must, or the (unrelated)
            // "coordinates required" validation trips before the check under test even runs.
            Latitude: 23.03,
            Longitude: 72.58);

        var result = await CameraReconciliationEndpoints.FromFederatedAsync(
            request, new ReconciliationRepository(_fixture.DataSource), new CameraRepository(_fixture.DataSource),
            _fixture.DataSource, EndpointContext(), CancellationToken.None);

        var problem = ((Microsoft.AspNetCore.Http.HttpResults.ProblemHttpResult)result.Result!);
        problem.StatusCode.ShouldBe(409);
        problem.ProblemDetails.Title.ShouldBe("Placement mismatch");

        (await _fixture.ScalarAsync<long>(
            "SELECT count(*) FROM federation.cameras WHERE camera_code = 'RECON-FROM-FED-MISMATCH'"))
            .ShouldBe(0, "the camera inserted earlier in the same transaction must have rolled back");
    }
}
