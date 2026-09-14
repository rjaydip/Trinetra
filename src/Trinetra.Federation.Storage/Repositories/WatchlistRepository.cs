using Dapper;
using Npgsql;
using Trinetra.Federation.Core.Model;

namespace Trinetra.Federation.Storage.Repositories;

/// <summary>The outcome of acknowledging an alert: whose org owns it, and whether it was already done.</summary>
public sealed record AlertAcknowledgement(Guid OrganizationUnitId, bool AlreadyAcknowledged);

/// <summary>One prior detection found to match a plate newly added to the watchlist.</summary>
public sealed record HistoricalMatch
{
    public string EventId { get; init; } = "";
    public DateTimeOffset OccurredAt { get; init; }
}

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

    /// <summary>
    /// One page of watchlist entries within the caller's reach, with the full match count. A
    /// plate watchlist can run to tens of thousands of rows, so this is always bounded.
    /// <paramref name="active"/> filters to active (<c>true</c>) or retired (<c>false</c>)
    /// entries; null returns both.
    /// </summary>
    public async Task<PagedRows<WatchlistEntry>> ListAsync(
        bool? active, PageWindow window, CallerContext caller, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require("alert.read");

        var reachClause = caller.IsUnscopedFor("alert.read")
            ? ""
            : """
              AND organization_unit_id IN (
                  SELECT organization_unit_id
                  FROM federation.authorized_org_units(
                           p_user_id => @UserId, p_api_key_id => @ApiKeyId,
                           p_permission => 'alert.read'))
              """;

        await using var c = await _dataSource.OpenConnectionAsync(ct);
        var rows = (await c.QueryAsync<EntryRow, long, (EntryRow E, long T)>(
            new CommandDefinition($"""
                SELECT id, organization_unit_id, plate_number_normalized, reason, severity,
                       is_active, created_at, created_by, count(*) OVER() AS total_count
                FROM federation.watchlist_entry
                WHERE (@active::bool IS NULL OR is_active = @active)
                  {reachClause}
                ORDER BY created_at DESC, id
                LIMIT @limit OFFSET @offset;
                """, new
            {
                active, caller.UserId, caller.ApiKeyId,
                limit = window.Limit, offset = window.Offset,
            }, cancellationToken: ct),
            (e, t) => (e, t), splitOn: "total_count")).ToList();

        return new PagedRows<WatchlistEntry>(
            [.. rows.Select(r => r.E.ToDomain())], rows.Count > 0 ? PagedCount.From(rows[0].T) : 0);
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

    /// <summary>
    /// Deactivates an entry and returns it as it was (for the audit "before"). Null when the id
    /// is unknown.
    /// </summary>
    public async Task<WatchlistEntry?> DeactivateAsync(
        Guid id, CallerContext caller, UnitOfWork work, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(work);
        caller.Require("watchlist.manage");

        var row = await work.Connection.QuerySingleOrDefaultAsync<EntryRow>(new CommandDefinition("""
            UPDATE federation.watchlist_entry SET is_active = FALSE
            WHERE id = @id
            RETURNING id, organization_unit_id, plate_number_normalized, reason, severity,
                      is_active, created_at, created_by;
            """, new { id }, work.Transaction, cancellationToken: ct));

        return row?.ToDomain();
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
    /// Every prior detection (already in <c>detection_event</c>) whose plate matches any of
    /// <paramref name="plateVariants"/>, within the entry's own organization unit. Used to
    /// backfill alerts when a plate is newly added to the watchlist, so history already on file
    /// is not silently missed (only future ingests trigger <see cref="FindActiveMatchAsync"/>).
    /// Capped at <paramref name="cap"/> — a plate with more prior sightings than that is itself a
    /// signal worth surfacing as "capped" rather than silently truncated.
    /// </summary>
    public async Task<IReadOnlyList<HistoricalMatch>> FindHistoricalMatchesAsync(
        Guid organizationUnitId, IReadOnlySet<string> plateVariants, int cap, UnitOfWork work,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(work);

        if (plateVariants.Count == 0)
        {
            return [];
        }

        var rows = await work.Connection.QueryAsync<HistoricalMatch>(new CommandDefinition("""
            SELECT event_id AS EventId, occurred_at AS OccurredAt
            FROM federation.detection_event
            WHERE organization_unit_id = @organizationUnitId
              AND plate_number_normalized = ANY(@plateVariants)
            ORDER BY occurred_at DESC
            LIMIT @cap;
            """, new { organizationUnitId, plateVariants = plateVariants.ToArray(), cap },
            work.Transaction, cancellationToken: ct));

        return [.. rows];
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

    /// <summary>
    /// One page of raised alerts within the caller's reach, with the full match count.
    /// <paramref name="acknowledged"/> filters to acknowledged (<c>true</c>) or outstanding
    /// (<c>false</c>) alerts; null returns both. <paramref name="plate"/> is matched against the
    /// entry's normalised plate.
    /// </summary>
    /// <summary>
    /// One page of raised alerts within the caller's reach, with the full match count. Filters:
    /// <paramref name="acknowledged"/> (true = acked / false = outstanding / null = both),
    /// <paramref name="plate"/> (exact normalised plate), <paramref name="entryId"/> (one
    /// watchlist entry), <paramref name="severity"/>, and a <paramref name="from"/>/<paramref
    /// name="to"/> window on <c>raised_at</c>.
    /// </summary>
    public async Task<PagedRows<WatchlistAlertRow>> ListAlertsAsync(
        bool? acknowledged, string? plate, Guid? entryId, string? severity,
        DateTimeOffset? from, DateTimeOffset? to,
        PageWindow window, CallerContext caller, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require("alert.read");

        var reachClause = caller.IsUnscopedFor("alert.read")
            ? ""
            : """
              AND w.organization_unit_id IN (
                  SELECT organization_unit_id
                  FROM federation.authorized_org_units(
                           p_user_id => @UserId, p_api_key_id => @ApiKeyId,
                           p_permission => 'alert.read'))
              """;

        await using var c = await _dataSource.OpenConnectionAsync(ct);
        var rows = (await c.QueryAsync<WatchlistAlertRow, long, (WatchlistAlertRow R, long T)>(
            new CommandDefinition($"""
                SELECT a.id, a.watchlist_entry_id, w.plate_number_normalized, w.reason, w.severity,
                       a.detection_event_id, a.detection_occurred_at, a.raised_at, a.acknowledged_at,
                       count(*) OVER() AS total_count
                FROM federation.watchlist_alert a
                JOIN federation.watchlist_entry w ON w.id = a.watchlist_entry_id
                WHERE (@acknowledged::bool IS NULL
                       OR (@acknowledged AND a.acknowledged_at IS NOT NULL)
                       OR (NOT @acknowledged AND a.acknowledged_at IS NULL))
                  AND (@plate::text IS NULL OR w.plate_number_normalized = @plate)
                  AND (@entryId::uuid IS NULL OR a.watchlist_entry_id = @entryId)
                  AND (@severity::text IS NULL OR w.severity = @severity)
                  AND (@from::timestamptz IS NULL OR a.raised_at >= @from)
                  AND (@to::timestamptz IS NULL OR a.raised_at < @to)
                  {reachClause}
                ORDER BY a.raised_at DESC, a.id
                LIMIT @limit OFFSET @offset;
                """, new
            {
                acknowledged, plate, entryId, severity, from, to,
                caller.UserId, caller.ApiKeyId,
                limit = window.Limit, offset = window.Offset,
            }, cancellationToken: ct),
            (r, t) => (r, t), splitOn: "total_count")).ToList();

        return new PagedRows<WatchlistAlertRow>(
            [.. rows.Select(x => x.R)], rows.Count > 0 ? PagedCount.From(rows[0].T) : 0);
    }

    /// <summary>
    /// Acknowledges an alert, idempotently. Returns the alert's owning organization unit (via its
    /// entry) and whether it was <b>already</b> acknowledged before this call; null when the
    /// alert id is unknown.
    /// </summary>
    public async Task<AlertAcknowledgement?> AcknowledgeAlertAsync(
        Guid alertId, CallerContext caller, UnitOfWork work, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(work);
        caller.Require("alert.acknowledge");

        return await work.Connection.QuerySingleOrDefaultAsync<AlertAcknowledgement>(
            new CommandDefinition("""
            WITH target AS (
                SELECT a.id, (a.acknowledged_at IS NOT NULL) AS already_acknowledged,
                       w.organization_unit_id
                FROM federation.watchlist_alert a
                JOIN federation.watchlist_entry w ON w.id = a.watchlist_entry_id
                WHERE a.id = @id
            ),
            upd AS (
                UPDATE federation.watchlist_alert
                SET acknowledged_at = now(), acknowledged_by = @ActorId
                WHERE id = @id AND acknowledged_at IS NULL
                RETURNING id
            )
            SELECT organization_unit_id AS OrganizationUnitId,
                   already_acknowledged AS AlreadyAcknowledged
            FROM target;
            """, new { id = alertId, ActorId = caller.UserId },
            work.Transaction, cancellationToken: ct));
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
