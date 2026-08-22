using System.Text.Json;
using Dapper;
using Npgsql;
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
    /// Deliberately does not delete cameras that stopped being reported. A VMS that returns 3 of
    /// its 128 channels is a common vendor failure mode, and treating that as "125 cameras were
    /// removed" would destroy inventory on a transient glitch. Reconciling genuine removals is a
    /// separate, deliberate operation.
    /// </remarks>
    public async Task<int> UpsertCamerasAsync(
        IReadOnlyCollection<FederatedCamera> cameras, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cameras);

        if (cameras.Count == 0)
        {
            return 0;
        }

        const string sql = """
            INSERT INTO federation.federated_camera
                (target_id, native_camera_id, camera_id, organization_unit_id, site_id,
                 name, vendor_model, firmware,
                 is_enabled, is_recording, health, last_seen, stream_references, raw_reference)
            VALUES (@TargetId, @NativeCameraId, @CameraId, @OrganizationUnitId, @SiteId,
                    @Name, @VendorModel, @Firmware,
                    @IsEnabled, @IsRecording, @Health::federation.health_status, @LastSeen,
                    @StreamReferences, @RawReference)
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

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        return await connection.ExecuteAsync(new CommandDefinition(sql, cameras.Select(c => new
        {
            c.TargetId,
            c.NativeCameraId,
            c.CameraId,
            c.OrganizationUnitId,
            c.SiteId,
            c.Name,
            c.VendorModel,
            c.Firmware,
            c.IsEnabled,
            c.IsRecording,
            Health = c.Health.ToString(),
            c.LastSeen,
            StreamReferences = c.StreamReferences.ToArray(),
            c.RawReference,
        }), cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    private sealed class CursorRow
    {
        public DateTimeOffset CursorTimestamp { get; init; }
        public string? CursorEventId { get; init; }
        public int MaxLookbackHours { get; init; }
    }
}
