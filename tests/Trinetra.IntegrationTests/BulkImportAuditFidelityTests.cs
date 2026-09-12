using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Trinetra.Federation.Api.Auth;
using Trinetra.Federation.Api.Contracts;
using Trinetra.Federation.Api.Endpoints;
using Trinetra.Federation.Storage;
using Trinetra.Federation.Storage.Repositories;

namespace Trinetra.IntegrationTests;

/// <summary>
/// Finding 10-M5: <c>POST /cameras/bulk-import</c>'s upsert-replace path used to audit
/// <c>before: null</c> for a row it was actually updating — unlike the single-row
/// <c>PUT /cameras/{id}</c>, which captures the prior state. Now both capture it the same way.
/// </summary>
public sealed class BulkImportAuditFidelityTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly PostgresFixture _fixture;

    public BulkImportAuditFidelityTests(PostgresFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        await _fixture.ResetTargetsAsync();

        await _fixture.ExecuteAsync("""
            DELETE FROM federation.config_audit WHERE entity_type = 'camera'
              AND entity_id IN (SELECT id::text FROM federation.cameras WHERE camera_code = 'BULK-AUDIT-1');
            DELETE FROM federation.cameras WHERE camera_code = 'BULK-AUDIT-1';
            """);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private CameraRepository Repo => new(_fixture.DataSource);

    private static readonly IServiceProvider Services = new ServiceCollection()
        .AddLogging()
        .AddProblemDetails()
        .BuildServiceProvider();

    /// <summary>
    /// <c>BulkImportAsync</c> reads its caller from <c>CallerContextFactory.From(HttpContext)</c>,
    /// which parses claims — passing a <see cref="CallerContext"/> object directly (as other
    /// tests in this suite do for repository-level calls) would be inert here, since the
    /// endpoint never sees it. Unscoped for `camera.create`/`camera.update`/`camera.read` (what
    /// the handler and repository actually check) via the same claim shape a real unscoped admin
    /// token carries. `camera.import` is included for realism (it's what the real route requires
    /// via `.RequirePermission(...)`) but is not itself exercised — this test calls the handler
    /// directly, bypassing routing/middleware/the permission filter entirely.
    /// </summary>
    private static DefaultHttpContext ContextFor() => new()
    {
        User = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("sub", Guid.NewGuid().ToString()),
            new Claim(TrinetraClaims.Permission, "camera.create"),
            new Claim(TrinetraClaims.Permission, "camera.import"),
            new Claim(TrinetraClaims.Permission, "camera.update"),
            new Claim(TrinetraClaims.Permission, "camera.read"),
            new Claim(TrinetraClaims.UnscopedPermission, "camera.create"),
            new Claim(TrinetraClaims.UnscopedPermission, "camera.import"),
            new Claim(TrinetraClaims.UnscopedPermission, "camera.update"),
            new Claim(TrinetraClaims.UnscopedPermission, "camera.read"),
            new Claim(TrinetraClaims.UnscopedGeography, "camera.create"),
            new Claim(TrinetraClaims.UnscopedGeography, "camera.import"),
            new Claim(TrinetraClaims.UnscopedGeography, "camera.update"),
            new Claim(TrinetraClaims.UnscopedGeography, "camera.read"),
        ], "test")),
        RequestServices = Services,
    };

    private static CameraWriteRequest MakeRequest(string name) => new(
        CameraCode: "BULK-AUDIT-1",
        Name: name,
        OrganizationUnitId: PostgresFixture.PoliceUnit,
        GeographicAreaId: PostgresFixture.VillageId,
        CameraType: "FIXED",
        Latitude: 23.03,
        Longitude: 72.58);

    [Fact]
    public async Task BulkUpsert_ExistingCamera_AuditsPriorState_NotNull()
    {
        // Seed: a create via bulk-import (insert mode).
        var createResult = await CameraEndpoints.BulkImportAsync(
            new BulkImportRequest("insert", [MakeRequest("Original Name")]),
            Repo, _fixture.DataSource, ContextFor(), CancellationToken.None);

        var created = ((Microsoft.AspNetCore.Http.HttpResults.Ok<BulkImportResult>)createResult.Result!).Value!;
        created.Created.ShouldBe(1, created.Rows[0].Error ?? "no error reported");
        var cameraId = created.Rows[0].CameraId!.Value;

        // The row under test: an upsert that changes an existing camera.
        var upsertResult = await CameraEndpoints.BulkImportAsync(
            new BulkImportRequest("upsert", [MakeRequest("Renamed")]),
            Repo, _fixture.DataSource, ContextFor(), CancellationToken.None);

        var upserted = ((Microsoft.AspNetCore.Http.HttpResults.Ok<BulkImportResult>)upsertResult.Result!).Value!;
        upserted.Updated.ShouldBe(1);

        var beforeState = await _fixture.ScalarAsync<string>($"""
            SELECT before_state::text FROM federation.config_audit
            WHERE entity_type = 'camera' AND entity_id = '{cameraId}' AND action = 'update'
            ORDER BY changed_at DESC LIMIT 1;
            """);

        beforeState.ShouldNotBeNull("the update audit row must capture the camera's prior state");
        beforeState!.Contains("Original Name", StringComparison.Ordinal).ShouldBeTrue(
            "before_state must reflect what the camera looked like before this bulk upsert");
    }
}
