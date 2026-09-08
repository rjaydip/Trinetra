using Dapper;
using Npgsql;

namespace Trinetra.Federation.Storage.Repositories;

/// <summary>One normalised event, as the hot-window query returns it.</summary>
// Init properties, not positional records. Dapper binds a positional record by matching the
// reader's column TYPES to constructor parameters exactly, so a timestamptz (which Npgsql
// surfaces as DateTime) will not bind to a DateTimeOffset parameter and the query throws at
// runtime. Property binding converts, which is what lets these keep DateTimeOffset -- required
// everywhere by CLAUDE.md, because a naive timestamp silently corrupts time-window correlation.
public sealed record EventRow
{
    public string EventId { get; init; } = "";
    public Guid SourceVmsId { get; init; }
    public string CameraId { get; init; } = "";
    public string EventType { get; init; } = "";
    public string? VendorEventType { get; init; }
    public DateTimeOffset OccurredAt { get; init; }
    public string Severity { get; init; } = "";
    public string? ObjectReference { get; init; }
    public double? Confidence { get; init; }
}

/// <summary>Everything the event query is allowed to filter on.</summary>
/// <remarks>
/// <c>From</c> and <c>To</c> are required, not nullable: <c>federation_event</c> is partitioned
/// by time and takes 100-400M rows/day, so an unbounded query takes the database down with it.
/// The bound is part of the query's type rather than a check somewhere upstream.
/// </remarks>
public readonly record struct EventQuery(
    DateTimeOffset From,
    DateTimeOffset To,
    int PageSize,
    string? CameraId,
    string? EventType,
    string? ObjectReference,
    DateTimeOffset? CursorTime,
    string? CursorId);

/// <summary>
/// The hot PostgreSQL window of normalised events.
/// </summary>
/// <remarks>
/// This serves recent events only. Free-text and wide historical search belong in OpenSearch;
/// this repository gets re-pointed at it rather than extended, and keeping the query narrow is
/// what makes that swap cheap.
/// </remarks>
public sealed class EventQueryRepository
{
    private readonly NpgsqlDataSource _dataSource;

    public EventQueryRepository(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<IReadOnlyList<EventRow>> QueryAsync(
        EventQuery query, CallerContext caller, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require("event.read");

        await using var c = await _dataSource.OpenConnectionAsync(ct);

        // Organization and geography are independent scope dimensions, ANDed, each bypassed only
        // by its own unscoped flag (invariant 12). Both are resolved to a concrete array before
        // the query for the same planner reason the org dimension always was — see ScopeAsync.
        var unscopedOrg = caller.IsUnscopedFor("event.read");
        var unscopedGeo = caller.IsUnscopedForGeography("event.read");

        Guid[] units = unscopedOrg ? [] : await AuthorizedOrgUnitsAsync(c, caller, ct).ConfigureAwait(false);
        Guid[] areas = unscopedGeo ? [] : await AuthorizedAreasAsync(c, caller, ct).ConfigureAwait(false);

        if ((!unscopedOrg && units.Length == 0) || (!unscopedGeo && areas.Length == 0))
        {
            // Reaches nothing on one of the dimensions. Returning early is not only an
            // optimisation: `= ANY(empty array)` is valid SQL that matches nothing, but running
            // it still costs a scan per page.
            return [];
        }

        // Two shapes rather than one with a nullable cursor. The keyset predicate has to be a ROW
        // comparison for the index to seek to the cursor; written as the equivalent OR-chain the
        // planner treats it as a filter and re-reads every row before the cursor, so page N costs
        // N pages of work -- the exact deep-pagination cost keyset was chosen to avoid. Measured
        // at a cursor 21k rows in: 83.6ms as an OR-chain, 0.107ms as a row comparison.
        //
        // A single query cannot serve both, because ROW(a, b) < ROW(NULL, NULL) is NULL rather
        // than TRUE, so the first page would come back empty.
        var sql = $"""
            SELECT e.event_id, e.source_vms_id, e.camera_id, e.event_type, e.vendor_event_type,
                   e.occurred_at, e.severity, e.object_reference, e.confidence
            FROM federation.federation_event e
            WHERE e.occurred_at >= @From AND e.occurred_at < @To
              {(unscopedOrg ? "" : "AND e.organization_unit_id = ANY (@units)")}
              {(unscopedGeo ? "" : """
              AND (e.geographic_area_id IS NULL OR e.geographic_area_id = ANY (@areas))
              """)}
              AND (@CameraId::text IS NULL OR e.camera_id = @CameraId)
              AND (@EventType::text IS NULL OR e.event_type = @EventType)
              AND (@ObjectReference::text IS NULL OR e.object_reference = @ObjectReference)
              {(query.CursorTime is null ? "" : "AND (e.occurred_at, e.event_id) < (@CursorTime, @CursorId)")}
            ORDER BY e.occurred_at DESC, e.event_id DESC
            LIMIT @PageSize;
            """;

        var rows = await c.QueryAsync<EventRow>(new CommandDefinition(sql, new
        {
            query.From, query.To, query.CameraId, query.EventType, query.ObjectReference,
            query.CursorTime, query.CursorId, query.PageSize, units, areas,
        }, cancellationToken: ct));

        return [.. rows];
    }

    /// <summary>The organization units this caller may read events for.</summary>
    /// <remarks>
    /// Resolved to a concrete list before the query rather than joined as a set-returning
    /// function. The planner cannot see inside <c>authorized_org_units()</c>, so it falls back to
    /// scanning the time range and discarding out-of-scope rows -- at 1% scope that is ~99 rows
    /// read per row returned. Measured on 400k events in one partition: 10.0ms / 15,101 buffers
    /// as a subquery, 1.6ms / 372 buffers as an array, because the array lets it use
    /// ix_event_org_time and read only rows already in scope.
    /// </remarks>
    private static async Task<Guid[]> AuthorizedOrgUnitsAsync(
        NpgsqlConnection c, CallerContext caller, CancellationToken ct)
    {
        var units = await c.QueryAsync<Guid>(new CommandDefinition("""
            SELECT organization_unit_id FROM federation.authorized_org_units(
                p_user_id => @UserId, p_api_key_id => @ApiKeyId, p_permission => 'event.read');
            """, new { caller.UserId, caller.ApiKeyId }, cancellationToken: ct));

        return [.. units];
    }

    /// <summary>
    /// The geographic areas this caller may read events for — resolved to an array for the same
    /// planner reason as <see cref="AuthorizedOrgUnitsAsync"/>. Used as a secondary filter over
    /// the (already time- and org-narrowed) page rather than the primary access path, so a
    /// geographic_area_id filter on the main query is a direct column comparison.
    /// </summary>
    private static async Task<Guid[]> AuthorizedAreasAsync(
        NpgsqlConnection c, CallerContext caller, CancellationToken ct)
    {
        var areas = await c.QueryAsync<Guid>(new CommandDefinition("""
            SELECT geographic_area_id FROM federation.authorized_geographic_areas(
                p_user_id => @UserId, p_api_key_id => @ApiKeyId, p_permission => 'event.read');
            """, new { caller.UserId, caller.ApiKeyId }, cancellationToken: ct));

        return [.. areas];
    }
}
