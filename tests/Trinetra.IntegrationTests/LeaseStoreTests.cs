using Npgsql;
using Shouldly;
using Trinetra.Federation.Core.Model;
using Trinetra.Federation.Storage;

namespace Trinetra.IntegrationTests;

/// <summary>
/// Verifies the lease-based work assignment that replaces Kubernetes on bare metal
/// (architecture §4). These are the tests that decide whether the scale design actually holds.
/// </summary>
public sealed class LeaseStoreTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private readonly LeaseStore _leases;

    public LeaseStoreTests(PostgresFixture fixture)
    {
        _fixture = fixture;
        _leases = new LeaseStore(fixture.DataSource);
    }

    public Task InitializeAsync() => _fixture.ResetTargetsAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task SeedTargetsAsync(int count, VendorKind vendor = VendorKind.Onvif,
        RuntimeClass runtimeClass = RuntimeClass.Managed)
    {
        // Ids are derived deterministically from the index so a test can name a specific target
        // without querying for it first.
        await _fixture.ExecuteAsync($"""
            INSERT INTO federation.connector_target
                (id, code, organization_unit_id, geographic_area_id, display_name, vendor, runtime_class,
                 endpoint, credential_reference, rate_limit_per_second, rate_limit_burst,
                 max_concurrent_requests, expected_camera_count)
            SELECT ('00000000-0000-0000-0000-' || lpad(i::text, 12, '0'))::uuid,
                   'TGT-' || i,
                   '{PostgresFixture.PoliceUnit}'::uuid,
                   '{PostgresFixture.VillageId}'::uuid,
                   'Site ' || i,
                   '{vendor}'::federation.vendor_kind,
                   '{runtimeClass}'::federation.runtime_class,
                   'https://10.0.0.' || i || ':443', 'vault://vms/' || i,
                   7.5, 15, 6, 128
            FROM generate_series(1, {count}) i;
            """);
    }

    private Task<IReadOnlyList<ConnectorTarget>> ClaimAsync(string workerId, int maxTargets, TimeSpan ttl) =>
        _leases.ClaimAsync(workerId, maxTargets, ttl, RuntimeClass.Managed, vendorFilter: null, CancellationToken.None);

    /// <summary>The deterministic id of seeded target N.</summary>
    private static Guid TargetId(int index) =>
        Guid.Parse($"00000000-0000-0000-0000-{index:D12}");

    [Fact]
    public async Task Claim_MapsEveryColumn_IntoTheDomainRecord()
    {
        // Guards the Dapper snake_case defect: unmapped columns fail silently, leaving a worker
        // holding blank targets that it then polls for nothing.
        await SeedTargetsAsync(1);

        var claimed = await ClaimAsync("worker-1", 10, TimeSpan.FromSeconds(45));

        var target = claimed.ShouldHaveSingleItem();
        target.Code.ShouldBe("TGT-1");
        target.Id.ShouldBe(TargetId(1));
        target.OrganizationUnitId.ShouldBe(PostgresFixture.PoliceUnit);
        target.DisplayName.ShouldBe("Site 1");
        target.Vendor.ShouldBe(VendorKind.Onvif);
        target.RuntimeClass.ShouldBe(RuntimeClass.Managed);
        target.Endpoint.ShouldBe("https://10.0.0.1:443");
        target.CredentialReference.ShouldBe("vault://vms/1");
        target.State.ShouldBe(TargetState.Active);
        target.RateLimitPerSecond.ShouldBe(7.5);
        target.RateLimitBurst.ShouldBe(15);
        target.MaxConcurrentRequests.ShouldBe(6);
        target.ExpectedCameraCount.ShouldBe(128);
        target.VerifyTls.ShouldBeTrue();
    }

    [Fact]
    public async Task ConcurrentClaims_NeverAssignTheSameTargetTwice()
    {
        // THE critical test. A fleet restart has every worker claiming simultaneously; that is
        // the normal case, not an edge case. Two workers owning one target means double the
        // request rate against the vendor and duplicated events. SKIP LOCKED is what prevents it.
        await SeedTargetsAsync(120);

        var workers = Enumerable.Range(1, 10).Select(i => $"worker-{i}").ToArray();

        var results = await Task.WhenAll(workers.Select(w =>
            ClaimAsync(w, 20, TimeSpan.FromSeconds(45))));

        var allClaimed = results.SelectMany(r => r.Select(t => t.Code)).ToList();

        allClaimed.Count.ShouldBe(allClaimed.Distinct().Count(),
            "a target was claimed by more than one worker");
        allClaimed.Count.ShouldBe(120, "every available target should have been claimed");

        // And the database agrees: no target is left with a null or duplicated owner.
        var unleased = await _fixture.ScalarAsync<long>(
            "SELECT count(*) FROM federation.connector_target WHERE leased_by IS NULL;");
        unleased.ShouldBe(0);
    }

    [Fact]
    public async Task Claim_RespectsMaxTargets()
    {
        // Bounds blast radius: without the cap, whichever worker starts first claims the whole
        // estate and its failure becomes a total outage rather than a partial one.
        await SeedTargetsAsync(50);

        var claimed = await ClaimAsync("worker-1", 20, TimeSpan.FromSeconds(45));

        claimed.Count.ShouldBe(20);
    }

    [Fact]
    public async Task Claim_SkipsTargetsAlreadyLeasedByAnotherWorker()
    {
        await SeedTargetsAsync(10);
        await ClaimAsync("worker-1", 10, TimeSpan.FromSeconds(45));

        var second = await ClaimAsync("worker-2", 10, TimeSpan.FromSeconds(45));

        second.ShouldBeEmpty();
    }

    [Fact]
    public async Task Claim_ReclaimsTargetsWhoseLeaseHasExpired()
    {
        // The crash-recovery path: a dead worker's targets must return to the pool.
        await SeedTargetsAsync(5);
        await ClaimAsync("dead-worker", 5, TimeSpan.FromMilliseconds(1));

        await _fixture.ExecuteAsync(
            "UPDATE federation.connector_target SET lease_expires_at = now() - interval '1 minute';");

        var reclaimed = await ClaimAsync("live-worker", 5, TimeSpan.FromSeconds(45));

        reclaimed.Count.ShouldBe(5);
    }

    [Fact]
    public async Task Claim_ExcludesQuarantinedAndDisabledTargets()
    {
        await SeedTargetsAsync(6);
        await _fixture.ExecuteAsync("""
            UPDATE federation.connector_target SET state = 'Quarantined' WHERE code = 'TGT-1';
            UPDATE federation.connector_target SET state = 'Disabled'    WHERE code = 'TGT-2';
            """);

        var claimed = await ClaimAsync("worker-1", 10, TimeSpan.FromSeconds(45));

        claimed.Select(t => t.Code).ShouldNotContain("TGT-1");
        claimed.Select(t => t.Code).ShouldNotContain("TGT-2");
        claimed.Count.ShouldBe(4);
    }

    [Fact]
    public async Task Claim_HonoursVendorFilter_SoNativeWorkersStayPinned()
    {
        await SeedTargetsAsync(4);
        await _fixture.ExecuteAsync(
            "UPDATE federation.connector_target SET vendor = 'DahuaCgi' WHERE code IN ('TGT-1','TGT-2');");

        var claimed = await _leases.ClaimAsync(
            "worker-dahua", 10, TimeSpan.FromSeconds(45),
            RuntimeClass.Managed, [VendorKind.DahuaCgi], CancellationToken.None);

        claimed.Count.ShouldBe(2);
        claimed.ShouldAllBe(t => t.Vendor == VendorKind.DahuaCgi);
    }

    [Fact]
    public async Task Claim_HonoursRuntimeClass_SoNativeAndManagedNeverShareAProcess()
    {
        await SeedTargetsAsync(3, runtimeClass: RuntimeClass.Managed);
        await _fixture.ExecuteAsync(
            "UPDATE federation.connector_target SET runtime_class = 'Native' WHERE code = 'TGT-1';");

        var managed = await ClaimAsync("worker-managed", 10, TimeSpan.FromSeconds(45));

        managed.Count.ShouldBe(2);
        managed.Select(t => t.Code).ShouldNotContain("TGT-1");
    }

    [Fact]
    public async Task Renew_ExtendsOnlyThisWorkersLeases()
    {
        await SeedTargetsAsync(6);
        await ClaimAsync("worker-1", 3, TimeSpan.FromSeconds(45));
        await ClaimAsync("worker-2", 3, TimeSpan.FromSeconds(45));

        var held = await _leases.RenewAsync("worker-1", TimeSpan.FromSeconds(45), CancellationToken.None);

        held.Count.ShouldBe(3);
    }

    [Fact]
    public async Task Renew_DoesNotStealBackAnExpiredLease()
    {
        // A worker that stalled past its TTL must not reclaim a target another worker now owns
        // — that would produce two owners polling one VMS, which is invisible in metrics.
        await SeedTargetsAsync(2);
        await ClaimAsync("slow-worker", 2, TimeSpan.FromSeconds(45));
        await _fixture.ExecuteAsync(
            "UPDATE federation.connector_target SET lease_expires_at = now() - interval '1 minute';");

        var held = await _leases.RenewAsync("slow-worker", TimeSpan.FromSeconds(45), CancellationToken.None);

        held.ShouldBeEmpty("an expired lease must not be renewable");
    }

    [Fact]
    public async Task ReleaseAll_ReturnsTargetsToThePoolImmediately()
    {
        // Makes a rolling restart cost the restart itself rather than a full TTL per worker.
        await SeedTargetsAsync(5);
        await ClaimAsync("worker-1", 5, TimeSpan.FromSeconds(45));

        var released = await _leases.ReleaseAllAsync("worker-1", CancellationToken.None);
        released.ShouldBe(5);

        var reclaimed = await ClaimAsync("worker-2", 5, TimeSpan.FromSeconds(45));
        reclaimed.Count.ShouldBe(5);
    }

    [Fact]
    public async Task Quarantine_RemovesTheTargetFromCirculation()
    {
        await SeedTargetsAsync(3);
        await ClaimAsync("worker-1", 3, TimeSpan.FromSeconds(45));

        await _leases.QuarantineAsync(TargetId(1), "credentials rejected", CancellationToken.None);
        await _leases.ReleaseAllAsync("worker-1", CancellationToken.None);

        var claimed = await ClaimAsync("worker-2", 10, TimeSpan.FromSeconds(45));

        claimed.Select(t => t.Code).ShouldNotContain("TGT-1");
        var state = await _fixture.ScalarAsync<string>(
            "SELECT state::text FROM federation.connector_target WHERE code = 'TGT-1';");
        state.ShouldBe("Quarantined");
    }
}
