using Dapper;
using Npgsql;
using NpgsqlTypes;
using Trinetra.Federation.Core.Events;

namespace Trinetra.Federation.Storage;

/// <summary>
/// Persists normalised events into the time-partitioned event table.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every write is idempotent.</b> Transport is at-least-once — VMS reconnects, subscription
/// replays and lease handovers all resend — so inserts collide on
/// <c>(source_vms, source_event_id, occurred_at)</c> and are dropped. At-least-once delivery plus
/// idempotent sinks is what makes the system effectively-once; nothing here may assume
/// exactly-once.
/// </para>
/// <para>
/// Writes go through <see cref="NpgsqlBinaryImporter"/> into a staging table rather than as
/// individual inserts. At the medium design point this table takes 1-5k rows/second sustained,
/// which row-by-row inserts cannot absorb without becoming the bottleneck the event bus exists
/// to prevent.
/// </para>
/// </remarks>
public sealed class EventStore
{
    private readonly NpgsqlDataSource _dataSource;

    public EventStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    /// <summary>
    /// Writes a batch, ignoring duplicates. Returns the number of rows actually inserted.
    /// </summary>
    /// <remarks>
    /// A returned count below <paramref name="events"/>'s length is normal and means duplicates
    /// were suppressed — it is a useful signal of redelivery, not an error.
    /// </remarks>
    public async Task<int> AppendAsync(
        IReadOnlyCollection<NormalisedEvent> events, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(events);

        if (events.Count == 0)
        {
            return 0;
        }

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        // An explicit transaction is required, not merely tidy. ON COMMIT DROP is only
        // meaningful inside a transaction block: without one, each statement commits on its own
        // and the staging table is dropped the instant it is created. It also makes the COPY and
        // the merge atomic, so a failure can never leave rows staged but unmerged.
        await using var transaction = await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // A temporary staging table lets the batch arrive via binary COPY (fast) and then land
        // through a single INSERT ... ON CONFLICT (idempotent). COPY alone cannot express
        // conflict handling, and INSERT alone cannot match COPY's throughput.
        await using (var create = new NpgsqlCommand("""
            CREATE TEMP TABLE staged_event (
                LIKE federation.federation_event INCLUDING DEFAULTS
            ) ON COMMIT DROP;
            """, connection, transaction))
        {
            await create.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var writer = await connection.BeginBinaryImportAsync("""
            COPY staged_event (
                event_id, source_vms_id, source_event_id, camera_id,
                organization_unit_id, geographic_area_id,
                event_type, vendor_event_type, occurred_at, severity,
                object_reference, confidence, raw_reference, delivery_mode, trace_id
            ) FROM STDIN (FORMAT BINARY)
            """, cancellationToken).ConfigureAwait(false))
        {
            foreach (var e in events)
            {
                await writer.StartRowAsync(cancellationToken).ConfigureAwait(false);
                await writer.WriteAsync(e.EventId, NpgsqlDbType.Text, cancellationToken).ConfigureAwait(false);
                await writer.WriteAsync(e.SourceVmsId, NpgsqlDbType.Uuid, cancellationToken).ConfigureAwait(false);
                await WriteNullableAsync(writer, e.SourceEventId, cancellationToken).ConfigureAwait(false);
                await writer.WriteAsync(e.CameraId, NpgsqlDbType.Text, cancellationToken).ConfigureAwait(false);
                await writer.WriteAsync(e.OrganizationUnitId, NpgsqlDbType.Uuid, cancellationToken).ConfigureAwait(false);

                if (e.GeographicAreaId is { } areaId)
                {
                    await writer.WriteAsync(areaId, NpgsqlDbType.Uuid, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await writer.WriteNullAsync(cancellationToken).ConfigureAwait(false);
                }

                await writer.WriteAsync(e.EventType.ToString(), NpgsqlDbType.Text, cancellationToken).ConfigureAwait(false);
                await WriteNullableAsync(writer, e.VendorEventType, cancellationToken).ConfigureAwait(false);
                await writer.WriteAsync(e.Timestamp, NpgsqlDbType.TimestampTz, cancellationToken).ConfigureAwait(false);
                await writer.WriteAsync(e.Severity.ToString(), NpgsqlDbType.Text, cancellationToken).ConfigureAwait(false);
                await WriteNullableAsync(writer, e.ObjectReference, cancellationToken).ConfigureAwait(false);

                if (e.Confidence is { } confidence)
                {
                    await writer.WriteAsync(confidence, NpgsqlDbType.Double, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await writer.WriteNullAsync(cancellationToken).ConfigureAwait(false);
                }

                await WriteNullableAsync(writer, e.RawReference, cancellationToken).ConfigureAwait(false);
                await writer.WriteAsync(e.DeliveryMode.ToString(), NpgsqlDbType.Text, cancellationToken).ConfigureAwait(false);
                await writer.WriteNullAsync(cancellationToken).ConfigureAwait(false); // trace_id
            }

            await writer.CompleteAsync(cancellationToken).ConfigureAwait(false);
        }

        const string merge = """
            INSERT INTO federation.federation_event (
                event_id, source_vms_id, source_event_id, camera_id,
                organization_unit_id, geographic_area_id,
                event_type, vendor_event_type, occurred_at, severity,
                object_reference, confidence, raw_reference, delivery_mode, trace_id
            )
            SELECT event_id, source_vms_id, source_event_id, camera_id,
                   organization_unit_id, geographic_area_id,
                   event_type, vendor_event_type, occurred_at, severity,
                   object_reference, confidence, raw_reference, delivery_mode, trace_id
            FROM staged_event
            ON CONFLICT DO NOTHING;
            """;

        var inserted = await connection.ExecuteAsync(new CommandDefinition(
            merge, transaction: transaction, cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return inserted;
    }

    private static async Task WriteNullableAsync(
        NpgsqlBinaryImporter writer, string? value, CancellationToken cancellationToken)
    {
        if (value is null)
        {
            await writer.WriteNullAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await writer.WriteAsync(value, NpgsqlDbType.Text, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Creates event partitions ahead of need.
    /// </summary>
    /// <remarks>
    /// Called at worker startup and on a daily schedule. If a partition is missing at insert
    /// time rows silently land in the DEFAULT partition, which is unindexed for range scans and
    /// then blocks the correct partition from ever being created.
    /// </remarks>
    public async Task<int> EnsurePartitionsAsync(
        int daysAhead, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        return await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT federation.ensure_event_partitions(CURRENT_DATE, @DaysAhead);",
            new { DaysAhead = daysAhead }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }
}
