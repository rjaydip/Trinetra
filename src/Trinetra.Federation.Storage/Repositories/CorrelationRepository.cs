using Dapper;
using Npgsql;

namespace Trinetra.Federation.Storage.Repositories;

/// <summary>One correlation group, as the list endpoint returns it.</summary>
public sealed record CorrelationGroupRow
{
    public Guid Id { get; init; }
    public Guid RuleId { get; init; }
    public string RuleCode { get; init; } = "";
    public string NaturalKey { get; init; } = "";
    public DateTimeOffset WindowBucket { get; init; }

    /// <summary>Possible-match confidence in [0,1] -- never certainty.</summary>
    public double Confidence { get; init; }

    public int MemberCount { get; init; }
    public DateTimeOffset FirstOccurredAt { get; init; }
    public DateTimeOffset LastOccurredAt { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>One member event of a correlation group.</summary>
public sealed record CorrelationGroupMemberRow
{
    public string FederationEventId { get; init; } = "";
    public DateTimeOffset EventOccurredAt { get; init; }
    public string CameraId { get; init; } = "";
    public Guid SourceVmsId { get; init; }
    public Guid OrganizationUnitId { get; init; }
    public Guid? GeographicAreaId { get; init; }
}

/// <summary>A group plus its members, as the detail endpoint returns it.</summary>
public sealed record CorrelationGroupDetailRow
{
    public required CorrelationGroupRow Group { get; init; }
    public required IReadOnlyList<CorrelationGroupMemberRow> Members { get; init; }
}

/// <summary>
/// Read models over <c>correlation_group</c> / <c>correlation_group_member</c>, scoped to the
/// caller through the organization/geography columns denormalised onto each member row.
/// </summary>
/// <remarks>
/// <para>
/// Groups themselves carry no organization/geography — a possible match can span multiple
/// departments and areas by design. A group is in scope for a caller when <b>at least one</b>
/// member event is: the same "reachable member" test <c>EventQueryRepository</c> applies to a
/// single event, extended to a cluster. This is deliberately permissive rather than requiring
/// every member to be in scope — a caller who can already see one camera's event should be able
/// to see that a possible match exists, even if some of the other cameras involved belong to a
/// department they cannot otherwise reach. The member list itself still only shows the rows the
/// caller is scoped for.
/// </para>
/// <para>
/// <b>Confidence is never certainty.</b> Every response DTO built from these rows must describe
/// <see cref="CorrelationGroupRow.Confidence"/> as a possible-match score, per CLAUDE.md's
/// production posture.
/// </para>
/// </remarks>
public sealed class CorrelationRepository
{
    private readonly NpgsqlDataSource _dataSource;

    public CorrelationRepository(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<PagedRows<CorrelationGroupRow>> ListAsync(
        DateTimeOffset from, DateTimeOffset to, PageWindow window,
        CallerContext caller, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require("correlation.read");

        await using var c = await _dataSource.OpenConnectionAsync(ct);

        var unscopedOrg = caller.IsUnscopedFor("correlation.read");
        var unscopedGeo = caller.IsUnscopedForGeography("correlation.read");

        Guid[] units = unscopedOrg ? [] : await AuthorizedOrgUnitsAsync(c, caller, ct);
        Guid[] areas = unscopedGeo ? [] : await AuthorizedAreasAsync(c, caller, ct);

        if ((!unscopedOrg && units.Length == 0) || (!unscopedGeo && areas.Length == 0))
        {
            return new PagedRows<CorrelationGroupRow>([], 0);
        }

        var sql = $"""
            SELECT g.id, g.rule_id, r.code AS rule_code, g.natural_key, g.window_bucket,
                   g.confidence, g.member_count, g.first_occurred_at, g.last_occurred_at,
                   g.created_at, count(*) OVER() AS total_count
            FROM federation.correlation_group g
            JOIN federation.correlation_rule r ON r.id = g.rule_id
            WHERE g.first_occurred_at < @To AND g.last_occurred_at >= @From
              AND EXISTS (
                  SELECT 1 FROM federation.correlation_group_member m
                  WHERE m.group_id = g.id
                    {(unscopedOrg ? "" : "AND m.organization_unit_id = ANY (@units)")}
                    {(unscopedGeo ? "" : """
                    AND (m.geographic_area_id IS NULL OR m.geographic_area_id = ANY (@areas))
                    """)}
              )
            ORDER BY g.last_occurred_at DESC, g.id
            LIMIT @Limit OFFSET @Offset;
            """;

        var rows = (await c.QueryAsync<CorrelationGroupRow, long, (CorrelationGroupRow R, long T)>(
            new CommandDefinition(sql, new
            {
                From = from, To = to, units, areas, window.Limit, window.Offset,
            }, cancellationToken: ct),
            (r, t) => (r, t), splitOn: "total_count")).ToList();

        return new PagedRows<CorrelationGroupRow>(
            [.. rows.Select(x => x.R)], rows.Count > 0 ? PagedCount.From(rows[0].T) : 0);
    }

    /// <summary>
    /// One group with its members, or null if the id is unknown or no member is reachable by this
    /// caller (see remarks on the class for the "at least one member in scope" rule).
    /// </summary>
    public async Task<CorrelationGroupDetailRow?> GetAsync(
        Guid id, CallerContext caller, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require("correlation.read");

        await using var c = await _dataSource.OpenConnectionAsync(ct);

        var unscopedOrg = caller.IsUnscopedFor("correlation.read");
        var unscopedGeo = caller.IsUnscopedForGeography("correlation.read");

        Guid[] units = unscopedOrg ? [] : await AuthorizedOrgUnitsAsync(c, caller, ct);
        Guid[] areas = unscopedGeo ? [] : await AuthorizedAreasAsync(c, caller, ct);

        if ((!unscopedOrg && units.Length == 0) || (!unscopedGeo && areas.Length == 0))
        {
            return null;
        }

        var group = await c.QuerySingleOrDefaultAsync<CorrelationGroupRow>(new CommandDefinition($"""
            SELECT g.id, g.rule_id, r.code AS rule_code, g.natural_key, g.window_bucket,
                   g.confidence, g.member_count, g.first_occurred_at, g.last_occurred_at,
                   g.created_at
            FROM federation.correlation_group g
            JOIN federation.correlation_rule r ON r.id = g.rule_id
            WHERE g.id = @id
              AND EXISTS (
                  SELECT 1 FROM federation.correlation_group_member m
                  WHERE m.group_id = g.id
                    {(unscopedOrg ? "" : "AND m.organization_unit_id = ANY (@units)")}
                    {(unscopedGeo ? "" : """
                    AND (m.geographic_area_id IS NULL OR m.geographic_area_id = ANY (@areas))
                    """)}
              );
            """, new { id, units, areas }, cancellationToken: ct));

        if (group is null)
        {
            return null;
        }

        // The member list itself is filtered to what this caller can reach -- unlike the
        // existence check above, which only needs one reachable member.
        var members = await c.QueryAsync<CorrelationGroupMemberRow>(new CommandDefinition($"""
            SELECT m.federation_event_id, m.event_occurred_at, m.camera_id, m.source_vms_id,
                   m.organization_unit_id, m.geographic_area_id
            FROM federation.correlation_group_member m
            WHERE m.group_id = @id
              {(unscopedOrg ? "" : "AND m.organization_unit_id = ANY (@units)")}
              {(unscopedGeo ? "" : """
              AND (m.geographic_area_id IS NULL OR m.geographic_area_id = ANY (@areas))
              """)}
            ORDER BY m.event_occurred_at;
            """, new { id, units, areas }, cancellationToken: ct));

        return new CorrelationGroupDetailRow { Group = group, Members = [.. members] };
    }

    private static async Task<Guid[]> AuthorizedOrgUnitsAsync(
        NpgsqlConnection c, CallerContext caller, CancellationToken ct)
    {
        var units = await c.QueryAsync<Guid>(new CommandDefinition("""
            SELECT organization_unit_id FROM federation.authorized_org_units(
                p_user_id => @UserId, p_api_key_id => @ApiKeyId, p_permission => 'correlation.read');
            """, new { caller.UserId, caller.ApiKeyId }, cancellationToken: ct));

        return [.. units];
    }

    private static async Task<Guid[]> AuthorizedAreasAsync(
        NpgsqlConnection c, CallerContext caller, CancellationToken ct)
    {
        var areas = await c.QueryAsync<Guid>(new CommandDefinition("""
            SELECT geographic_area_id FROM federation.authorized_geographic_areas(
                p_user_id => @UserId, p_api_key_id => @ApiKeyId, p_permission => 'correlation.read');
            """, new { caller.UserId, caller.ApiKeyId }, cancellationToken: ct));

        return [.. areas];
    }
}
