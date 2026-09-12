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
/// Finding 10-M4: <c>POST /cameras/bulk-import</c> used to open and commit up to 500 separate
/// transactions, one per row. It now shares one connection/transaction for the whole batch and
/// isolates each row with a <c>SAVEPOINT</c> instead — this suite proves that switch didn't
/// weaken the one promise that matters: a bad row must not roll back the good ones around it.
/// </summary>
public sealed class BulkImportSavepointTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly PostgresFixture _fixture;

    public BulkImportSavepointTests(PostgresFixture fixture) => _fixture = fixture;

    private static readonly Guid ScopedUser  = Guid.Parse("d8888888-8888-8888-8888-888888888801");
    private static readonly Guid ScopedGroup = Guid.Parse("f8888888-8888-8888-8888-888888888801");
    private static readonly Guid OrgScope    = Guid.Parse("e8888888-8888-8888-8888-888888888801");
    private static readonly Guid GeoScope    = Guid.Parse("e8888888-8888-8888-8888-888888888802");

    public async Task InitializeAsync()
    {
        await _fixture.ResetTargetsAsync();

        await _fixture.ExecuteAsync($"""
            DELETE FROM federation.cameras
            WHERE camera_code IN
                ('SP-GOOD-1', 'SP-GOOD-2', 'SP-DUP', 'SP-GOOD-3', 'SP-UPSERT-DUP',
                 'SP-SCOPE-OK', 'SP-SCOPE-OUT');
            DELETE FROM federation.user_groups WHERE user_id = '{ScopedUser}';
            DELETE FROM federation.group_scopes WHERE group_id = '{ScopedGroup}';
            DELETE FROM federation.access_groups WHERE id = '{ScopedGroup}';
            DELETE FROM federation.role_permissions WHERE role_id IN (
                SELECT id FROM federation.roles WHERE code = 'BULK_SCOPE_TEST');
            DELETE FROM federation.roles WHERE code = 'BULK_SCOPE_TEST';
            DELETE FROM federation.scopes WHERE id IN ('{OrgScope}', '{GeoScope}');
            DELETE FROM federation.platform_users WHERE id = '{ScopedUser}';

            -- A caller scoped to the CHILD unit only, so a row targeting the PARENT
            -- (PostgresFixture.PoliceUnit) is a genuine org-scope ForbiddenException,
            -- not a made-up condition.
            INSERT INTO federation.platform_users
                (id, username, display_name, password_hash, password_salt, password_iterations)
            VALUES ('{ScopedUser}', 'bulk-scope-tester', 'Bulk Scope Tester', '\x00', '\x00', 600000);

            INSERT INTO federation.roles (code, name, description, is_system, status)
            VALUES ('BULK_SCOPE_TEST', 'Bulk Scope Test', 'Integration-test-only role',
                    FALSE, 'ACTIVE');
            INSERT INTO federation.role_permissions (role_id, permission_code)
            SELECT r.id, p.code FROM federation.roles r, unnest(ARRAY['camera.create', 'camera.read'])
                AS p(code)
            WHERE r.code = 'BULK_SCOPE_TEST';

            INSERT INTO federation.scopes (id, scope_type, organization_unit_id)
            VALUES ('{OrgScope}', 'ORGANIZATION', '{PostgresFixture.AhmedabadCp}');
            INSERT INTO federation.scopes (id, scope_type, geographic_area_id)
            VALUES ('{GeoScope}', 'GEOGRAPHY', '{PostgresFixture.VillageId}');

            INSERT INTO federation.access_groups (id, code, name, role_id, status)
            SELECT '{ScopedGroup}', 'BULK-SCOPE-TEST-GROUP', 'Bulk Scope Test Group', r.id, 'ACTIVE'
            FROM federation.roles r WHERE r.code = 'BULK_SCOPE_TEST';
            INSERT INTO federation.group_scopes VALUES
                ('{ScopedGroup}', '{OrgScope}'), ('{ScopedGroup}', '{GeoScope}');
            INSERT INTO federation.user_groups (user_id, group_id)
            VALUES ('{ScopedUser}', '{ScopedGroup}');
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
    /// so the test drives real claims rather than a <see cref="CallerContext"/> object directly.
    /// </summary>
    private static DefaultHttpContext ContextFor() => new()
    {
        User = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("sub", Guid.NewGuid().ToString()),
            new Claim(TrinetraClaims.Permission, "camera.create"),
            new Claim(TrinetraClaims.Permission, "camera.read"),
            new Claim(TrinetraClaims.Permission, "camera.update"),
            new Claim(TrinetraClaims.UnscopedPermission, "camera.create"),
            new Claim(TrinetraClaims.UnscopedPermission, "camera.read"),
            new Claim(TrinetraClaims.UnscopedPermission, "camera.update"),
            new Claim(TrinetraClaims.UnscopedGeography, "camera.create"),
            new Claim(TrinetraClaims.UnscopedGeography, "camera.read"),
            new Claim(TrinetraClaims.UnscopedGeography, "camera.update"),
        ], "test")),
        RequestServices = Services,
    };

    /// <summary>Scoped to <see cref="PostgresFixture.AhmedabadCp"/> only — org AND geo declared,
    /// neither unscoped — so <c>RequirePlacementAsync</c>'s DB-backed check actually runs.</summary>
    private static DefaultHttpContext ScopedContextFor() => new()
    {
        User = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("sub", ScopedUser.ToString()),
            new Claim(TrinetraClaims.Permission, "camera.create"),
            new Claim(TrinetraClaims.Permission, "camera.read"),
        ], "test")),
        RequestServices = Services,
    };

    private static CameraWriteRequest MakeRequest(string code) =>
        MakeRequest(code, PostgresFixture.PoliceUnit);

    private static CameraWriteRequest MakeRequest(string code, Guid organizationUnitId) => new(
        CameraCode: code,
        Name: $"Camera {code}",
        OrganizationUnitId: organizationUnitId,
        GeographicAreaId: PostgresFixture.VillageId,
        CameraType: "FIXED",
        Latitude: 23.03,
        Longitude: 72.58);

    [Fact]
    public async Task Insert_OneDuplicateCodeMidBatch_OthersStillCommit()
    {
        // Seed a live camera under 'SP-DUP' so the row targeting that code inside the batch
        // hits the real UniqueViolation path (PostgresException), not just a validation error —
        // the case that used to abort the whole per-row transaction before this fix, and now
        // must only discard that one savepoint.
        var seedResult = await CameraEndpoints.BulkImportAsync(
            new BulkImportRequest("insert", [MakeRequest("SP-DUP") with { Name = "Pre-existing" }]),
            Repo, _fixture.DataSource, ContextFor(), CancellationToken.None);
        ((Microsoft.AspNetCore.Http.HttpResults.Ok<BulkImportResult>)seedResult.Result!)
            .Value!.Created.ShouldBe(1);

        var result = await CameraEndpoints.BulkImportAsync(
            new BulkImportRequest("insert",
            [
                MakeRequest("SP-GOOD-1"),
                MakeRequest("SP-DUP"),      // fails: code already live — UniqueViolation
                MakeRequest("SP-GOOD-2"),
            ]),
            Repo, _fixture.DataSource, ContextFor(), CancellationToken.None);

        var body = ((Microsoft.AspNetCore.Http.HttpResults.Ok<BulkImportResult>)result.Result!).Value!;

        body.Created.ShouldBe(2, string.Join("; ", body.Rows.Select(r => $"{r.CameraCode}:{r.Status}:{r.Error}")));
        body.Failed.ShouldBe(1);
        body.Rows[1].Status.ShouldBe("error");
        body.Rows[1].Error.ShouldBe("duplicate camera_code");

        // The two good rows must actually be on disk after the batch commits — not just
        // reported as "created" in the in-memory result before a rollback discarded them.
        (await _fixture.ScalarAsync<long>(
            "SELECT count(*) FROM federation.cameras WHERE camera_code IN ('SP-GOOD-1', 'SP-GOOD-2')"))
            .ShouldBe(2);
    }

    [Fact]
    public async Task Insert_OneValidationFailureMidBatch_OthersStillCommit()
    {
        var badRow = MakeRequest("") with { Name = "" }; // fails TryBuild, never reaches a savepoint

        var result = await CameraEndpoints.BulkImportAsync(
            new BulkImportRequest("insert", [MakeRequest("SP-GOOD-3"), badRow]),
            Repo, _fixture.DataSource, ContextFor(), CancellationToken.None);

        var body = ((Microsoft.AspNetCore.Http.HttpResults.Ok<BulkImportResult>)result.Result!).Value!;

        body.Created.ShouldBe(1, string.Join("; ", body.Rows.Select(r => $"{r.CameraCode}:{r.Status}:{r.Error}")));
        body.Failed.ShouldBe(1);

        (await _fixture.ScalarAsync<long>(
            "SELECT count(*) FROM federation.cameras WHERE camera_code = 'SP-GOOD-3'")).ShouldBe(1);
    }

    /// <summary>
    /// dotnet-expert review of this PR flagged a real regression, now fixed:
    /// <c>FindLiveIdByCodeAsync</c> used to read on its own connection, outside the batch's now-
    /// shared transaction — invisible to an earlier row's still-uncommitted insert in the SAME
    /// batch. Two rows sharing a code in one `upsert` request would both take the "no existing
    /// row" branch and the second would hit a live `UniqueViolation` instead of correctly
    /// replacing the first. Fixed by reading through <c>work</c>'s own connection/transaction.
    /// </summary>
    [Fact]
    public async Task Upsert_TwoRowsSameCodeInOneBatch_SecondReplacesTheFirst()
    {
        var result = await CameraEndpoints.BulkImportAsync(
            new BulkImportRequest("upsert",
            [
                MakeRequest("SP-UPSERT-DUP") with { Name = "First" },
                MakeRequest("SP-UPSERT-DUP") with { Name = "Second" },
            ]),
            Repo, _fixture.DataSource, ContextFor(), CancellationToken.None);

        var body = ((Microsoft.AspNetCore.Http.HttpResults.Ok<BulkImportResult>)result.Result!).Value!;

        body.Created.ShouldBe(1, string.Join("; ", body.Rows.Select(r => $"{r.CameraCode}:{r.Status}:{r.Error}")));
        body.Updated.ShouldBe(1, string.Join("; ", body.Rows.Select(r => $"{r.CameraCode}:{r.Status}:{r.Error}")));
        body.Failed.ShouldBe(0);

        // Exactly one live row, carrying the second row's name — a genuine replace, not two
        // competing inserts.
        var liveCount = await _fixture.ScalarAsync<long>(
            "SELECT count(*) FROM federation.cameras WHERE camera_code = 'SP-UPSERT-DUP' AND deleted_at IS NULL");
        liveCount.ShouldBe(1);

        var name = await _fixture.ScalarAsync<string>(
            "SELECT name FROM federation.cameras WHERE camera_code = 'SP-UPSERT-DUP' AND deleted_at IS NULL");
        name.ShouldBe("Second");
    }

    /// <summary>
    /// Finding 10-M4: the row-isolation claim must also hold for <c>ForbiddenException</c> (a
    /// scoped caller whose caller's organization reach doesn't cover the row's target unit), not
    /// just <c>UniqueViolation</c> — a distinct, repository-thrown code path with its own
    /// rollback-to-savepoint branch.
    /// </summary>
    [Fact]
    public async Task Insert_OneOutOfScopeRowMidBatch_OthersStillCommit()
    {
        var result = await CameraEndpoints.BulkImportAsync(
            new BulkImportRequest("insert",
            [
                MakeRequest("SP-SCOPE-OK", PostgresFixture.AhmedabadCp),
                MakeRequest("SP-SCOPE-OUT", PostgresFixture.PoliceUnit), // parent — out of reach
            ]),
            Repo, _fixture.DataSource, ScopedContextFor(), CancellationToken.None);

        var body = ((Microsoft.AspNetCore.Http.HttpResults.Ok<BulkImportResult>)result.Result!).Value!;

        body.Created.ShouldBe(1, string.Join("; ", body.Rows.Select(r => $"{r.CameraCode}:{r.Status}:{r.Error}")));
        body.Failed.ShouldBe(1);
        body.Rows[1].Status.ShouldBe("error");
        body.Rows[1].Error.ShouldBe("organization_unit or geographic_area not in scope");

        (await _fixture.ScalarAsync<long>(
            "SELECT count(*) FROM federation.cameras WHERE camera_code = 'SP-SCOPE-OK'")).ShouldBe(1);
        (await _fixture.ScalarAsync<long>(
            "SELECT count(*) FROM federation.cameras WHERE camera_code = 'SP-SCOPE-OUT'")).ShouldBe(0);
    }
}
