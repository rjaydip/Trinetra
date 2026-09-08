using Dapper;
using Npgsql;
using Trinetra.Federation.Core.Model;

namespace Trinetra.Federation.Storage.Repositories;

/// <summary>
/// Connector targets, scoped to what the caller may reach.
/// </summary>
/// <remarks>
/// <para>
/// Every read and write is constrained on <b>both</b> scope dimensions, ANDed and each with its
/// own unscoped flag (<c>CLAUDE.md</c> invariant 12): the organization dimension against
/// <c>connector_target.organization_unit_id</c>, the geographic dimension against
/// <c>connector_target.geographic_area_id</c>. A target's <c>geographic_area_id</c> is nullable,
/// so a target with no area has no geographic key and the geo dimension cannot constrain it —
/// matching <c>CameraRepository.RequirePlacementAsync</c>. The organization dimension always applies.
/// </para>
/// <para>
/// The scope predicate is the <b>database's</b>, not a filter applied after the fact — one
/// forgotten check would otherwise expose another department's estate with nothing to report it.
/// </para>
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
        t.id, t.code, t.organization_unit_id, t.geographic_area_id, t.display_name,
        t.vendor::text AS vendor, t.runtime_class::text AS runtime_class,
        t.endpoint, t.credential_reference, t.verify_tls, t.state::text AS state,
        t.rate_limit_per_second, t.rate_limit_burst,
        t.inventory_poll_seconds, t.status_poll_seconds, t.event_poll_seconds,
        t.max_concurrent_requests, t.expected_camera_count
        """;

    // The dual-dimension scope predicate, parameterised by the permission the caller exercises.
    // Both dimensions are ANDed; each is bypassed only by ITS OWN unscoped flag (invariant 12).
    // A target with no area (geographic_area_id IS NULL) has no geographic key, so the geo
    // dimension does not apply to it — the organization dimension still does. `alias` is the table reference the
    // predicate hangs off (`t` in a SELECT, `connector_target` in an UPDATE/DELETE).
    private static string Scope(string alias, string permission) => $"""
        (@UnscopedOrg OR {alias}.organization_unit_id IN (
            SELECT organization_unit_id FROM federation.authorized_org_units(
                p_user_id => @UserId, p_api_key_id => @ApiKeyId, p_permission => @Perm)))
        AND (@UnscopedGeo OR {alias}.geographic_area_id IS NULL OR {alias}.geographic_area_id IN (
            SELECT geographic_area_id FROM federation.authorized_geographic_areas(
                p_user_id => @UserId, p_api_key_id => @ApiKeyId, p_permission => @Perm)))
        """;

    private static DynamicParameters ScopeArgs(CallerContext caller, string permission)
    {
        var p = new DynamicParameters();
        p.Add("UserId", caller.UserId);
        p.Add("ApiKeyId", caller.ApiKeyId);
        p.Add("Perm", permission);
        p.Add("UnscopedOrg", caller.IsUnscopedFor(permission));
        p.Add("UnscopedGeo", caller.IsUnscopedForGeography(permission));
        return p;
    }

    private readonly NpgsqlDataSource _dataSource;

    public ConnectorTargetRepository(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<IReadOnlyList<ConnectorTarget>> ListAsync(
        CallerContext caller, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require("vms.read");

        // One SQL shape, deliberately. For an unscoped caller both flags are true, so each
        // dimension's `@Unscoped OR <set membership>` short-circuits to TRUE per row and the
        // `authorized_*` functions are never evaluated — no plan-folding hazard, just a
        // constant-time check on the same scan. For a scoped caller an empty authorized set
        // matches nothing, which is the safe reading: "reaches nothing", never "reaches
        // everything" (that inversion is how an admin check becomes an estate-wide leak).
        var args = ScopeArgs(caller, "vms.read");

        await using var c = await _dataSource.OpenConnectionAsync(ct);
        var rows = await c.QueryAsync<TargetRow>(new CommandDefinition($"""
            SELECT {Columns} FROM federation.connector_target t
            WHERE ({Scope("t", "vms.read")})
            ORDER BY t.code;
            """, args, cancellationToken: ct));

        return rows.Select(r => r.ToDomain()).ToList();
    }

    public async Task<ConnectorTarget?> GetAsync(
        Guid id, CallerContext caller, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require("vms.read");

        var args = ScopeArgs(caller, "vms.read");
        args.Add("id", id);

        await using var c = await _dataSource.OpenConnectionAsync(ct);

        var row = await c.QuerySingleOrDefaultAsync<TargetRow>(new CommandDefinition($"""
            SELECT {Columns} FROM federation.connector_target t
            WHERE t.id = @id
              AND ({Scope("t", "vms.read")});
            """, args, cancellationToken: ct));

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

        // Referenced records must exist AND be ACTIVE. A foreign key alone catches "does not
        // exist" (400 via ConstraintViolationExceptionHandler); it says nothing about status, so
        // without this a target could attach to a deactivated unit or area and silently drop out
        // of every scope query. Runs unconditionally — including for an unscoped caller, who
        // otherwise got no validation here at all.
        var refs = await c.QuerySingleAsync<ReferenceCheckRow>(new CommandDefinition(
            """
            SELECT
                EXISTS (SELECT 1 FROM federation.organization_units
                        WHERE id = @OrgUnit AND status = 'ACTIVE') AS org_active,
                (@GeographicAreaId::uuid IS NULL OR EXISTS (
                    SELECT 1 FROM federation.geographic_areas
                    WHERE id = @GeographicAreaId AND status = 'ACTIVE')
                ) AS area_active;
            """, new { OrgUnit = target.OrganizationUnitId, target.GeographicAreaId },
            work.Transaction, cancellationToken: ct));

        if (!refs.OrgActive)
        {
            throw new InvalidReferenceException(
                "organizationUnitId", "does not exist or is not ACTIVE");
        }

        if (!refs.AreaActive)
        {
            throw new InvalidReferenceException(
                "geographicAreaId", "does not exist or is not ACTIVE");
        }

        // A caller must be able to reach BOTH the organization unit and the geographic area
        // they are assigning the target to — otherwise they could park a target in another
        // department, or another district, and read its events. Each dimension is skipped only
        // by its own unscoped flag; a target with no area has no geography to check.
        var writePermission = target.Id == Guid.Empty ? "vms.create" : "vms.update";
        var orgOk = caller.IsUnscopedFor(writePermission);
        var geoOk = target.GeographicAreaId is null || caller.IsUnscopedForGeography(writePermission);

        if (!orgOk || !geoOk)
        {
            // Each dimension checked against its OWN authorized_* set, never has_permission():
            // that function requires every dimension a group constrains to be supplied, so a
            // group scoped on BOTH organization and geography would fail an org-only call
            // simply because no geography was passed — a false denial, not a real one.
            var permitted = await c.ExecuteScalarAsync<bool>(new CommandDefinition(
                """
                SELECT
                    (@OrgOk OR EXISTS (
                        SELECT 1 FROM federation.authorized_org_units(
                            p_user_id => @UserId, p_api_key_id => @ApiKeyId, p_permission => @Permission)
                        WHERE organization_unit_id = @OrgUnit))
                    AND
                    (@GeoOk OR @GeographicAreaId::uuid IS NULL OR EXISTS (
                        SELECT 1 FROM federation.authorized_geographic_areas(
                            p_user_id => @UserId, p_api_key_id => @ApiKeyId,
                            p_permission => @Permission)
                        WHERE geographic_area_id = @GeographicAreaId));
                """,
                new
                {
                    caller.UserId,
                    caller.ApiKeyId,
                    Permission = writePermission,
                    OrgOk = orgOk,
                    GeoOk = geoOk,
                    OrgUnit = target.OrganizationUnitId,
                    target.GeographicAreaId,
                }, work.Transaction, cancellationToken: ct));

            if (!permitted)
            {
                throw new ForbiddenException(writePermission);
            }
        }

        return await c.ExecuteScalarAsync<Guid>(new CommandDefinition("""
            INSERT INTO federation.connector_target
                (id, code, organization_unit_id, geographic_area_id, display_name, vendor, runtime_class,
                 endpoint, credential_reference, verify_tls, state,
                 rate_limit_per_second, rate_limit_burst, inventory_poll_seconds,
                 status_poll_seconds, event_poll_seconds, max_concurrent_requests,
                 expected_camera_count, created_by, updated_by)
            VALUES (COALESCE(NULLIF(@Id,'00000000-0000-0000-0000-000000000000'::uuid), gen_random_uuid()),
                    @Code, @OrganizationUnitId, @GeographicAreaId, @DisplayName,
                    @Vendor::federation.vendor_kind, @RuntimeClass::federation.runtime_class,
                    @Endpoint, @CredentialReference, @VerifyTls, @State::federation.target_state,
                    @RateLimitPerSecond, @RateLimitBurst, @InventoryPollSeconds,
                    @StatusPollSeconds, @EventPollSeconds, @MaxConcurrentRequests,
                    @ExpectedCameraCount, @ActorId, @ActorId)
            ON CONFLICT (id) DO UPDATE
            -- state is deliberately NOT in this list. A replace must never change it — see
            -- VmsEndpoints.ReplaceAsync's doc — so state is written only by SetStateAsync from
            -- here on; the INSERT branch above still writes it (defaults to Active on register).
            SET code = EXCLUDED.code,
                organization_unit_id = EXCLUDED.organization_unit_id,
                geographic_area_id = EXCLUDED.geographic_area_id, display_name = EXCLUDED.display_name,
                vendor = EXCLUDED.vendor, runtime_class = EXCLUDED.runtime_class,
                endpoint = EXCLUDED.endpoint,
                credential_reference = EXCLUDED.credential_reference,
                verify_tls = EXCLUDED.verify_tls,
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
            target.Id, target.Code, target.OrganizationUnitId, target.GeographicAreaId, target.DisplayName,
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

        var args = ScopeArgs(caller, "vms.update");
        args.Add("id", id);
        args.Add("State", state.ToString());
        args.Add("ActorId", caller.UserId);

        var c = work.Connection;
        var affected = await c.ExecuteAsync(new CommandDefinition($"""
            UPDATE federation.connector_target
            SET state = @State::federation.target_state,
                leased_by = NULL, lease_expires_at = NULL,
                updated_by = @ActorId, updated_at = now()
            WHERE id = @id
              AND ({Scope("connector_target", "vms.update")});
            """, args, work.Transaction, cancellationToken: ct));

        return affected > 0;
    }

    /// <summary>
    /// Hard-deletes a target the caller can reach. Gated on <c>vms.delete</c>, distinct from
    /// <c>vms.update</c> — a destructive, irreversible cascade should not share a permission
    /// with routine edits (invariant: <c>camera.delete</c> is the same split for the camera
    /// registry). The caller must already have set the target's state to non-Active first — see
    /// <c>VmsEndpoints.RemoveAsync</c>, which enforces that before calling this.
    /// </summary>
    public async Task<bool> DeleteAsync(
        Guid id, CallerContext caller, UnitOfWork work, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(work);
        caller.Require("vms.delete");

        var args = ScopeArgs(caller, "vms.delete");
        args.Add("id", id);

        var c = work.Connection;
        var affected = await c.ExecuteAsync(new CommandDefinition($"""
            DELETE FROM federation.connector_target
            WHERE id = @id
              AND ({Scope("connector_target", "vms.delete")});
            """, args, work.Transaction, cancellationToken: ct));

        return affected > 0;
    }

    private sealed class ReferenceCheckRow
    {
        public bool OrgActive { get; init; }
        public bool AreaActive { get; init; }
    }

    private sealed class TargetRow
    {
        public Guid Id { get; init; }
        public string Code { get; init; } = "";
        public Guid OrganizationUnitId { get; init; }
        public Guid? GeographicAreaId { get; init; }
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
            GeographicAreaId = GeographicAreaId,
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
