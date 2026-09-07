using Dapper;
using Npgsql;

namespace Trinetra.Federation.Storage.Repositories;

/// <summary>
/// Organizations and their unit hierarchy.
/// </summary>
/// <remarks>
/// Cycle prevention and the same-organization rule are enforced by database triggers, not here.
/// A check in application code would be bypassed by the CLI, by a migration, or by anyone with a
/// psql prompt — and a cycle hangs every scope-resolution query that walks that branch.
/// </remarks>
// CA1822 fires on the write methods now that they take their connection from the UnitOfWork
// rather than the data source. That is the point of the change, not a defect: they stay instance
// methods so one entity's operations are called the same way regardless of which need the pool.
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Performance", "CA1822:Mark members as static",
    Justification = "Write methods take their connection from the UnitOfWork by design.",
    Scope = "type")]
public sealed class OrganizationRepository
{
    private readonly NpgsqlDataSource _dataSource;

    public OrganizationRepository(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    /// <summary>
    /// Organizations the caller reaches, never the whole table.
    /// </summary>
    /// <remarks>
    /// An organization is visible when the caller reaches at least one unit inside it. Before
    /// this took a <see cref="CallerContext"/> it returned every row: a user whose group was
    /// scoped to one department could enumerate every other department in the estate, which is
    /// the reconnaissance step for everything else.
    /// </remarks>
    public async Task<IReadOnlyList<Organization>> ListAsync(CallerContext caller, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require("organization.read");

        var sql = caller.IsUnscopedFor("organization.read")
            ? """
              SELECT id, code, name, organization_type, description, status
              FROM federation.organizations ORDER BY name;
              """
            : """
              SELECT id, code, name, organization_type, description, status
              FROM federation.organizations
              WHERE id IN (SELECT organization_id FROM federation.authorized_organizations(
                                @UserId, @ApiKeyId, 'organization.read'))
              ORDER BY name;
              """;

        await using var c = await _dataSource.OpenConnectionAsync(ct);
        var rows = await c.QueryAsync<Organization>(new CommandDefinition(
            sql, new { caller.UserId, caller.ApiKeyId }, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<Organization?> GetAsync(Guid id, CallerContext caller, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require("organization.read");

        await using var c = await _dataSource.OpenConnectionAsync(ct);

        // Out of scope returns null, not 403: distinguishing "does not exist" from "exists but is
        // not yours" tells an unauthorised caller which ids are real.
        return await c.QuerySingleOrDefaultAsync<Organization>(new CommandDefinition("""
            SELECT id, code, name, organization_type, description, status
            FROM federation.organizations
            WHERE id = @id
              AND (@Unscoped OR id IN (SELECT organization_id
                                       FROM federation.authorized_organizations(
                                           @UserId, @ApiKeyId, 'organization.read')));
            """, new
        {
            id, caller.UserId, caller.ApiKeyId,
            Unscoped = caller.IsUnscopedFor("organization.read"),
        }, cancellationToken: ct));
    }

    /// <summary>Creates or updates an organization. Unscoped callers only.</summary>
    /// <remarks>
    /// Creating an organization creates a new root that no existing scope reaches, so a scoped
    /// caller doing it would be manufacturing territory outside anyone's oversight -- including
    /// their own administrator's. Editing one likewise affects every department inside it.
    /// </remarks>
    public async Task<Guid> UpsertAsync(Organization org, CallerContext caller, UnitOfWork work, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(org);
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require("organization.manage");

        if (!caller.IsUnscopedFor("organization.manage"))
        {
            throw new ForbiddenException("organization.manage");
        }

        var c = work.Connection;
        return await c.ExecuteScalarAsync<Guid>(new CommandDefinition("""
            INSERT INTO federation.organizations
                (id, code, name, organization_type, description, status)
            VALUES (COALESCE(NULLIF(@Id,'00000000-0000-0000-0000-000000000000'::uuid), gen_random_uuid()),
                    @Code, @Name, @OrganizationType, @Description, @Status)
            ON CONFLICT (id) DO UPDATE
            SET code = EXCLUDED.code, name = EXCLUDED.name,
                organization_type = EXCLUDED.organization_type,
                description = EXCLUDED.description, status = EXCLUDED.status,
                updated_at = now()
            RETURNING id;
            """, org, work.Transaction, cancellationToken: ct));
    }

    // ---- Units -------------------------------------------------------------

    public async Task<IReadOnlyList<OrganizationUnit>> ListUnitsAsync(
        Guid? organizationId, CallerContext caller, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require("organization.read");

        await using var c = await _dataSource.OpenConnectionAsync(ct);
        var rows = await c.QueryAsync<OrganizationUnit>(new CommandDefinition("""
            SELECT id, organization_id, parent_unit_id, code, name, unit_type, status
            FROM federation.organization_units
            WHERE (@organizationId::uuid IS NULL OR organization_id = @organizationId)
              AND (@Unscoped OR id IN (SELECT organization_unit_id
                                       FROM federation.authorized_org_units(
                                           @UserId, @ApiKeyId, 'organization.read')))
            ORDER BY name;
            """, new
        {
            organizationId, caller.UserId, caller.ApiKeyId,
            Unscoped = caller.IsUnscopedFor("organization.read"),
        }, cancellationToken: ct));
        return rows.ToList();
    }

    /// <summary>Creates or updates a unit, within the caller's reach.</summary>
    /// <remarks>
    /// Both the unit and its parent are checked. Checking only the unit would let a scoped caller
    /// re-parent one of their own units under another department -- moving it, and everything
    /// beneath it, out of their administrator's view.
    /// </remarks>
    public async Task<Guid> UpsertUnitAsync(
        OrganizationUnit unit, CallerContext caller, UnitOfWork work, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require("organization.manage");

        var c = work.Connection;

        if (!caller.IsUnscopedFor("organization.manage"))
        {
            await RequireReachAsync(c, work.Transaction, caller, unit.ParentUnitId, ct).ConfigureAwait(false);

            if (unit.Id != Guid.Empty)
            {
                await RequireReachAsync(c, work.Transaction, caller, unit.Id, ct).ConfigureAwait(false);
            }
            else if (unit.ParentUnitId is null)
            {
                // A root unit answers to no existing scope.
                throw new ForbiddenException("organization.manage");
            }
        }

        // A live unit may not be attached under a retired one — that would put it, and everything
        // beneath it, under a parent every list and scope query treats as gone (finding 5-M8).
        await RequireActiveParentAsync(c, work.Transaction, unit.ParentUnitId, "parentUnitId", ct)
            .ConfigureAwait(false);

        return await c.ExecuteScalarAsync<Guid>(new CommandDefinition("""
            INSERT INTO federation.organization_units
                (id, organization_id, parent_unit_id, code, name, unit_type, status)
            VALUES (COALESCE(NULLIF(@Id,'00000000-0000-0000-0000-000000000000'::uuid), gen_random_uuid()),
                    @OrganizationId, @ParentUnitId, @Code, @Name, @UnitType, @Status)
            ON CONFLICT (id) DO UPDATE
            SET parent_unit_id = EXCLUDED.parent_unit_id, code = EXCLUDED.code,
                name = EXCLUDED.name, unit_type = EXCLUDED.unit_type,
                status = EXCLUDED.status, updated_at = now()
            RETURNING id;
            """, unit, work.Transaction, cancellationToken: ct));
    }

    /// <summary>
    /// Deactivates a unit, resolving active children by the chosen strategy.
    /// </summary>
    /// <returns>Null on success; the affected children when the strategy is Refuse.</returns>
    public async Task<DeactivationConflict?> DeactivateUnitAsync(
        Guid unitId, ChildStrategy strategy, Guid? newParentId, CallerContext caller, UnitOfWork work, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require("organization.manage");

        var c = work.Connection;

        // Deactivation cascades: a whole department's cameras fall out of every scope query.
        // Both ends are checked -- the unit being deactivated and, for a reparent, the
        // destination -- so a scoped caller cannot push a subtree somewhere they cannot see.
        if (!caller.IsUnscopedFor("organization.manage"))
        {
            await RequireReachAsync(c, work.Transaction, caller, unitId, ct).ConfigureAwait(false);
            await RequireReachAsync(c, work.Transaction, caller, newParentId, ct).ConfigureAwait(false);
        }

        // Serialize every deactivation of this hierarchy against every other, for the life of
        // the transaction. A per-node key would not order "deactivate ancestor" against
        // "deactivate descendant"; the races in 5-H2 need one lock for the whole tree. Released
        // automatically on commit or rollback, so the 409-refuse and exception paths need no
        // explicit unlock. Creates are deliberately not locked — see NOTE(5-H2) below.
        await c.ExecuteAsync(new CommandDefinition(
            "SELECT pg_advisory_xact_lock(hashtext('federation.organization_units.deactivate'));",
            transaction: work.Transaction, cancellationToken: ct)).ConfigureAwait(false);

        // FOR UPDATE on the root: a concurrent direct-child INSERT takes FOR KEY SHARE on this
        // row for its foreign-key check, so it blocks here until we commit. That only *orders*
        // the race — once we commit, the INSERT still lands an ACTIVE child under the now-
        // INACTIVE parent, because the create path does not check parent status. Closing that
        // needs 5-M8 (a parent-ACTIVE check on every Upsert*); see the NOTE below.
        var rootStatus = await c.ExecuteScalarAsync<string?>(new CommandDefinition("""
            SELECT status FROM federation.organization_units WHERE id = @unitId FOR UPDATE;
            """, new { unitId }, work.Transaction, cancellationToken: ct)).ConfigureAwait(false);

        // Already inactive (or a concurrent deactivation won the lock first): nothing to do.
        // A missing row falls through to the UPDATE, which hits nothing — unchanged behaviour,
        // the 404 refinement is 5-M10, out of scope here.
        if (rootStatus == "INACTIVE")
        {
            return null;
        }

        // NOTE(5-H2): an ACTIVE node can still end up under an INACTIVE ancestor two ways — a
        // grandchild inserted under a still-ACTIVE mid-tree node while an ancestor is cascaded
        // (a narrow race the lock only orders), and the non-concurrent 5-M8 path (deactivate a
        // node, then add a child under it later — the create path does not check parent status).
        // Neither is a scope escape: org_unit_descendants and authorized_org_units ignore
        // status. Detection query and remediation in docs/OPERATIONS.md; see also
        // docs/DEPARTMENT-SCHEMA.md.
        var children = (await c.QueryAsync<string>(new CommandDefinition("""
            SELECT name FROM federation.organization_units
            WHERE parent_unit_id = @unitId AND status = 'ACTIVE';
            """, new { unitId }, work.Transaction, cancellationToken: ct))).ToList();

        if (children.Count > 0 && strategy == ChildStrategy.Refuse)
        {
            return new DeactivationConflict(children, []);
        }

        if (strategy == ChildStrategy.Reparent && children.Count > 0)
        {
            if (newParentId is null)
            {
                throw new InvalidOperationException(
                    "Reparenting requires a new parent unit.");
            }

            // The destination must itself be live (finding 5-M8) — reparenting onto a retired
            // unit only moves the orphaning one level up.
            await RequireActiveParentAsync(c, work.Transaction, newParentId, "newParentId", ct)
                .ConfigureAwait(false);

            // Validated before anything moves: reparenting into the subtree being deactivated
            // would detach it from the root and make it unreachable to every scope query.
            var wouldDetach = await c.ExecuteScalarAsync<bool>(new CommandDefinition("""
                SELECT EXISTS (SELECT 1 FROM federation.org_unit_descendants(@unitId) d
                               WHERE d.id = @newParentId);
                """, new { unitId, newParentId }, work.Transaction, cancellationToken: ct));

            if (wouldDetach)
            {
                throw new InvalidOperationException(
                    "The new parent is inside the branch being deactivated, which would detach "
                    + "that subtree from the hierarchy.");
            }

            await c.ExecuteAsync(new CommandDefinition("""
                UPDATE federation.organization_units
                SET parent_unit_id = @newParentId, updated_at = now()
                WHERE parent_unit_id = @unitId;
                """, new { unitId, newParentId }, work.Transaction, cancellationToken: ct));
        }

        var sql = strategy == ChildStrategy.Cascade
            ? """
              UPDATE federation.organization_units
              SET status = 'INACTIVE', updated_at = now()
              WHERE id IN (SELECT id FROM federation.org_unit_descendants(@unitId));
              """
            : """
              UPDATE federation.organization_units
              SET status = 'INACTIVE', updated_at = now() WHERE id = @unitId;
              """;

        await c.ExecuteAsync(new CommandDefinition(sql, new { unitId }, work.Transaction, cancellationToken: ct));
        return null;
    }
    /// <summary>Throws unless the caller administers the given unit.</summary>
    /// <remarks>
    /// A null unit id passes: it means "no unit supplied", which the callers above treat
    /// separately. Rejecting it here would block every legitimate call that omits an optional
    /// parent.
    /// <para>
    /// Runs on the <see cref="UnitOfWork"/>'s connection and must enlist in its transaction —
    /// Npgsql rejects an un-enlisted command once a transaction is open, which is why a scoped
    /// caller previously got a 500 on every create and deactivate.
    /// </para>
    /// <para>
    /// Set membership against <c>authorized_org_units</c>, never <c>has_permission</c>: that
    /// function denies a whole group the moment a dimension it constrains (geography) is not
    /// passed, which is a false denial for a caller scoped on both dimensions. See invariant 12
    /// and the PR2 camera fix.
    /// </para>
    /// </remarks>
    private static async Task RequireReachAsync(
        NpgsqlConnection c, NpgsqlTransaction tx, CallerContext caller, Guid? unitId,
        CancellationToken ct)
    {
        if (unitId is null)
        {
            return;
        }

        var reachable = await c.ExecuteScalarAsync<bool>(new CommandDefinition(
            """
            SELECT EXISTS (SELECT 1 FROM federation.authorized_org_units(
                               @UserId, @ApiKeyId, 'organization.manage')
                           WHERE organization_unit_id = @unitId);
            """, new { caller.UserId, caller.ApiKeyId, unitId }, tx, cancellationToken: ct));

        if (!reachable)
        {
            throw new ForbiddenException("organization.manage");
        }
    }

    /// <summary>
    /// Throws <see cref="InvalidReferenceException"/> unless the given parent unit exists and is
    /// ACTIVE. A null id passes — it means "root" or "not supplied". Runs in the caller's
    /// transaction so it sees uncommitted work.
    /// </summary>
    private static async Task RequireActiveParentAsync(
        NpgsqlConnection c, NpgsqlTransaction tx, Guid? parentUnitId, string field,
        CancellationToken ct)
    {
        if (parentUnitId is null)
        {
            return;
        }

        var active = await c.ExecuteScalarAsync<bool>(new CommandDefinition("""
            SELECT EXISTS (SELECT 1 FROM federation.organization_units
                           WHERE id = @parentUnitId AND status = 'ACTIVE');
            """, new { parentUnitId }, tx, cancellationToken: ct));

        if (!active)
        {
            throw new InvalidReferenceException(field, "does not exist or is not ACTIVE");
        }
    }

}
