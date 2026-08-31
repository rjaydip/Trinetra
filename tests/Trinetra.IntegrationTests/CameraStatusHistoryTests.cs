using Shouldly;
using Trinetra.Federation.Core.Events;
using Trinetra.Federation.Core.Model;
using Trinetra.Federation.Storage;

namespace Trinetra.IntegrationTests;

/// <summary>
/// The <c>camera_status_history</c> trigger, exercised through the real upsert path.
/// </summary>
/// <remarks>
/// The whole design rests on one property: an unchanged status must write nothing. Every camera
/// is updated every 30 seconds, so a trigger that logged each of those would produce ~230M
/// rows/day at the 80,000 camera design point — the thing transitions exist to avoid. The first
/// test below is that property; the rest are the transitions it must still catch.
/// </remarks>
public sealed class CameraStatusHistoryTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly PostgresFixture _fixture;

    private static readonly Guid TargetId = Guid.Parse("00000000-0000-0000-0000-0000000000a1");

    public CameraStatusHistoryTests(PostgresFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        await _fixture.ResetTargetsAsync();

        await _fixture.ExecuteAsync($"""
            INSERT INTO federation.connector_target
                (id, code, organization_unit_id, site_id, display_name, vendor, runtime_class,
                 endpoint, credential_reference, rate_limit_per_second, rate_limit_burst,
                 max_concurrent_requests, expected_camera_count)
            VALUES ('{TargetId}', 'TGT-H', '{PostgresFixture.PoliceUnit}',
                    '{PostgresFixture.SiteId}', 'History NVR',
                    'Hikvision'::federation.vendor_kind,
                    'Managed'::federation.runtime_class,
                    'https://10.0.0.9:443', 'vault://vms/9', 7.5, 15, 6, 8);
            """);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private ConnectorStateStore Store => new(_fixture.DataSource);

    private static FederatedCamera Camera(
        HealthStatus health = HealthStatus.Healthy,
        bool isEnabled = true,
        bool? isRecording = true) => new()
        {
            TargetId = TargetId,
            NativeCameraId = "ch1",
            OrganizationUnitId = PostgresFixture.PoliceUnit,
            SiteId = PostgresFixture.SiteId,
            Name = "Gate camera",
            IsEnabled = isEnabled,
            IsRecording = isRecording,
            Health = health,
            StreamReferences = ["rtsp://10.0.0.9/ch1"],
        };

    private Task<long> HistoryCountAsync() => _fixture.ScalarAsync<long>(
        "SELECT count(*) FROM federation.camera_status_history")!;

    /// <summary>
    /// The property the whole table depends on. If this fails, the status poll writes a row per
    /// camera per 30 seconds and the design point is unreachable.
    /// </summary>
    [Fact]
    public async Task RepeatedIdenticalPolls_WriteNoHistory()
    {
        await Store.UpsertCamerasAsync([Camera()], CancellationToken.None);

        for (var i = 0; i < 5; i++)
        {
            await Store.UpsertCamerasAsync([Camera()], CancellationToken.None);
        }

        // One row: the first observation. The five identical polls after it add nothing.
        (await HistoryCountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task FirstObservation_RecordsNullPreviousValues()
    {
        await Store.UpsertCamerasAsync([Camera()], CancellationToken.None);

        (await HistoryCountAsync()).ShouldBe(1);
        (await _fixture.ScalarAsync<object>(
            "SELECT previous_health FROM federation.camera_status_history")).ShouldBeNull();
        (await _fixture.ScalarAsync<string>(
            "SELECT health::text FROM federation.camera_status_history")).ShouldBe("Healthy");
    }

    [Fact]
    public async Task HealthChange_RecordsBothSidesOfTheTransition()
    {
        await Store.UpsertCamerasAsync([Camera(HealthStatus.Healthy)], CancellationToken.None);
        await Store.UpsertCamerasAsync(
            [Camera(HealthStatus.Unreachable)], CancellationToken.None);

        (await HistoryCountAsync()).ShouldBe(2);

        (await _fixture.ScalarAsync<string>("""
            SELECT previous_health::text FROM federation.camera_status_history
            ORDER BY changed_at DESC LIMIT 1
            """)).ShouldBe("Healthy");
        (await _fixture.ScalarAsync<string>("""
            SELECT health::text FROM federation.camera_status_history
            ORDER BY changed_at DESC LIMIT 1
            """)).ShouldBe("Unreachable");
    }

    /// <summary>
    /// A camera that stops reporting whether it is recording goes true → NULL. <c>&lt;&gt;</c>
    /// against NULL is NULL, so a trigger written with it would miss this entirely — which is why
    /// the WHEN clause uses IS DISTINCT FROM.
    /// </summary>
    [Fact]
    public async Task RecordingBecomingUnknown_IsATransition()
    {
        await Store.UpsertCamerasAsync([Camera(isRecording: true)], CancellationToken.None);
        await Store.UpsertCamerasAsync([Camera(isRecording: null)], CancellationToken.None);

        (await HistoryCountAsync()).ShouldBe(2);
        (await _fixture.ScalarAsync<bool?>("""
            SELECT previous_recording FROM federation.camera_status_history
            ORDER BY changed_at DESC LIMIT 1
            """)).ShouldBe(true);
        (await _fixture.ScalarAsync<object>("""
            SELECT is_recording FROM federation.camera_status_history
            ORDER BY changed_at DESC LIMIT 1
            """)).ShouldBeNull();
    }

    [Fact]
    public async Task DisablingACamera_IsATransition()
    {
        await Store.UpsertCamerasAsync([Camera(isEnabled: true)], CancellationToken.None);
        await Store.UpsertCamerasAsync([Camera(isEnabled: false)], CancellationToken.None);

        (await HistoryCountAsync()).ShouldBe(2);
        (await _fixture.ScalarAsync<bool>("""
            SELECT previous_enabled FROM federation.camera_status_history
            ORDER BY changed_at DESC LIMIT 1
            """)).ShouldBeTrue();
        (await _fixture.ScalarAsync<bool>("""
            SELECT is_enabled FROM federation.camera_status_history
            ORDER BY changed_at DESC LIMIT 1
            """)).ShouldBeFalse();
    }

    /// <summary>
    /// A rename is inventory, not status. It must not open a history row, or every metadata
    /// correction would look like a status event on the timeline.
    /// </summary>
    [Fact]
    public async Task NonStatusFieldChange_WritesNoHistory()
    {
        await Store.UpsertCamerasAsync([Camera()], CancellationToken.None);

        var renamed = Camera() with { Name = "Gate camera (north)" };
        await Store.UpsertCamerasAsync([renamed], CancellationToken.None);

        (await HistoryCountAsync()).ShouldBe(1);
        (await _fixture.ScalarAsync<string>(
            "SELECT name FROM federation.federated_camera")).ShouldBe("Gate camera (north)");
    }

    /// <summary>
    /// status_changed_at is what keeps "Healthy since March" answerable after retention has
    /// dropped the transition that established it, so it must track status and nothing else.
    /// </summary>
    [Fact]
    public async Task StatusChangedAt_MovesOnStatusChangeOnly()
    {
        await Store.UpsertCamerasAsync([Camera(HealthStatus.Healthy)], CancellationToken.None);

        var afterFirst = await _fixture.ScalarAsync<DateTime>(
            "SELECT status_changed_at FROM federation.federated_camera");

        // A poll that changes nothing about status must not move the stamp.
        await Store.UpsertCamerasAsync([Camera(HealthStatus.Healthy)], CancellationToken.None);
        (await _fixture.ScalarAsync<DateTime>(
            "SELECT status_changed_at FROM federation.federated_camera")).ShouldBe(afterFirst);

        await Store.UpsertCamerasAsync([Camera(HealthStatus.Degraded)], CancellationToken.None);
        (await _fixture.ScalarAsync<DateTime>(
            "SELECT status_changed_at FROM federation.federated_camera"))
            .ShouldBeGreaterThan(afterFirst);
    }

    [Fact]
    public async Task History_LandsInADatedPartition_NotTheDefault()
    {
        await Store.UpsertCamerasAsync([Camera()], CancellationToken.None);

        // The default partition exists to catch rows whose real partition is missing. A row in it
        // means partition maintenance has not run, and range pruning silently stops working.
        (await _fixture.ScalarAsync<long>(
            "SELECT count(*) FROM ONLY federation.camera_status_history_default")).ShouldBe(0);
        (await HistoryCountAsync()).ShouldBe(1);
    }
}
