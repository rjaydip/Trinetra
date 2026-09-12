using Shouldly;
using Trinetra.Federation.Core.Model;
using Trinetra.Federation.Storage;
using Trinetra.Federation.Storage.Repositories;

namespace Trinetra.IntegrationTests;

/// <summary>
/// Finding 10-M6: a retired camera's code is freed for reuse (v1.6.sql's unique index only
/// covers live rows), so two rows can share one `camera_code` once `includeRetired=true` widens
/// the result set. Keying the keyset cursor on `camera_code` alone made the pagination unstable
/// for tied rows — <c>ListAsync</c> now orders and pages on <c>(camera_code, id)</c> together.
/// </summary>
public sealed class CameraCursorTiebreakerTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly PostgresFixture _fixture;

    public CameraCursorTiebreakerTests(PostgresFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        await _fixture.ResetTargetsAsync();
        await _fixture.ExecuteAsync("""
            DELETE FROM federation.cameras
            WHERE camera_code IN ('CUR-DUP', 'CUR-TRIPLE', 'CUR-RETIRED-ONLY');
            """);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private CameraRepository Repo => new(_fixture.DataSource);

    // CallerContext.System(...) leaves UserId null — fine for a background caller, but
    // cameras.created_by is NOT NULL, so this test needs a real (if synthetic) user id.
    private static readonly CallerContext SystemCaller = new()
    {
        UserId = Guid.NewGuid(),
        IsSystem = true,
        Actor = "cursor-tiebreaker-test",
        Permissions = new HashSet<string>(StringComparer.Ordinal),
    };

    private static Camera MakeCamera(string code) => new()
    {
        Id = Guid.Empty,
        Code = code,
        Name = $"Camera {code}",
        OrganizationUnitId = PostgresFixture.PoliceUnit,
        GeographicAreaId = PostgresFixture.VillageId,
        CameraType = "FIXED",
        Latitude = 23.03,
        Longitude = 72.58,
    };

    private async Task<Guid> CreateAsync(Camera camera)
    {
        await using var work = await UnitOfWork.BeginAsync(_fixture.DataSource, CancellationToken.None);
        var id = await Repo.CreateAsync(camera, SystemCaller, work, CancellationToken.None);
        await work.CommitAsync(CancellationToken.None);
        return id;
    }

    private async Task RetireAsync(Guid id) => await _fixture.ExecuteAsync($"""
        UPDATE federation.cameras
        SET deleted_at = now(), maintenance_status = 'RETIRED'
        WHERE id = '{id}';
        """);

    [Fact]
    public async Task ListAsync_TwoRowsSharingACode_PagedOneAtATime_BothReturnedExactlyOnce()
    {
        var retiredId = await CreateAsync(MakeCamera("CUR-DUP"));
        await RetireAsync(retiredId);

        // The code is free again now that the only live holder is retired.
        var liveId = await CreateAsync(MakeCamera("CUR-DUP"));

        var seen = new List<Guid>();
        string? cursor = null;
        Guid? cursorId = null;

        // Page size 1 forces every tied row through its own cursor boundary — the exact
        // condition the bug needed. A handful of iterations is enough; bail out rather than
        // loop forever if the fix regresses and the cursor gets stuck.
        for (var i = 0; i < 10; i++)
        {
            var page = await Repo.ListAsync(
                new CameraQuery(1, cursor, cursorId, IncludeRetired: true,
                    OrganizationUnitId: null, GeographicAreaId: null, CameraType: null,
                    OperationalStatus: null, ConnectivityStatus: null, MaintenanceStatus: null,
                    Query: "CUR-DUP", Bbox: null),
                SystemCaller, CancellationToken.None);

            if (page.Count == 0)
            {
                break;
            }

            seen.Add(page[0].Id);
            cursor = page[0].Code;
            cursorId = page[0].Id;
        }

        seen.ShouldBe([retiredId, liveId], ignoreOrder: true,
            "both rows sharing the code must appear exactly once across the paged listing");
    }

    /// <summary>The three-way case: (code, id) stays total no matter how many rows tie on code.</summary>
    [Fact]
    public async Task ListAsync_ThreeRowsSharingACode_PagedOneAtATime_AllReturnedExactlyOnce()
    {
        var retired1 = await CreateAsync(MakeCamera("CUR-TRIPLE"));
        await RetireAsync(retired1);
        var retired2 = await CreateAsync(MakeCamera("CUR-TRIPLE"));
        await RetireAsync(retired2);
        var liveId = await CreateAsync(MakeCamera("CUR-TRIPLE"));

        var seen = new List<Guid>();
        string? cursor = null;
        Guid? cursorId = null;

        for (var i = 0; i < 10; i++)
        {
            var page = await Repo.ListAsync(
                new CameraQuery(1, cursor, cursorId, IncludeRetired: true,
                    OrganizationUnitId: null, GeographicAreaId: null, CameraType: null,
                    OperationalStatus: null, ConnectivityStatus: null, MaintenanceStatus: null,
                    Query: "CUR-TRIPLE", Bbox: null),
                SystemCaller, CancellationToken.None);

            if (page.Count == 0)
            {
                break;
            }

            seen.Add(page[0].Id);
            cursor = page[0].Code;
            cursorId = page[0].Id;
        }

        seen.ShouldBe([retired1, retired2, liveId], ignoreOrder: true,
            "all three rows sharing the code must appear exactly once across the paged listing");
    }

    /// <summary>The tiebreaker must not loosen the existing retired-filter predicate.</summary>
    [Fact]
    public async Task ListAsync_IncludeRetiredFalse_RetiredDuplicateNeverAppears()
    {
        var retiredId = await CreateAsync(MakeCamera("CUR-RETIRED-ONLY"));
        await RetireAsync(retiredId);
        var liveId = await CreateAsync(MakeCamera("CUR-RETIRED-ONLY"));

        var page = await Repo.ListAsync(
            new CameraQuery(10, Cursor: null, CursorId: null, IncludeRetired: false,
                OrganizationUnitId: null, GeographicAreaId: null, CameraType: null,
                OperationalStatus: null, ConnectivityStatus: null, MaintenanceStatus: null,
                Query: "CUR-RETIRED-ONLY", Bbox: null),
            SystemCaller, CancellationToken.None);

        page.Select(c => c.Id).ShouldBe([liveId]);
    }
}
