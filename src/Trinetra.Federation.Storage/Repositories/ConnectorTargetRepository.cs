using Dapper;
using Npgsql;
using Trinetra.Federation.Core.Model;

namespace Trinetra.Federation.Storage.Repositories;

/// <summary>
/// Connector targets, scoped to what the caller may reach.
/// </summary>
/// <remarks>
/// Every read joins against <c>authorized_org_units</c> so the <b>database</b> returns only
/// permitted rows — <c>RBAC-LOGICAL-FLOW.md</c> §20's requirement. Filtering after the fact
/// would mean one forgotten check exposes another department's estate, and nothing would report it.
/// </remarks>
// CA1822 fires on the write methods now that they take their connection from the UnitOfWork
// rather than the data source. That is the point of the change, not a defect: they stay instance
// methods so one entity's operations are called the same way regardless of which of them happen
// to need the pool.
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Performance", "CA1822:Mark members as static",
    Justification = "Write methods take their connection from the UnitOfWork by design.",
    Scope = "type")]
public sealed class ConnectorTargetRepository
{
    private const string Columns = """
        t.id, t.code, t.organization_unit_id, t.site_id, t.display_name,
        t.vendor::text AS vendor, t.runtime_class::text AS runtime_class,
        t.endpoint, t.credential_reference, t.verify_tls, t.state::text AS state,
        t.rate_limit_per_second, t.rate_limit_burst,
        t.inventory_poll_seconds, t.status_poll_seconds, t.event_poll_seconds,
        t.max_concurrent_requests, t.expected_camera_count
        """;

    private readonly NpgsqlDataSource _dataSource;

    public ConnectorTargetRepository(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<IReadOnlyList<ConnectorTarget>> ListAsync(
        CallerContext caller, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require("vms.read");

        // The unscoped branch is explicit rather than an empty-set special case: an empty
        // authorized set means "reaches nothing", and silently treating it as "reaches
        // everything" is how an administrator check becomes an estate-wide leak.
        var sql = caller.IsUnscopedFor("vms.read")
            ? $"SELECT {Columns} FROM federation.connector_target t ORDER BY t.code;"
            : $"""
               SELECT {Columns} FROM federation.connector_target t
               WHERE t.organization_unit_id IN (
                   SELECT organization_unit_id
                   FROM federation.authorized_org_units(
                            p_user_id => @UserId, p_api_key_id => @ApiKeyId,
                            p_permission => 'vms.read'))
               ORDER BY t.code;
               """;

        await using var c = await _dataSource.OpenConnectionAsync(ct);
        var rows = await c.QueryAsync<TargetRow>(new CommandDefinition(
            sql, new { caller.UserId, caller.ApiKeyId }, cancellationToken: ct));

        return rows.Select(r => r.ToDomain()).ToList();
    }

    public async Task<ConnectorTarget?> GetAsync(
        Guid id, CallerContext caller, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require("vms.read");

        await using var c = await _dataSource.OpenConnectionAsync(ct);

        var row = await c.QuerySingleOrDefaultAsync<TargetRow>(new CommandDefinition($"""
            SELECT {Columns} FROM federation.connector_target t
            WHERE t.id = @id
              AND (@Unscoped OR federation.has_permission(
                       p_user_id => @UserId, p_api_key_id => @ApiKeyId,
                       p_permission => 'vms.read',
                       p_organization_unit_id => t.organization_unit_id,
                       p_geographic_area_id =>
                           (SELECT s.geographic_area_id FROM federation.sites s WHERE s.id = t.site_id)));
            """, new
        {
            id, caller.UserId, caller.ApiKeyId, Unscoped = caller.IsUnscopedFor("vms.read"),
        }, cancellationToken: ct));

        // A target the caller cannot reach returns null, not a 403. Distinguishing "does not
        // exist" from "exists but is not yours" tells an unauthorised caller which target ids
        // are real.
        return row?.ToDomain();
    }

    public async Task<Guid> UpsertAsync(
        ConnectorTarget target, CallerContext caller, UnitOfWork work, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(work);
        caller.Require(target.Id == Guid.Empty ? "vms.create" : "vms.update");

        var c = work.Connection;

        // A caller must be able to reach the organization unit they are assigning the target to,
        // or they could park a target in another department and read its events.
        var writePermission = target.Id == Guid.Empty ? "vms.create" : "vms.update";

        if (!caller.IsUnscopedFor(writePermission))
        {
            var permitted = await c.ExecuteScalarAsync<bool>(new CommandDefinition(
                """
                SELECT federation.has_permission(
                    p_user_id => @UserId, p_api_key_id => @ApiKeyId,
                    p_permission => @Permission, p_organization_unit_id => @OrgUnit);
                """,
                new
                {
                    caller.UserId,
                    caller.ApiKeyId,
                    Permission = writePermission,
                    OrgUnit = target.OrganizationUnitId,
                }, work.Transaction, cancellationToken: ct));

            if (!permitted)
            {
                throw new ForbiddenException("vms.update");
            }
        }

        return await c.ExecuteScalarAsync<Guid>(new CommandDefinition("""
            INSERT INTO federation.connector_target
                (id, code, organization_unit_id, site_id, display_name, vendor, runtime_class,
                 endpoint, credential_reference, verify_tls, state,
                 rate_limit_per_second, rate_limit_burst, inventory_poll_seconds,
                 status_poll_seconds, event_poll_seconds, max_concurrent_requests,
                 expected_camera_count, created_by, updated_by)
            VALUES (COALESCE(NULLIF(@Id,'00000000-0000-0000-0000-000000000000'::uuid), gen_random_uuid()),
                    @Code, @OrganizationUnitId, @SiteId, @DisplayName,
                    @Vendor::federation.vendor_kind, @RuntimeClass::federation.runtime_class,
                    @Endpoint, @CredentialReference, @VerifyTls, @State::federation.target_state,
                    @RateLimitPerSecond, @RateLimitBurst, @InventoryPollSeconds,
                    @StatusPollSeconds, @EventPollSeconds, @MaxConcurrentRequests,
                    @ExpectedCameraCount, @ActorId, @ActorId)
            ON CONFLICT (id) DO UPDATE
            SET code = EXCLUDED.code,
                organization_unit_id = EXCLUDED.organization_unit_id,
                site_id = EXCLUDED.site_id, display_name = EXCLUDED.display_name,
                vendor = EXCLUDED.vendor, runtime_class = EXCLUDED.runtime_class,
                endpoint = EXCLUDED.endpoint,
                credential_reference = EXCLUDED.credential_reference,
                verify_tls = EXCLUDED.verify_tls, state = EXCLUDED.state,
                rate_limit_per_second = EXCLUDED.rate_limit_per_second,
                rate_limit_burst = EXCLUDED.rate_limit_burst,
                inventory_poll_seconds = EXCLUDED.inventory_poll_seconds,
                status_poll_seconds = EXCLUDED.status_poll_seconds,
                event_poll_seconds = EXCLUDED.event_poll_seconds,
                max_concurrent_requests = EXCLUDED.max_concurrent_requests,
                expected_camera_count = EXCLUDED.expected_camera_count,
                updated_by = EXCLUDED.updated_by, updated_at = now()
            RETURNING id;
            """, new
        {
            target.Id, target.Code, target.OrganizationUnitId, target.SiteId, target.DisplayName,
            Vendor = target.Vendor.ToString(),
            RuntimeClass = target.RuntimeClass.ToString(),
            target.Endpoint, target.CredentialReference, target.VerifyTls,
            State = target.State.ToString(),
            target.RateLimitPerSecond, target.RateLimitBurst,
            InventoryPollSeconds = (int)target.InventoryPollInterval.TotalSeconds,
            StatusPollSeconds = (int)target.StatusPollInterval.TotalSeconds,
            EventPollSeconds = (int)target.EventPollInterval.TotalSeconds,
            target.MaxConcurrentRequests, target.ExpectedCameraCount,
            ActorId = caller.UserId,
        }, work.Transaction, cancellationToken: ct));
    }

    /// <summary>
    /// Changes a target's state — enable, disable, or clear a quarantine.
    /// </summary>
    /// <remarks>
    /// Clearing a quarantine is deliberately an explicit operator action. Auto-recovery is what
    /// re-locks an integration account in a loop: the credential is still wrong, so the worker
    /// retries, fails, and quarantines again indefinitely.
    /// </remarks>
    public async Task<bool> SetStateAsync(
        Guid id, TargetState state, CallerContext caller, UnitOfWork work, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(work);
        caller.Require("vms.update");

        var c = work.Connection;
        var affected = await c.ExecuteAsync(new CommandDefinition("""
            UPDATE federation.connector_target
            SET state = @State::federation.target_state,
                leased_by = NULL, lease_expires_at = NULL,
                updated_by = @ActorId, updated_at = now()
            WHERE id = @id
              AND (@Unscoped OR federation.has_permission(
                       p_user_id => @UserId, p_api_key_id => @ApiKeyId,
                       p_permission => 'vms.update',
                       p_organization_unit_id => organization_unit_id));
            """, new
        {
            id, State = state.ToString(), ActorId = caller.UserId,
            caller.UserId, caller.ApiKeyId, Unscoped = caller.IsUnscopedFor("vms.update"),
        }, work.Transaction, cancellationToken: ct));

        return affected > 0;
    }

    public async Task<bool> DeleteAsync(
        Guid id, CallerContext caller, UnitOfWork work, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(work);
        caller.Require("vms.update");

        var c = work.Connection;
        var affected = await c.ExecuteAsync(new CommandDefinition("""
            DELETE FROM federation.connector_target
            WHERE id = @id
              AND (@Unscoped OR federation.has_permission(
                       p_user_id => @UserId, p_api_key_id => @ApiKeyId,
                       p_permission => 'vms.update',
                       p_organization_unit_id => organization_unit_id));
            """, new
        {
            id, caller.UserId, caller.ApiKeyId, Unscoped = caller.IsUnscopedFor("vms.update"),
        }, work.Transaction, cancellationToken: ct));

        return affected > 0;
    }

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
