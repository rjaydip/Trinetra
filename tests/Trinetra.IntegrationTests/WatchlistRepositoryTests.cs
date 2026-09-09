using Shouldly;
using Trinetra.Federation.Core.Model;
using Trinetra.Federation.Storage;
using Trinetra.Federation.Storage.Repositories;

namespace Trinetra.IntegrationTests;

/// <summary>
/// <see cref="WatchlistRepository"/> — the deactivate and acknowledge paths return enough for a
/// meaningful audit row (findings 16-M2 / 16-M3): which plate, under which org, and — for an
/// acknowledgement — whether it was a real transition or a no-op re-ack.
/// </summary>
public sealed class WatchlistRepositoryTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly PostgresFixture _fixture;

    public WatchlistRepositoryTests(PostgresFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        await _fixture.ResetTargetsAsync();
        await _fixture.ExecuteAsync("""
            DELETE FROM federation.watchlist_alert;
            DELETE FROM federation.watchlist_entry;
            DELETE FROM federation.detection_event WHERE event_id LIKE 'wlr-%';
            DELETE FROM federation.connector_target WHERE code = 'WLR-TGT';
            SELECT federation.ensure_event_partitions(CURRENT_DATE, 2);
            """);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private WatchlistRepository Repo => new(_fixture.DataSource);
    private static CallerContext System() => CallerContext.System("watchlist-repo-test");

    private async Task<Guid> SeedEntryAsync(string plate = "MH12AB1234")
    {
        await using var work = await UnitOfWork.BeginAsync(_fixture.DataSource, CancellationToken.None);
        var id = await Repo.CreateAsync(new WatchlistEntry
        {
            OrganizationUnitId = PostgresFixture.PoliceUnit,
            PlateNumberNormalized = plate,
            Reason = "test",
            Severity = "High",
        }, System(), work, CancellationToken.None);
        await work.CommitAsync(CancellationToken.None);
        return id;
    }

    [Fact]
    public async Task Deactivate_ReturnsTheEntry_ForTheAuditRow()
    {
        var id = await SeedEntryAsync("GJ01XY9999");

        await using var work = await UnitOfWork.BeginAsync(_fixture.DataSource, CancellationToken.None);
        var entry = await Repo.DeactivateAsync(id, System(), work, CancellationToken.None);
        await work.CommitAsync(CancellationToken.None);

        entry.ShouldNotBeNull();
        entry!.PlateNumberNormalized.ShouldBe("GJ01XY9999");
        entry.OrganizationUnitId.ShouldBe(PostgresFixture.PoliceUnit);
    }

    [Fact]
    public async Task Deactivate_UnknownId_ReturnsNull()
    {
        await using var work = await UnitOfWork.BeginAsync(_fixture.DataSource, CancellationToken.None);
        (await Repo.DeactivateAsync(Guid.NewGuid(), System(), work, CancellationToken.None))
            .ShouldBeNull();
    }

    [Fact]
    public async Task Acknowledge_FirstTime_ReturnsOrgAndNotAlreadyAcknowledged_ThenIdempotent()
    {
        var entryId = await SeedEntryAsync();
        var alertId = Guid.NewGuid();

        // Seed the chain the alert FK needs: a target, a detection_event, then the alert.
        await _fixture.ExecuteAsync($"""
            INSERT INTO federation.connector_target
                (id, code, organization_unit_id, display_name, vendor, endpoint, credential_reference)
            VALUES ('c9999999-0000-4000-8000-00000000e001', 'WLR-TGT', '{PostgresFixture.PoliceUnit}',
                    'Watchlist repo test target', 'Onvif'::federation.vendor_kind,
                    'http://10.0.0.1', 'vault://wlr');

            INSERT INTO federation.detection_event
                (event_id, occurred_at, target_id, native_camera_id, camera_id,
                 organization_unit_id, event_type, confidence)
            VALUES ('wlr-1', now(), 'c9999999-0000-4000-8000-00000000e001', 'ch1', NULL,
                    '{PostgresFixture.PoliceUnit}', 'AnprDetection', 0.9);

            INSERT INTO federation.watchlist_alert
                (id, watchlist_entry_id, detection_event_id, detection_occurred_at)
            SELECT '{alertId}', '{entryId}', 'wlr-1', occurred_at
            FROM federation.detection_event WHERE event_id = 'wlr-1';
            """);

        await using (var work = await UnitOfWork.BeginAsync(_fixture.DataSource, CancellationToken.None))
        {
            var first = await Repo.AcknowledgeAlertAsync(alertId, System(), work, CancellationToken.None);
            first.ShouldNotBeNull();
            first!.AlreadyAcknowledged.ShouldBeFalse();
            first.OrganizationUnitId.ShouldBe(PostgresFixture.PoliceUnit);
            await work.CommitAsync(CancellationToken.None);
        }

        await using (var work = await UnitOfWork.BeginAsync(_fixture.DataSource, CancellationToken.None))
        {
            var second = await Repo.AcknowledgeAlertAsync(alertId, System(), work, CancellationToken.None);
            second.ShouldNotBeNull();
            second!.AlreadyAcknowledged.ShouldBeTrue();   // no-op re-ack — endpoint writes no audit row
        }
    }

    [Fact]
    public async Task Acknowledge_UnknownAlert_ReturnsNull()
    {
        await using var work = await UnitOfWork.BeginAsync(_fixture.DataSource, CancellationToken.None);
        (await Repo.AcknowledgeAlertAsync(Guid.NewGuid(), System(), work, CancellationToken.None))
            .ShouldBeNull();
    }
}
