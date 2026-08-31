using Dapper;
using Npgsql;
using Trinetra.Federation.Core.Model;

namespace Trinetra.Federation.Storage.Repositories;

/// <summary>A raised watchlist alert, joined with the entry and detection that produced it.</summary>
public sealed record WatchlistAlertRow
{
    public Guid Id { get; init; }
    public Guid WatchlistEntryId { get; init; }
    public string PlateNumberNormalized { get; init; } = "";
    public string? Reason { get; init; }
    public string Severity { get; init; } = "";
    public string DetectionEventId { get; init; } = "";
    public DateTimeOffset DetectionOccurredAt { get; init; }
    public DateTimeOffset RaisedAt { get; init; }
    public DateTimeOffset? AcknowledgedAt { get; init; }
}

/// <summary>
/// Watchlist entries and the alerts raised when a detection matches one. Matching happens at
/// ingest time (<see cref="FindActiveMatchAsync"/> + <see cref="RaiseAlertAsync"/>), inside the
/// same transaction as the detection insert.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Performance", "CA1822:Mark members as static",
    Justification = "Write methods take their connection from the UnitOfWork by design.",
    Scope = "type")]
public sealed class WatchlistRepository
{
    private readonly NpgsqlDataSource _dataSource;

    public WatchlistRepository(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<IReadOnlyList<WatchlistEntry>> ListAsync(
        CallerContext caller, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require("alert.read");

        var sql = caller.IsUnscopedFor("alert.read")
            ? """
              SELECT id, organization_unit_id, plate_number_normalized, reason, severity,
                     is_active, created_at, created_by
              FROM federation.watchlist_entry ORDER BY created_at DESC;
              """
            : """
              SELECT id, organization_unit_id, plate_number_normalized, reason, severity,
                     is_active, created_at, created_by
              FROM federation.watchlist_entry
              WHERE organization_unit_id IN (
                  SELECT organization_unit_id
                  FROM federation.authorized_org_units(
                           p_user_id => @UserId, p_api_key_id => @ApiKeyId,
                           p_permission => 'alert.read'))
              ORDER BY created_at DESC;
              """;

        await using var c = await _dataSource.OpenConnectionAsync(ct);
        var rows = await c.QueryAsync<EntryRow>(new CommandDefinition(
            sql, new { caller.UserId, caller.ApiKeyId }, cancellationToken: ct));

        return [.. rows.Select(r => r.ToDomain())];
    }

    public async Task<Guid> CreateAsync(
        WatchlistEntry entry, CallerContext caller, UnitOfWork work, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(work);
        caller.Require("watchlist.manage");

        if (!caller.IsUnscopedFor("watchlist.manage"))
        {
            var permitted = await work.Connection.ExecuteScalarAsync<bool>(new CommandDefinition(
                """
                SELECT federation.has_permission(
                    p_user_id => @UserId, p_api_key_id => @ApiKeyId,
                    p_permission => 'watchlist.manage', p_organization_unit_id => @OrgUnit);
                """,
                new { caller.UserId, caller.ApiKeyId, OrgUnit = entry.OrganizationUnitId },
                work.Transaction, cancellationToken: ct));

            if (!permitted)
            {
                throw new ForbiddenException("watchlist.manage");
            }
        }

        return await work.Connection.ExecuteScalarAsync<Guid>(new CommandDefinition("""
            INSERT INTO federation.watchlist_entry
                (organization_unit_id, plate_number_normalized, reason, severity, is_active,
                 created_by)
            VALUES (@OrganizationUnitId, @PlateNumberNormalized, @Reason, @Severity, TRUE,
                    @ActorId)
            RETURNING id;
            """, new
        {
            entry.OrganizationUnitId, entry.PlateNumberNormalized, entry.Reason, entry.Severity,
            ActorId = caller.UserId,
        }, work.Transaction, cancellationToken: ct));
    }

    public async Task<bool> DeactivateAsync(
        Guid id, CallerContext caller, UnitOfWork work, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(work);
        caller.Require("watchlist.manage");

        var affected = await work.Connection.ExecuteAsync(new CommandDefinition("""
            UPDATE federation.watchlist_entry SET is_active = FALSE WHERE id = @id;
            """, new { id }, work.Transaction, cancellationToken: ct));

        return affected > 0;
    }

    /// <summary>The active watchlist entry matching any of a detection's OCR-variant plates, if any.</summary>
    public async Task<WatchlistEntry?> FindActiveMatchAsync(
        Guid organizationUnitId, IReadOnlySet<string> plateVariants, UnitOfWork work,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(work);

        if (plateVariants.Count == 0)
        {
            return null;
        }

        var row = await work.Connection.QuerySingleOrDefaultAsync<EntryRow>(new CommandDefinition("""
            SELECT id, organization_unit_id, plate_number_normalized, reason, severity,
                   is_active, created_at, created_by
            FROM federation.watchlist_entry
            WHERE organization_unit_id = @organizationUnitId
              AND is_active
              AND plate_number_normalized = ANY(@plateVariants)
            LIMIT 1;
            """, new { organizationUnitId, plateVariants = plateVariants.ToArray() },
            work.Transaction, cancellationToken: ct));

        return row?.ToDomain();
    }

    /// <summary>
    /// Raises an alert, idempotent on (entry, detection) — a retried detection ingest must not
    /// raise a second alert for the same match.
    /// </summary>
    public async Task<bool> RaiseAlertAsync(
        Guid watchlistEntryId, string detectionEventId, DateTimeOffset detectionOccurredAt,
        UnitOfWork work, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(work);

        var affected = await work.Connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO federation.watchlist_alert
                (watchlist_entry_id, detection_event_id, detection_occurred_at)
            VALUES (@watchlistEntryId, @detectionEventId, @detectionOccurredAt)
            ON CONFLICT (watchlist_entry_id, detection_event_id) DO NOTHING;
            """, new { watchlistEntryId, detectionEventId, detectionOccurredAt },
            work.Transaction, cancellationToken: ct));

        return affected > 0;
    }

    public async Task<IReadOnlyList<WatchlistAlertRow>> ListAlertsAsync(
        int limit, CallerContext caller, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require("alert.read");

        var sql = caller.IsUnscopedFor("alert.read")
            ? """
              SELECT a.id, a.watchlist_entry_id, w.plate_number_normalized, w.reason, w.severity,
                     a.detection_event_id, a.detection_occurred_at, a.raised_at, a.acknowledged_at
              FROM federation.watchlist_alert a
              JOIN federation.watchlist_entry w ON w.id = a.watchlist_entry_id
              ORDER BY a.raised_at DESC LIMIT @limit;
              """
            : """
              SELECT a.id, a.watchlist_entry_id, w.plate_number_normalized, w.reason, w.severity,
                     a.detection_event_id, a.detection_occurred_at, a.raised_at, a.acknowledged_at
              FROM federation.watchlist_alert a
              JOIN federation.watchlist_entry w ON w.id = a.watchlist_entry_id
              WHERE w.organization_unit_id IN (
                  SELECT organization_unit_id
                  FROM federation.authorized_org_units(
                           p_user_id => @UserId, p_api_key_id => @ApiKeyId,
                           p_permission => 'alert.read'))
              ORDER BY a.raised_at DESC LIMIT @limit;
              """;

        await using var c = await _dataSource.OpenConnectionAsync(ct);
        var rows = await c.QueryAsync<WatchlistAlertRow>(new CommandDefinition(sql, new
        {
            limit = Math.Clamp(limit, 1, 500), caller.UserId, caller.ApiKeyId,
        }, cancellationToken: ct));

        return [.. rows];
    }

    public async Task<bool> AcknowledgeAlertAsync(
        Guid alertId, CallerContext caller, UnitOfWork work, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(work);
        caller.Require("alert.acknowledge");

        var affected = await work.Connection.ExecuteAsync(new CommandDefinition("""
            UPDATE federation.watchlist_alert
            SET acknowledged_at = now(), acknowledged_by = @ActorId
            WHERE id = @id AND acknowledged_at IS NULL;
            """, new { id = alertId, ActorId = caller.UserId },
            work.Transaction, cancellationToken: ct));

        return affected > 0;
    }

    private sealed record EntryRow
    {
        public Guid Id { get; init; }
        public Guid OrganizationUnitId { get; init; }
        public string PlateNumberNormalized { get; init; } = "";
        public string? Reason { get; init; }
        public string Severity { get; init; } = "";
        public bool IsActive { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
        public Guid? CreatedBy { get; init; }

        public WatchlistEntry ToDomain() => new()
        {
            Id = Id,
            OrganizationUnitId = OrganizationUnitId,
            PlateNumberNormalized = PlateNumberNormalized,
            Reason = Reason,
            Severity = Severity,
            IsActive = IsActive,
            CreatedAt = CreatedAt,
            CreatedBy = CreatedBy,
        };
    }
}
