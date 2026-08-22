using Shouldly;

namespace Trinetra.IntegrationTests;

/// <summary>
/// Verifies the storage behaviour the application depends on but cannot express in C#:
/// partition routing, retention by DROP PARTITION, and dedup semantics (architecture §5, §7).
/// </summary>
public sealed class SchemaTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly PostgresFixture _fixture;

    public SchemaTests(PostgresFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        await _fixture.ResetTargetsAsync();
        await _fixture.ExecuteAsync("""
            DELETE FROM federation.federation_event;
            SELECT federation.ensure_event_partitions(CURRENT_DATE, 2);
            """);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static readonly Guid SourceVms = Guid.Parse("00000000-0000-0000-0000-0000000000aa");

    private static string InsertEvent(string eventId, string? sourceEventId, string occurredAt) =>
        $"""
         INSERT INTO federation.federation_event
             (event_id, source_vms_id, source_event_id, camera_id, organization_unit_id,
              event_type, occurred_at)
         VALUES ('{eventId}', '{SourceVms}',
                 {(sourceEventId is null ? "NULL" : $"'{sourceEventId}'")},
                 'CAM-001', '{PostgresFixture.PoliceUnit}', 'AnprDetection', {occurredAt})
         ON CONFLICT DO NOTHING;
         """;

    [Fact]
    public async Task EnsureEventPartitions_IsIdempotent()
    {
        await _fixture.ExecuteAsync("SELECT federation.ensure_event_partitions(CURRENT_DATE, 5);");

        var created = await _fixture.ScalarAsync<int>(
            "SELECT federation.ensure_event_partitions(CURRENT_DATE, 5);");

        created.ShouldBe(0, "a second call must create nothing");
    }

    [Fact]
    public async Task Events_RouteToTheirDailyPartition()
    {
        await _fixture.ExecuteAsync(InsertEvent("EVT-ROUTE", "SRC-ROUTE", "now()"));

        var partition = await _fixture.ScalarAsync<string>("""
            SELECT tableoid::regclass::text FROM federation.federation_event
            WHERE event_id = 'EVT-ROUTE';
            """);

        // regclass::text schema-qualifies whenever search_path excludes the schema, which is
        // the case on a pooled connection, so match the suffix rather than the exact string.
        partition.ShouldNotBeNull();
        partition.ShouldEndWith($"federation_event_{DateTime.UtcNow:yyyyMMdd}");
    }

    [Fact]
    public async Task DuplicateDelivery_InsertsExactlyOneRow()
    {
        // Transport is at-least-once. Effectively-once depends entirely on this constraint
        // plus ON CONFLICT DO NOTHING — nothing may assume exactly-once delivery.
        await _fixture.ExecuteAsync(InsertEvent("EVT-DUP-1", "SRC-DUP", "'2026-08-19T10:32:15Z'"));
        await _fixture.ExecuteAsync(InsertEvent("EVT-DUP-2", "SRC-DUP", "'2026-08-19T10:32:15Z'"));

        var count = await _fixture.ScalarAsync<long>("""
            SELECT count(*) FROM federation.federation_event WHERE source_event_id = 'SRC-DUP';
            """);

        count.ShouldBe(1);
    }

    [Fact]
    public async Task EventsWithNullSourceEventId_DoNotCollide()
    {
        // Confirms the dedup index can be non-partial: unique indexes are NULLS DISTINCT by
        // default, so a vendor that supplies no event id still records every event.
        await _fixture.ExecuteAsync(InsertEvent("EVT-N1", null, "now()"));
        await _fixture.ExecuteAsync(InsertEvent("EVT-N2", null, "now()"));

        var count = await _fixture.ScalarAsync<long>("""
            SELECT count(*) FROM federation.federation_event WHERE source_event_id IS NULL;
            """);

        count.ShouldBe(2);
    }

    [Fact]
    public async Task DropEventPartitions_RemovesOnlyInRangePartitions_AndNeverTheDefault()
    {
        await _fixture.ExecuteAsync(
            "SELECT federation.ensure_event_partitions(CURRENT_DATE - 10, 3);");

        await _fixture.ExecuteAsync(
            "SELECT federation.drop_event_partitions_before(CURRENT_DATE - 8);");

        var defaultExists = await _fixture.ScalarAsync<bool>("""
            SELECT EXISTS (SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'federation' AND c.relname = 'federation_event_default');
            """);
        defaultExists.ShouldBeTrue("the DEFAULT partition must never be dropped");

        var oldRemains = await _fixture.ScalarAsync<bool>($"""
            SELECT EXISTS (SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'federation'
              AND c.relname = 'federation_event_{DateTime.UtcNow.AddDays(-10):yyyyMMdd}');
            """);
        oldRemains.ShouldBeFalse("partitions older than the cutoff must be dropped");
    }

    [Fact]
    public async Task CreatingAPartitionOverDefaultRows_FailsWithAnActionableMessage()
    {
        // Rows land in DEFAULT when a partition was missing at insert time. PostgreSQL then
        // refuses to attach an overlapping range, and the raw error does not say why.
        await _fixture.ExecuteAsync(InsertEvent("EVT-FAR", "SRC-FAR", "now() + interval '400 days'"));

        var ex = await Should.ThrowAsync<Npgsql.PostgresException>(
            _fixture.ExecuteAsync(
                "SELECT federation.ensure_event_partitions((CURRENT_DATE + 400)::date, 0);"));

        ex.MessageText.ShouldContain("federation_event_default holds rows in range");
        ex.MessageText.ShouldContain("Drain those rows");

        await _fixture.ExecuteAsync(
            "DELETE FROM federation.federation_event WHERE event_id = 'EVT-FAR';");
    }

    [Fact]
    public async Task PartitionHealthView_ReportsDefaultOccupancy()
    {
        var rowsInDefault = await _fixture.ScalarAsync<long>(
            "SELECT rows_in_default_partition FROM federation.event_partition_health;");

        rowsInDefault.ShouldBe(0, "a non-zero value is the alert condition for partition drift");
    }
}
