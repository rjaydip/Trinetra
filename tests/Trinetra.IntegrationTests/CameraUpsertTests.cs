using Shouldly;
using Trinetra.Federation.Core.Events;
using Trinetra.Federation.Core.Model;
using Trinetra.Federation.Storage;

namespace Trinetra.IntegrationTests;

/// <summary>
/// Merge semantics of <see cref="ConnectorStateStore.UpsertCamerasAsync"/>.
/// </summary>
/// <remarks>
/// This path runs on the status poll — every 30 seconds per target, fleet-wide — and it is a
/// binary COPY into a staging table followed by one <c>INSERT ... ON CONFLICT DO UPDATE</c>.
/// That is far cheaper than a statement per camera, but it moves the semantics out of C# and into
/// one SQL statement where a mistake is silent: a wrong COALESCE erases inventory a VMS merely
/// declined to repeat, and a duplicate channel aborts the whole poll rather than one row. Each
/// test below is one of those failures.
/// </remarks>
public sealed class CameraUpsertTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly PostgresFixture _fixture;

    private static readonly Guid TargetId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    public CameraUpsertTests(PostgresFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        await _fixture.ResetTargetsAsync();

        await _fixture.ExecuteAsync($"""
            INSERT INTO federation.connector_target
                (id, code, organization_unit_id, site_id, display_name, vendor, runtime_class,
                 endpoint, credential_reference, rate_limit_per_second, rate_limit_burst,
                 max_concurrent_requests, expected_camera_count)
            VALUES ('{TargetId}', 'TGT-1', '{PostgresFixture.PoliceUnit}',
                    '{PostgresFixture.SiteId}', 'Ring Road NVR',
                    'Hikvision'::federation.vendor_kind,
                    'Managed'::federation.runtime_class,
                    'https://10.0.0.1:443', 'vault://vms/1', 7.5, 15, 6, 128);
            """);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private ConnectorStateStore Store => new(_fixture.DataSource);

    private static FederatedCamera Camera(
        string nativeId,
        string? name = "Channel",
        string? vendorModel = "DS-2CD",
        string? firmware = "V5.5.0",
        bool? isRecording = true,
        HealthStatus health = HealthStatus.Healthy,
        DateTimeOffset? lastSeen = null,
        IReadOnlyList<string>? streams = null) => new()
        {
            TargetId = TargetId,
            NativeCameraId = nativeId,
            OrganizationUnitId = PostgresFixture.PoliceUnit,
            SiteId = PostgresFixture.SiteId,
            Name = name,
            VendorModel = vendorModel,
            Firmware = firmware,
            IsEnabled = true,
            IsRecording = isRecording,
            Health = health,
            LastSeen = lastSeen,
            StreamReferences = streams ?? ["rtsp://10.0.0.1/ch1"],
        };

    [Fact]
    public async Task UpsertCameras_InsertsWholeBatch_InOneRoundTrip()
    {
        var batch = Enumerable.Range(1, 128).Select(i => Camera($"ch{i}")).ToList();

        var affected = await Store.UpsertCamerasAsync(batch, CancellationToken.None);

        affected.ShouldBe(128);
        (await _fixture.ScalarAsync<long>(
            "SELECT count(*) FROM federation.federated_camera")).ShouldBe(128);
    }

    [Fact]
    public async Task UpsertCameras_SecondPoll_UpdatesRatherThanDuplicating()
    {
        await Store.UpsertCamerasAsync([Camera("ch1", name: "Old name")], CancellationToken.None);
        await Store.UpsertCamerasAsync([Camera("ch1", name: "New name")], CancellationToken.None);

        (await _fixture.ScalarAsync<long>(
            "SELECT count(*) FROM federation.federated_camera")).ShouldBe(1);
        (await _fixture.ScalarAsync<string>(
            "SELECT name FROM federation.federated_camera")).ShouldBe("New name");
    }

    /// <summary>
    /// A status poll reports health and little else. Those absent fields must not erase what the
    /// inventory poll established — that is what the COALESCEs in the merge are for.
    /// </summary>
    [Fact]
    public async Task UpsertCameras_NullsFromAStatusPoll_DoNotErasePriorInventory()
    {
        await Store.UpsertCamerasAsync(
            [Camera("ch1", name: "Gate camera", vendorModel: "DS-2CD2143", firmware: "V5.5.0")],
            CancellationToken.None);

        await Store.UpsertCamerasAsync(
            [Camera("ch1", name: null, vendorModel: null, firmware: null, isRecording: null,
                    health: HealthStatus.Degraded)],
            CancellationToken.None);

        (await _fixture.ScalarAsync<string>(
            "SELECT name FROM federation.federated_camera")).ShouldBe("Gate camera");
        (await _fixture.ScalarAsync<string>(
            "SELECT vendor_model FROM federation.federated_camera")).ShouldBe("DS-2CD2143");
        (await _fixture.ScalarAsync<string>(
            "SELECT firmware FROM federation.federated_camera")).ShouldBe("V5.5.0");
        (await _fixture.ScalarAsync<bool>(
            "SELECT is_recording FROM federation.federated_camera")).ShouldBeTrue();

        // Health is the one field a status poll is authoritative for, so it does overwrite.
        (await _fixture.ScalarAsync<string>(
            "SELECT health::text FROM federation.federated_camera")).ShouldBe("Degraded");
    }

    [Fact]
    public async Task UpsertCameras_EmptyStreamList_KeepsTheStreamsAlreadyKnown()
    {
        await Store.UpsertCamerasAsync(
            [Camera("ch1", streams: ["rtsp://10.0.0.1/ch1/main", "rtsp://10.0.0.1/ch1/sub"])],
            CancellationToken.None);

        await Store.UpsertCamerasAsync([Camera("ch1", streams: [])], CancellationToken.None);

        (await _fixture.ScalarAsync<int>(
            "SELECT cardinality(stream_references) FROM federation.federated_camera")).ShouldBe(2);
    }

    [Fact]
    public async Task UpsertCameras_NonEmptyStreamList_ReplacesTheStreamsKnown()
    {
        await Store.UpsertCamerasAsync(
            [Camera("ch1", streams: ["rtsp://old/one", "rtsp://old/two"])], CancellationToken.None);

        await Store.UpsertCamerasAsync(
            [Camera("ch1", streams: ["rtsp://new/only"])], CancellationToken.None);

        (await _fixture.ScalarAsync<string>(
            "SELECT stream_references[1] FROM federation.federated_camera"))
            .ShouldBe("rtsp://new/only");
        (await _fixture.ScalarAsync<int>(
            "SELECT cardinality(stream_references) FROM federation.federated_camera")).ShouldBe(1);
    }

    /// <summary>
    /// A VMS returning the same channel twice in one response is a real vendor behaviour. Under a
    /// statement-per-camera write the second row simply overwrote the first; under a single merge
    /// it raises "ON CONFLICT DO UPDATE command cannot affect row a second time" and takes the
    /// whole poll down with it, so the merge de-duplicates and keeps the last occurrence.
    /// </summary>
    [Fact]
    public async Task UpsertCameras_DuplicateChannelInOneBatch_KeepsTheLastAndDoesNotThrow()
    {
        var batch = new List<FederatedCamera>
        {
            Camera("ch1", name: "First"),
            Camera("ch2", name: "Other"),
            Camera("ch1", name: "Second"),
        };

        await Should.NotThrowAsync(() => Store.UpsertCamerasAsync(batch, CancellationToken.None));

        (await _fixture.ScalarAsync<long>(
            "SELECT count(*) FROM federation.federated_camera")).ShouldBe(2);
        (await _fixture.ScalarAsync<string>(
            "SELECT name FROM federation.federated_camera WHERE native_camera_id = 'ch1'"))
            .ShouldBe("Second");
    }

    /// <summary>
    /// The binary importer has no overload that turns a null into SQL NULL — a null reaches the
    /// wire as a typed default, which for a timestamp is year 1 rather than "never seen".
    /// </summary>
    [Fact]
    public async Task UpsertCameras_UnknownLastSeen_StaysNull()
    {
        await Store.UpsertCamerasAsync([Camera("ch1", lastSeen: null)], CancellationToken.None);

        (await _fixture.ScalarAsync<object>(
            "SELECT last_seen FROM federation.federated_camera")).ShouldBeNull();
    }

    [Fact]
    public async Task UpsertCameras_KnownLastSeen_RoundTripsTheInstant()
    {
        var seen = new DateTimeOffset(2026, 8, 23, 10, 30, 0, TimeSpan.FromHours(5.5));

        await Store.UpsertCamerasAsync([Camera("ch1", lastSeen: seen)], CancellationToken.None);

        (await _fixture.ScalarAsync<DateTime>(
            "SELECT last_seen FROM federation.federated_camera"))
            .ShouldBe(seen.UtcDateTime);
    }

    [Fact]
    public async Task UpsertCameras_EmptyBatch_TouchesNothing()
    {
        (await Store.UpsertCamerasAsync([], CancellationToken.None)).ShouldBe(0);
        (await _fixture.ScalarAsync<long>(
            "SELECT count(*) FROM federation.federated_camera")).ShouldBe(0);
    }
}
