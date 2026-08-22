using System.Data;
using Dapper;
using Npgsql;
using Trinetra.Federation.Core.Model;

namespace Trinetra.Federation.Storage;

/// <summary>
/// Lease-based work assignment. Decides which worker owns which connector target.
/// </summary>
/// <remarks>
/// <para>
/// Deployment is bare metal with no container orchestrator, but must scale horizontally. So
/// the platform provides its own assignment and failover using PostgreSQL — already a hard
/// dependency — rather than adding ZooKeeper, etcd or Consul for one job.
/// </para>
/// <para>
/// Properties this buys, per architecture §4:
/// <list type="bullet">
/// <item><b>Rebalancing is emergent.</b> A new worker claims unleased targets; a dead worker's
/// targets are reclaimed when its lease expires. No coordinator, no leader election, no
/// split-brain.</item>
/// <item><b>Bounded per-worker load.</b> <c>maxTargets</c> stops one process claiming the whole
/// estate and then dying with it.</item>
/// <item><b>Failover latency equals the lease TTL.</b> That is the accepted trade-off; during
/// that window the target's events are not being pulled, and are gap-filled from the cursor on
/// reclaim.</item>
/// </list>
/// </para>
/// </remarks>
public sealed class LeaseStore
{
    private readonly NpgsqlDataSource _dataSource;

    public LeaseStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    /// <summary>
    /// Atomically claim up to <paramref name="maxTargets"/> unleased targets for this worker.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>FOR UPDATE SKIP LOCKED</c> is what makes this safe under concurrency: workers
    /// starting simultaneously step over each other's in-flight rows instead of serialising on
    /// them or deadlocking. Without SKIP LOCKED, a fleet restart — the exact moment every
    /// worker claims at once — would be the worst case rather than a non-event.
    /// </para>
    /// <para>
    /// Ordering by <c>last_claimed_at NULLS FIRST</c> makes never-claimed targets win, so a
    /// newly added site starts federating promptly instead of waiting behind established ones.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<ConnectorTarget>> ClaimAsync(
        string workerId,
        int maxTargets,
        TimeSpan leaseTtl,
        RuntimeClass runtimeClass,
        IReadOnlyCollection<VendorKind>? vendorFilter,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxTargets, 1);

        const string sql = """
            WITH claimable AS (
                SELECT id
                FROM federation.connector_target
                WHERE state = 'Active'
                  AND runtime_class = @RuntimeClass::federation.runtime_class
                  AND (lease_expires_at IS NULL OR lease_expires_at < now())
                  AND (@VendorFilter::federation.vendor_kind[] IS NULL
                       OR vendor = ANY(@VendorFilter::federation.vendor_kind[]))
                ORDER BY last_claimed_at NULLS FIRST
                LIMIT @MaxTargets
                FOR UPDATE SKIP LOCKED
            )
            UPDATE federation.connector_target t
            SET leased_by = @WorkerId,
                lease_expires_at = now() + @LeaseTtl,
                last_claimed_at = now(),
                updated_at = now()
            FROM claimable c
            WHERE t.id = c.id
            -- Enum columns are cast to text rather than mapped as Npgsql enums. Npgsql cannot
            -- read an unmapped PostgreSQL enum, and registering mappings would couple the
            -- domain enums to the exact database type names. TargetRow parses them back.
            RETURNING t.id, t.code, t.organization_unit_id, t.site_id, t.display_name,
                      t.vendor::text        AS vendor,
                      t.runtime_class::text AS runtime_class,
                      t.endpoint, t.credential_reference, t.verify_tls,
                      t.state::text         AS state,
                      t.rate_limit_per_second, t.rate_limit_burst,
                      t.inventory_poll_seconds, t.status_poll_seconds, t.event_poll_seconds,
                      t.max_concurrent_requests, t.expected_camera_count;
            """;

        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        var rows = await conn.QueryAsync<TargetRow>(new CommandDefinition(
            sql,
            new
            {
                WorkerId = workerId,
                MaxTargets = maxTargets,
                LeaseTtl = leaseTtl,
                RuntimeClass = runtimeClass.ToString(),
                VendorFilter = vendorFilter?.Select(v => v.ToString()).ToArray(),
            },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return rows.Select(r => r.ToDomain()).ToList();
    }

    /// <summary>
    /// Extend this worker's leases. Call at roughly <c>leaseTtl / 3</c>.
    /// </summary>
    /// <remarks>
    /// Renewing only rows still owned by this worker matters: if a worker was slow enough that
    /// its lease already expired and another worker claimed the target, this must not steal it
    /// back and produce two owners polling one VMS.
    /// </remarks>
    /// <returns>Targets still held. A count below expectation means leases were lost.</returns>
    public async Task<IReadOnlyList<Guid>> RenewAsync(
        string workerId,
        TimeSpan leaseTtl,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE federation.connector_target
            SET lease_expires_at = now() + @LeaseTtl,
                updated_at = now()
            WHERE leased_by = @WorkerId
              AND lease_expires_at >= now()
            RETURNING id;
            """;

        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        var held = await conn.QueryAsync<Guid>(new CommandDefinition(
            sql,
            new { WorkerId = workerId, LeaseTtl = leaseTtl },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return held.ToList();
    }

    /// <summary>
    /// Release leases on clean shutdown, so targets are picked up immediately rather than
    /// after the TTL. This is the difference between a rolling restart costing seconds and
    /// costing a full lease window per worker.
    /// </summary>
    public async Task<int> ReleaseAllAsync(string workerId, CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE federation.connector_target
            SET leased_by = NULL, lease_expires_at = NULL, updated_at = now()
            WHERE leased_by = @WorkerId;
            """;

        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        return await conn.ExecuteAsync(new CommandDefinition(
            sql, new { WorkerId = workerId }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Quarantine a target so it stops consuming worker capacity.
    /// </summary>
    /// <remarks>
    /// Used for permanent conditions — bad credentials, misconfiguration — which retrying
    /// cannot fix. Requires an operator to clear, deliberately: silently auto-recovering a
    /// quarantined target is how a locked-out integration account gets re-locked on a loop.
    /// </remarks>
    public async Task QuarantineAsync(
        Guid targetId, string reason, CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE federation.connector_target
            SET state = 'Quarantined', leased_by = NULL, lease_expires_at = NULL, updated_at = now()
            WHERE id = @TargetId;

            INSERT INTO federation.connector_health
                (target_id, checked_at, status, last_error)
            VALUES (@TargetId, now(), 'AuthFailed', @Reason)
            ON CONFLICT (target_id, checked_at) DO NOTHING;
            """;

        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await conn.ExecuteAsync(new CommandDefinition(
            sql, new { TargetId = targetId, Reason = reason },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>Dapper projection. Kept separate so the domain record stays free of
    /// persistence concerns and snake_case column names.</summary>
    private sealed class TargetRow
    {
        public Guid Id { get; init; }
        public string Code { get; init; } = "";
        public Guid OrganizationUnitId { get; init; }
        public Guid? SiteId { get; init; }
        public string DisplayName { get; init; } = "";
        public string Vendor { get; init; } = "";
        public string RuntimeClass { get; init; } = "";
        public string Endpoint { get; init; } = "";
        public string CredentialReference { get; init; } = "";
        public bool VerifyTls { get; init; }
        public string State { get; init; } = "";
        public double RateLimitPerSecond { get; init; }
        public int RateLimitBurst { get; init; }
        public int InventoryPollSeconds { get; init; }
        public int StatusPollSeconds { get; init; }
        public int EventPollSeconds { get; init; }
        public int MaxConcurrentRequests { get; init; }
        public int? ExpectedCameraCount { get; init; }

        public ConnectorTarget ToDomain() => new()
        {
            Id = Id,
            Code = Code,
            OrganizationUnitId = OrganizationUnitId,
            SiteId = SiteId,
            DisplayName = DisplayName,
            Vendor = Enum.Parse<VendorKind>(Vendor),
            RuntimeClass = Enum.Parse<RuntimeClass>(RuntimeClass),
            Endpoint = Endpoint,
            CredentialReference = CredentialReference,
            VerifyTls = VerifyTls,
            State = Enum.Parse<TargetState>(State),
            RateLimitPerSecond = RateLimitPerSecond,
            RateLimitBurst = RateLimitBurst,
            InventoryPollInterval = TimeSpan.FromSeconds(InventoryPollSeconds),
            StatusPollInterval = TimeSpan.FromSeconds(StatusPollSeconds),
            EventPollInterval = TimeSpan.FromSeconds(EventPollSeconds),
            MaxConcurrentRequests = MaxConcurrentRequests,
            ExpectedCameraCount = ExpectedCameraCount,
        };
    }
}
