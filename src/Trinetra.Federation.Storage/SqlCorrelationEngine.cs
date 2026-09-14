using System.Text.Json;
using Dapper;
using Npgsql;
using Trinetra.Federation.Core.Abstractions;
using Trinetra.Federation.Core.Model;

namespace Trinetra.Federation.Storage;

/// <summary>
/// Day-one <see cref="ICorrelationEngine"/> implementation: windowed SQL over the hot
/// <c>federation_event</c> tier. Architecture §8.
/// </summary>
/// <remarks>
/// <para>
/// Not a scoped repository — this is a system-internal writer with no caller, the same shape as
/// <see cref="EventStore"/>: it runs inside the connector-worker process on its own schedule
/// (<c>CorrelationRunner</c>), never in response to an API request, so there is no
/// <see cref="CallerContext"/> to require. The read side callers actually reach —
/// <c>CorrelationRepository</c> — takes one, as every scoped repository method must.
/// </para>
/// <para>
/// Per rule: buckets in-window events sharing an <c>object_reference</c> by
/// <see cref="CorrelationRule.TimeWindow"/>, optionally narrows membership by
/// <see cref="CorrelationRule.SpatialRadiusMeters"/> (plain-trig haversine against the bucket's
/// centroid — no PostGIS), keeps only buckets meeting
/// <see cref="CorrelationRule.ConfidenceFloor"/> and <see cref="CorrelationRule.RequiredSignalAgreement"/>
/// (distinct cameras), then upserts one <c>correlation_group</c> row per surviving bucket with its
/// members. The unique constraint on <c>(rule_id, natural_key, window_bucket)</c> makes a rerun
/// over an overlapping window a no-op rather than a duplicate (CLAUDE.md invariant 5).
/// </para>
/// </remarks>
public sealed class SqlCorrelationEngine : ICorrelationEngine
{
    private readonly NpgsqlDataSource _dataSource;

    public SqlCorrelationEngine(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<CorrelationRunSummary> RunAsync(
        DateTimeOffset windowStart, DateTimeOffset windowEnd, CancellationToken cancellationToken)
    {
        if (windowEnd <= windowStart)
        {
            throw new ArgumentException("windowEnd must be after windowStart.", nameof(windowEnd));
        }

        var rules = await LoadActiveRulesAsync(cancellationToken).ConfigureAwait(false);

        var candidateGroups = 0;
        var created = 0;

        foreach (var rule in rules)
        {
            var groups = await FindGroupsAsync(rule, windowStart, windowEnd, cancellationToken)
                .ConfigureAwait(false);
            candidateGroups += groups.Count;

            foreach (var group in groups)
            {
                if (await PersistGroupAsync(rule, group, cancellationToken).ConfigureAwait(false))
                {
                    created++;
                }
            }
        }

        return new CorrelationRunSummary
        {
            RulesEvaluated = rules.Count,
            CandidateGroups = candidateGroups,
            GroupsCreated = created,
        };
    }

    private async Task<IReadOnlyList<CorrelationRule>> LoadActiveRulesAsync(CancellationToken ct)
    {
        await using var c = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);

        var rows = await c.QueryAsync<RuleRow>(new CommandDefinition("""
            SELECT id, code, name, description, time_window_seconds, spatial_radius_meters,
                   confidence_floor, required_signal_agreement, is_active
            FROM federation.correlation_rule
            WHERE is_active;
            """, cancellationToken: ct)).ConfigureAwait(false);

        return [.. rows.Select(r => new CorrelationRule
        {
            Id = r.Id,
            Code = r.Code,
            Name = r.Name,
            Description = r.Description,
            TimeWindow = TimeSpan.FromSeconds(r.TimeWindowSeconds),
            SpatialRadiusMeters = r.SpatialRadiusMeters,
            ConfidenceFloor = r.ConfidenceFloor,
            RequiredSignalAgreement = r.RequiredSignalAgreement,
            IsActive = r.IsActive,
        })];
    }

    /// <summary>
    /// One SQL pass: bucket, optionally narrow by spatial radius against each bucket's centroid,
    /// then keep only buckets clearing the confidence floor and signal-agreement threshold.
    /// </summary>
    private async Task<IReadOnlyList<CorrelationGroupOutcome>> FindGroupsAsync(
        CorrelationRule rule, DateTimeOffset windowStart, DateTimeOffset windowEnd, CancellationToken ct)
    {
        await using var c = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);

        var rows = await c.QueryAsync<GroupRow>(new CommandDefinition("""
            WITH matched AS (
                SELECT event_id, object_reference, occurred_at, camera_id, source_vms_id,
                       organization_unit_id, geographic_area_id, confidence, latitude, longitude
                FROM federation.federation_event
                WHERE occurred_at >= @WindowStart AND occurred_at < @WindowEnd
                  AND object_reference IS NOT NULL
                  AND (confidence IS NULL OR confidence >= @ConfidenceFloor)
            ),
            bucketed AS (
                SELECT *,
                       to_timestamp(floor(extract(epoch FROM occurred_at) / @WindowSeconds)
                                    * @WindowSeconds) AS window_bucket
                FROM matched
            ),
            centroids AS (
                SELECT object_reference, window_bucket,
                       avg(latitude::double precision) AS centroid_lat,
                       avg(longitude::double precision) AS centroid_lon
                FROM bucketed
                WHERE latitude IS NOT NULL AND longitude IS NOT NULL
                GROUP BY object_reference, window_bucket
            ),
            -- Plain-trig haversine against the bucket centroid -- no PostGIS (CLAUDE.md: no
            -- PostgreSQL extensions). A candidate with no location, or no radius configured, is
            -- never excluded by this step.
            filtered AS (
                SELECT b.*
                FROM bucketed b
                LEFT JOIN centroids c
                  ON c.object_reference = b.object_reference AND c.window_bucket = b.window_bucket
                WHERE @SpatialRadiusMeters::double precision IS NULL
                   OR b.latitude IS NULL OR b.longitude IS NULL
                   OR c.centroid_lat IS NULL
                   OR 2 * 6371000 * asin(sqrt(
                        power(sin(radians(b.latitude::double precision - c.centroid_lat) / 2), 2)
                        + cos(radians(c.centroid_lat)) * cos(radians(b.latitude::double precision))
                          * power(sin(radians(b.longitude::double precision - c.centroid_lon) / 2), 2)
                      )) <= @SpatialRadiusMeters
            ),
            grouped AS (
                SELECT object_reference, window_bucket,
                       count(DISTINCT camera_id) AS distinct_cameras,
                       avg(coalesce(confidence, 1.0)) AS avg_confidence,
                       min(occurred_at) AS first_occurred_at,
                       max(occurred_at) AS last_occurred_at,
                       jsonb_agg(jsonb_build_object(
                           'eventId', event_id, 'cameraId', camera_id,
                           'sourceVmsId', source_vms_id,
                           'organizationUnitId', organization_unit_id,
                           'geographicAreaId', geographic_area_id,
                           'occurredAt', occurred_at,
                           'objectReference', object_reference,
                           'confidence', confidence,
                           'latitude', latitude, 'longitude', longitude)
                           ORDER BY occurred_at) AS members
                FROM filtered
                GROUP BY object_reference, window_bucket
                HAVING count(DISTINCT camera_id) >= @RequiredSignalAgreement
                   AND avg(coalesce(confidence, 1.0)) >= @ConfidenceFloor
            )
            SELECT object_reference AS NaturalKey, window_bucket AS WindowBucket,
                   least(1.0, avg_confidence) AS Confidence,
                   first_occurred_at AS FirstOccurredAt, last_occurred_at AS LastOccurredAt,
                   members::text AS MembersJson
            FROM grouped;
            """, new
        {
            WindowStart = windowStart,
            WindowEnd = windowEnd,
            WindowSeconds = rule.TimeWindow.TotalSeconds,
            rule.ConfidenceFloor,
            rule.SpatialRadiusMeters,
            rule.RequiredSignalAgreement,
        }, cancellationToken: ct)).ConfigureAwait(false);

        return [.. rows.Select(r => new CorrelationGroupOutcome
        {
            NaturalKey = r.NaturalKey,
            WindowBucket = r.WindowBucket,
            Confidence = r.Confidence,
            FirstOccurredAt = r.FirstOccurredAt,
            LastOccurredAt = r.LastOccurredAt,
            Members = [.. JsonSerializer.Deserialize<List<MemberJson>>(r.MembersJson)!
                .Select(m => new CorrelationCandidate
                {
                    EventId = m.EventId,
                    CameraId = m.CameraId,
                    SourceVmsId = m.SourceVmsId,
                    OrganizationUnitId = m.OrganizationUnitId,
                    GeographicAreaId = m.GeographicAreaId,
                    ObjectReference = m.ObjectReference,
                    Confidence = m.Confidence,
                    OccurredAt = m.OccurredAt,
                    Latitude = m.Latitude,
                    Longitude = m.Longitude,
                })],
        })];
    }

    /// <summary>
    /// Upserts one group and its members. <c>FindGroupsAsync</c> recomputes the full candidate
    /// set for a bucket on every poll (it re-queries <c>federation_event</c> for the whole window
    /// each time, not just new rows since the last run), so when
    /// <c>(rule_id, natural_key, window_bucket)</c> already exists this revises the existing
    /// group's aggregates and upserts any member found this pass that wasn't recorded before —
    /// a late-arriving event (settle delay / clock drift / a flaky adapter catching up) still
    /// joins its group instead of being silently dropped. Returns <see langword="true"/> only
    /// when a brand-new group row was inserted, so the caller's "created" counter still reflects
    /// new groups, not every revision of an existing one.
    /// </summary>
    private async Task<bool> PersistGroupAsync(
        CorrelationRule rule, CorrelationGroupOutcome group, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        var (groupId, inserted) = await connection.QuerySingleAsync<(Guid Id, bool Inserted)>(
            new CommandDefinition("""
            INSERT INTO federation.correlation_group
                (rule_id, natural_key, window_bucket, confidence, member_count,
                 first_occurred_at, last_occurred_at)
            VALUES (@RuleId, @NaturalKey, @WindowBucket, @Confidence, @MemberCount,
                    @FirstOccurredAt, @LastOccurredAt)
            ON CONFLICT (rule_id, natural_key, window_bucket) DO UPDATE
                SET confidence = GREATEST(correlation_group.confidence, EXCLUDED.confidence),
                    member_count = EXCLUDED.member_count,
                    first_occurred_at = LEAST(correlation_group.first_occurred_at, EXCLUDED.first_occurred_at),
                    last_occurred_at = GREATEST(correlation_group.last_occurred_at, EXCLUDED.last_occurred_at)
            RETURNING id, (xmax = 0) AS inserted;
            """, new
        {
            RuleId = rule.Id,
            group.NaturalKey,
            group.WindowBucket,
            group.Confidence,
            MemberCount = group.Members.Count,
            group.FirstOccurredAt,
            group.LastOccurredAt,
        }, transaction, cancellationToken: ct)).ConfigureAwait(false);

        foreach (var member in group.Members)
        {
            await connection.ExecuteAsync(new CommandDefinition("""
                INSERT INTO federation.correlation_group_member
                    (group_id, federation_event_id, event_occurred_at, camera_id, source_vms_id,
                     organization_unit_id, geographic_area_id)
                VALUES (@GroupId, @EventId, @OccurredAt, @CameraId, @SourceVmsId,
                        @OrganizationUnitId, @GeographicAreaId)
                ON CONFLICT DO NOTHING;
                """, new
            {
                GroupId = groupId,
                member.EventId,
                member.OccurredAt,
                member.CameraId,
                member.SourceVmsId,
                member.OrganizationUnitId,
                member.GeographicAreaId,
            }, transaction, cancellationToken: ct)).ConfigureAwait(false);
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return inserted;
    }

    private sealed record RuleRow
    {
        public Guid Id { get; init; }
        public string Code { get; init; } = "";
        public string Name { get; init; } = "";
        public string? Description { get; init; }
        public int TimeWindowSeconds { get; init; }
        public double? SpatialRadiusMeters { get; init; }
        public double ConfidenceFloor { get; init; }
        public int RequiredSignalAgreement { get; init; }
        public bool IsActive { get; init; }
    }

    private sealed record GroupRow
    {
        public string NaturalKey { get; init; } = "";
        public DateTimeOffset WindowBucket { get; init; }
        public double Confidence { get; init; }
        public DateTimeOffset FirstOccurredAt { get; init; }
        public DateTimeOffset LastOccurredAt { get; init; }
        public string MembersJson { get; init; } = "[]";
    }

    private sealed record MemberJson
    {
        public string EventId { get; init; } = "";
        public string CameraId { get; init; } = "";
        public Guid SourceVmsId { get; init; }
        public Guid OrganizationUnitId { get; init; }
        public Guid? GeographicAreaId { get; init; }
        public string ObjectReference { get; init; } = "";
        public double? Confidence { get; init; }
        public DateTimeOffset OccurredAt { get; init; }
        public double? Latitude { get; init; }
        public double? Longitude { get; init; }
    }
}
