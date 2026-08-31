using System.Text.Json;
using Dapper;
using Npgsql;
using NpgsqlTypes;
using Trinetra.Federation.Core.Capabilities;
using Trinetra.Federation.Core.Model;

namespace Trinetra.Federation.Storage;

/// <summary>Where a target's event stream was last read up to.</summary>
/// <param name="Position">Resume point. Events at or before this have been published.</param>
/// <param name="LastEventId">Vendor event id at <paramref name="Position"/>, for tie-breaking.</param>
/// <param name="MaxLookback">Bound on how far back a gap-fill may reach.</param>
public readonly record struct EventCursor(
    DateTimeOffset Position, string? LastEventId, TimeSpan MaxLookback);

/// <summary>
/// Per-target operational state: cursors, capability matrix and health.
/// </summary>
/// <remarks>
/// Grouped into one type because all three are written by the same connector loop on the same
/// cadence, and splitting them would mean three connections per poll cycle instead of one.
/// </remarks>
public sealed class ConnectorStateStore
{
    private readonly NpgsqlDataSource _dataSource;

    public ConnectorStateStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    // ---- Cursors -----------------------------------------------------------

    /// <summary>
    /// Reads a target's cursor, or seeds one if this is its first poll.
    /// </summary>
    /// <remarks>
    /// A missing cursor seeds to <i>now</i>, not to the epoch. Seeding to the epoch would make a
    /// newly onboarded target request its VMS's entire event history on first contact, which on
    /// a busy site is both a self-inflicted flood and a serious load spike for the vendor.
    /// </remarks>
    public async Task<EventCursor> GetCursorAsync(
        Guid targetId, CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO federation.connector_cursor (target_id, cursor_timestamp)
            VALUES (@TargetId, now())
            ON CONFLICT (target_id) DO NOTHING;

            SELECT cursor_timestamp, cursor_event_id, max_lookback_hours
            FROM federation.connector_cursor
            WHERE target_id = @TargetId;
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        var row = await connection.QuerySingleAsync<CursorRow>(new CommandDefinition(
            sql, new { TargetId = targetId }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return new EventCursor(
            row.CursorTimestamp, row.CursorEventId, TimeSpan.FromHours(row.MaxLookbackHours));
    }

    /// <summary>
    /// Advances a cursor.
    /// </summary>
    /// <remarks>
    /// Never moves backwards: the <c>GREATEST</c> guard means a late or out-of-order batch
    /// cannot rewind progress and cause events to be republished indefinitely.
    /// </remarks>
    public async Task AdvanceCursorAsync(
        Guid targetId, DateTimeOffset position, string? lastEventId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE federation.connector_cursor
            SET cursor_timestamp = GREATEST(cursor_timestamp, @Position),
                cursor_event_id = @LastEventId,
                updated_at = now()
            WHERE target_id = @TargetId;
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await connection.ExecuteAsync(new CommandDefinition(sql,
            new { TargetId = targetId, Position = position, LastEventId = lastEventId },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    // ---- Capabilities ------------------------------------------------------

    /// <summary>
    /// Stores the probed capability matrix.
    /// </summary>
    /// <remarks>
    /// Downstream services read this rather than asking a device. At 80k cameras, letting a UI
    /// render trigger capability probes would turn one page load into thousands of vendor
    /// round trips.
    /// </remarks>
    public async Task SaveCapabilitiesAsync(
        Guid targetId, CapabilitySet capabilities, CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO federation.connector_capability
                (target_id, supported, adapter_version, probed_at, notes)
            VALUES (@TargetId, @Supported, @AdapterVersion, @ProbedAt, @Notes::jsonb)
            ON CONFLICT (target_id) DO UPDATE
            SET supported = EXCLUDED.supported,
                adapter_version = EXCLUDED.adapter_version,
                probed_at = EXCLUDED.probed_at,
                notes = EXCLUDED.notes;
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await connection.ExecuteAsync(new CommandDefinition(sql, new
        {
            TargetId = targetId,
            Supported = (int)capabilities.Supported,
            capabilities.AdapterVersion,
            capabilities.ProbedAt,
            Notes = JsonSerializer.Serialize(capabilities.Notes),
        }, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    // ---- Health ------------------------------------------------------------

    /// <summary>Records a health observation.</summary>
    public async Task RecordHealthAsync(
        ConnectorHealth health, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(health);

        const string sql = """
            INSERT INTO federation.connector_health
                (target_id, checked_at, status, latency_ms, camera_count,
                 consecutive_failures, circuit_open, last_error, events_since_check,
                 cursor_lag_seconds)
            VALUES (@TargetId, @CheckedAt, @Status::federation.health_status, @LatencyMs,
                    @CameraCount, @ConsecutiveFailures, @CircuitOpen, @LastError,
                    @EventsSinceCheck, @CursorLagSeconds)
            ON CONFLICT (target_id, checked_at) DO NOTHING;
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await connection.ExecuteAsync(new CommandDefinition(sql, new
        {
            health.TargetId,
            health.CheckedAt,
            Status = health.Status.ToString(),
            health.LatencyMs,
            health.CameraCount,
            health.ConsecutiveFailures,
            health.CircuitOpen,
            LastError = health.LastError?.Length > 1000 ? health.LastError[..1000] : health.LastError,
            EventsSinceCheck = health.EventsSinceLastCheck,
            CursorLagSeconds = health.CursorLag?.TotalSeconds,
        }, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    // ---- Inventory ---------------------------------------------------------

    /// <summary>
    /// Upserts the camera inventory reported by a target.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately does not delete cameras that stopped being reported. A VMS that returns 3 of
    /// its 128 channels is a common vendor failure mode, and treating that as "125 cameras were
    /// removed" would destroy inventory on a transient glitch. Reconciling genuine removals is a
    /// separate, deliberate operation.
    /// </para>
    /// <para>
    /// <b>One binary COPY and one merge, never a statement per camera.</b> Passing an enumerable
    /// to Dapper looks like a batch and is not: it re-executes the statement once per element, so
    /// a 128-channel NVR cost 128 round trips and a full parameter set per row. This path runs on
    /// the status poll — every 30 seconds per target, fleet-wide — which is where that turns from
    /// wasteful into the thing saturating the link to the database.
    /// </para>
    /// <para>
    /// The staging table is declared explicitly rather than with <c>LIKE federated_camera</c>
    /// because <c>health</c> is an enum: binary COPY would need the enum mapped on the connection,
    /// whereas staging it as text and casting once in the merge needs nothing.
    /// </para>
    /// </remarks>
    public async Task<int> UpsertCamerasAsync(
        IReadOnlyCollection<FederatedCamera> cameras, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cameras);

        if (cameras.Count == 0)
        {
            return 0;
        }

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        // ON COMMIT DROP is only meaningful inside a transaction block, and the COPY and the
        // merge must land or fail together — the same reasoning as EventStore.AppendAsync.
        await using var transaction = await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using (var create = new NpgsqlCommand("""
            CREATE TEMP TABLE staged_camera (
                ordinal              INT     NOT NULL,
                target_id            UUID    NOT NULL,
                native_camera_id     TEXT    NOT NULL,
                camera_id            UUID,
                organization_unit_id UUID    NOT NULL,
                site_id              UUID,
                name                 TEXT,
                vendor_model         TEXT,
                firmware             TEXT,
                is_enabled           BOOLEAN NOT NULL,
                is_recording         BOOLEAN,
                health               TEXT    NOT NULL,
                last_seen            TIMESTAMPTZ,
                stream_references    TEXT[]  NOT NULL,
                raw_reference        TEXT
            ) ON COMMIT DROP;
            """, connection, transaction))
        {
            await create.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var writer = await connection.BeginBinaryImportAsync("""
            COPY staged_camera (
                ordinal, target_id, native_camera_id, camera_id,
                organization_unit_id, site_id,
                name, vendor_model, firmware,
                is_enabled, is_recording, health, last_seen, stream_references, raw_reference
            ) FROM STDIN (FORMAT BINARY)
            """, cancellationToken).ConfigureAwait(false))
        {
            var ordinal = 0;

            foreach (var c in cameras)
            {
                await writer.StartRowAsync(cancellationToken).ConfigureAwait(false);
                await writer.WriteAsync(ordinal++, NpgsqlDbType.Integer, cancellationToken).ConfigureAwait(false);
                await writer.WriteAsync(c.TargetId, NpgsqlDbType.Uuid, cancellationToken).ConfigureAwait(false);
                await writer.WriteAsync(c.NativeCameraId, NpgsqlDbType.Text, cancellationToken).ConfigureAwait(false);
                await WriteNullableAsync(writer, c.CameraId, NpgsqlDbType.Uuid, cancellationToken).ConfigureAwait(false);
                await writer.WriteAsync(c.OrganizationUnitId, NpgsqlDbType.Uuid, cancellationToken).ConfigureAwait(false);
                await WriteNullableAsync(writer, c.SiteId, NpgsqlDbType.Uuid, cancellationToken).ConfigureAwait(false);
                await WriteNullableAsync(writer, c.Name, NpgsqlDbType.Text, cancellationToken).ConfigureAwait(false);
                await WriteNullableAsync(writer, c.VendorModel, NpgsqlDbType.Text, cancellationToken).ConfigureAwait(false);
                await WriteNullableAsync(writer, c.Firmware, NpgsqlDbType.Text, cancellationToken).ConfigureAwait(false);
                await writer.WriteAsync(c.IsEnabled, NpgsqlDbType.Boolean, cancellationToken).ConfigureAwait(false);
                await WriteNullableAsync(writer, c.IsRecording, NpgsqlDbType.Boolean, cancellationToken).ConfigureAwait(false);
                await writer.WriteAsync(c.Health.ToString(), NpgsqlDbType.Text, cancellationToken).ConfigureAwait(false);
                await WriteNullableAsync(writer, c.LastSeen, NpgsqlDbType.TimestampTz, cancellationToken).ConfigureAwait(false);

                // Materialised per row and released with it, rather than building a parameter
                // object graph for the whole batch up front.
                await writer.WriteAsync(
                    c.StreamReferences as string[] ?? [.. c.StreamReferences],
                    NpgsqlDbType.Array | NpgsqlDbType.Text, cancellationToken).ConfigureAwait(false);

                await WriteNullableAsync(writer, c.RawReference, NpgsqlDbType.Text, cancellationToken).ConfigureAwait(false);
            }

            await writer.CompleteAsync(cancellationToken).ConfigureAwait(false);
        }

        // DISTINCT ON collapses a channel a VMS reported twice in one response. Without it the
        // merge fails outright with "ON CONFLICT DO UPDATE command cannot affect row a second
        // time" — a whole poll lost to one duplicate row, where the per-row path used to just
        // apply the second write over the first. Ordering by ordinal DESC keeps the last
        // occurrence, matching that behaviour.
        const string merge = """
            INSERT INTO federation.federated_camera
                (target_id, native_camera_id, camera_id, organization_unit_id, site_id,
                 name, vendor_model, firmware,
                 is_enabled, is_recording, health, last_seen, stream_references, raw_reference)
            SELECT DISTINCT ON (target_id, native_camera_id)
                   target_id, native_camera_id, camera_id, organization_unit_id, site_id,
                   name, vendor_model, firmware,
                   is_enabled, is_recording, health::federation.health_status, last_seen,
                   stream_references, raw_reference
            FROM staged_camera
            ORDER BY target_id, native_camera_id, ordinal DESC
            ON CONFLICT (target_id, native_camera_id) DO UPDATE
            SET name = COALESCE(EXCLUDED.name, federated_camera.name),
                vendor_model = COALESCE(EXCLUDED.vendor_model, federated_camera.vendor_model),
                firmware = COALESCE(EXCLUDED.firmware, federated_camera.firmware),
                is_enabled = EXCLUDED.is_enabled,
                is_recording = COALESCE(EXCLUDED.is_recording, federated_camera.is_recording),
                health = EXCLUDED.health,
                last_seen = EXCLUDED.last_seen,
                stream_references = CASE
                    WHEN cardinality(EXCLUDED.stream_references) > 0
                    THEN EXCLUDED.stream_references
                    ELSE federated_camera.stream_references END,
                updated_at = now();
            """;

        var affected = await connection.ExecuteAsync(new CommandDefinition(
            merge, transaction: transaction, cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return affected;
    }

    /// <summary>Writes a value, or NULL when it has none.</summary>
    /// <remarks>
    /// <see cref="NpgsqlBinaryImporter"/> has no overload that treats a null as SQL NULL — a null
    /// reaches the wire as a typed default instead, so <c>last_seen</c> would silently become the
    /// zero timestamp on every camera a VMS has never reported seeing.
    /// </remarks>
    private static async Task WriteNullableAsync<T>(
        NpgsqlBinaryImporter writer, T? value, NpgsqlDbType type, CancellationToken cancellationToken)
        where T : struct
    {
        if (value is { } present)
        {
            await writer.WriteAsync(present, type, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await writer.WriteNullAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task WriteNullableAsync(
        NpgsqlBinaryImporter writer, string? value, NpgsqlDbType type, CancellationToken cancellationToken)
    {
        if (value is null)
        {
            await writer.WriteNullAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await writer.WriteAsync(value, type, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class CursorRow
    {
        public DateTimeOffset CursorTimestamp { get; init; }
        public string? CursorEventId { get; init; }
        public int MaxLookbackHours { get; init; }
    }
}
