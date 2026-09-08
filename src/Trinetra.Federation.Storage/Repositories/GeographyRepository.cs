using Dapper;
using Npgsql;

namespace Trinetra.Federation.Storage.Repositories;

/// <summary>
/// Geographic areas — the operator-defined location hierarchy. Every level (its name and depth)
/// is defined by the operator through <c>geographic_area_types</c>; there is no fixed bottom
/// tier. A camera attaches directly to an area at any level.
/// </summary>
// CA1822 fires on the write methods now that they take their connection from the UnitOfWork
// rather than the data source. That is the point of the change, not a defect: they stay instance
// methods so one entity's operations are called the same way regardless of which need the pool.
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Performance", "CA1822:Mark members as static",
    Justification = "Write methods take their connection from the UnitOfWork by design.",
    Scope = "type")]
public sealed class GeographyRepository
{
    private readonly NpgsqlDataSource _dataSource;

    public GeographyRepository(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<IReadOnlyList<GeographicArea>> ListAreasAsync(
        Guid? parentId, bool rootsOnly, CallerContext caller, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require("geography.read");

        await using var c = await _dataSource.OpenConnectionAsync(ct);
        var rows = await c.QueryAsync<GeographicArea>(new CommandDefinition("""
            SELECT id, parent_area_id, code, name, area_type, description, status
            FROM federation.geographic_areas
            WHERE (@rootsOnly = FALSE OR parent_area_id IS NULL)
              AND (@parentId::uuid IS NULL OR parent_area_id = @parentId)
              AND (@Unscoped OR id IN (SELECT geographic_area_id
                                       FROM federation.authorized_geographic_areas(
                                           @UserId, @ApiKeyId, 'geography.read')))
            ORDER BY name;
            """, new
        {
            parentId, rootsOnly, caller.UserId, caller.ApiKeyId,
            Unscoped = Geo(caller, "geography.read"),
        }, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<GeographicArea?> GetAreaAsync(
        Guid id, CallerContext caller, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require("geography.read");

        await using var c = await _dataSource.OpenConnectionAsync(ct);
        return await c.QuerySingleOrDefaultAsync<GeographicArea>(new CommandDefinition("""
            SELECT id, parent_area_id, code, name, area_type, description, status
            FROM federation.geographic_areas
            WHERE id = @id
              AND (@Unscoped OR id IN (SELECT geographic_area_id
                                       FROM federation.authorized_geographic_areas(
                                           @UserId, @ApiKeyId, 'geography.read')));
            """, new
        {
            id, caller.UserId, caller.ApiKeyId, Unscoped = Geo(caller, "geography.read"),
        }, cancellationToken: ct));
    }

    /// <summary>The chain from an area up to its root, nearest first.</summary>
    /// <remarks>
    /// Ancestors are returned in full once the area itself is in scope. Walking upwards is how
    /// containment is displayed -- "Village X, Daskroi, Ahmedabad, Gujarat" -- and hiding the
    /// levels above the caller's scope would leave a breadcrumb that stops in mid-air. Names of
    /// parent areas are not sensitive; the cameras and events beneath them are, and those stay
    /// scoped by their own queries.
    /// </remarks>
    public async Task<IReadOnlyList<GeographicArea>> GetAncestorsAsync(
        Guid id, CallerContext caller, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);

        if (await GetAreaAsync(id, caller, ct).ConfigureAwait(false) is null)
        {
            return [];
        }

        await using var c = await _dataSource.OpenConnectionAsync(ct);
        var rows = await c.QueryAsync<GeographicArea>(new CommandDefinition("""
            SELECT g.id, g.parent_area_id, g.code, g.name, g.area_type, g.description, g.status
            FROM federation.geographic_area_ancestors(@id) a
            JOIN federation.geographic_areas g ON g.id = a.id;
            """, new { id }, cancellationToken: ct));
        return rows.ToList();
    }

    /// <summary>
    /// Inserts or updates a geographic area.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>parentIsChanging</c> is <see langword="false"/> only on a field-only update (name,
    /// description, area type) that leaves the parent as it stands. When the parent is not moving,
    /// the "parent must be ACTIVE" guard is skipped: a pure rename of an area whose parent has
    /// since been retired must still be allowed. An <c>area_type</c> change is still level-order
    /// checked against the parent's level and the levels of the area's own children by the
    /// <c>trg_geo_area_acyclic</c> trigger, which surfaces a violation as a 400.
    /// </para>
    /// </remarks>
    public async Task<Guid> UpsertAreaAsync(
        GeographicArea area, CallerContext caller, UnitOfWork work, CancellationToken ct,
        bool parentIsChanging = true)
    {
        ArgumentNullException.ThrowIfNull(area);
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require("geography.manage");

        var c = work.Connection;

        if (!Geo(caller, "geography.manage"))
        {
            // Both the area and its parent: without the parent check a scoped caller could
            // re-parent their own area under someone else's district and take it out of view.
            await RequireAreaAsync(c, work.Transaction, caller, area.ParentAreaId, ct).ConfigureAwait(false);

            if (area.Id != Guid.Empty)
            {
                await RequireAreaAsync(c, work.Transaction, caller, area.Id, ct).ConfigureAwait(false);
            }
            else if (area.ParentAreaId is null)
            {
                // A root area sits outside every existing geographic scope.
                throw new ForbiddenException("geography.manage");
            }
        }

        // A live area may not be attached under a retired one (finding 5-M8) -- but only when the
        // parent is actually being set or moved.
        if (parentIsChanging)
        {
            await RequireActiveAreaAsync(c, work.Transaction, area.ParentAreaId, "parentAreaId", ct)
                .ConfigureAwait(false);
        }

        return await c.ExecuteScalarAsync<Guid>(new CommandDefinition("""
            INSERT INTO federation.geographic_areas
                (id, parent_area_id, code, name, area_type, description, status)
            VALUES (COALESCE(NULLIF(@Id,'00000000-0000-0000-0000-000000000000'::uuid), gen_random_uuid()),
                    @ParentAreaId, @Code, @Name, @AreaType, @Description, @Status)
            ON CONFLICT (id) DO UPDATE
            SET parent_area_id = EXCLUDED.parent_area_id, code = EXCLUDED.code,
                name = EXCLUDED.name, area_type = EXCLUDED.area_type,
                description = EXCLUDED.description,
                status = EXCLUDED.status, updated_at = now()
            RETURNING id;
            """, area, work.Transaction, cancellationToken: ct));
    }

    /// <summary>
    /// Deactivates an area, resolving active child areas by the chosen strategy.
    /// </summary>
    /// <returns>Null on success; what would be affected when the strategy is Refuse.</returns>
    public async Task<DeactivationConflict?> DeactivateAreaAsync(
        Guid areaId, ChildStrategy strategy, Guid? newParentId, CallerContext caller, UnitOfWork work, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require("geography.manage");

        var c = work.Connection;

        // Cascade deactivation takes every descendant area with it, so both the area and any
        // reparent destination must be inside the caller's own geographic scope.
        if (!Geo(caller, "geography.manage"))
        {
            await RequireAreaAsync(c, work.Transaction, caller, areaId, ct).ConfigureAwait(false);
            await RequireAreaAsync(c, work.Transaction, caller, newParentId, ct).ConfigureAwait(false);
        }

        // One lock for the whole geographic hierarchy, held for the transaction. Serializes
        // concurrent deactivations; the residual ACTIVE-under-INACTIVE windows (the cascade race
        // and the non-concurrent 5-M8 create path) are neither closed here — see the NOTE(5-H2)
        // in OrganizationRepository.DeactivateUnitAsync and docs/OPERATIONS.md.
        await c.ExecuteAsync(new CommandDefinition(
            "SELECT pg_advisory_xact_lock(hashtext('federation.geographic_areas.deactivate'));",
            transaction: work.Transaction, cancellationToken: ct)).ConfigureAwait(false);

        var rootStatus = await c.ExecuteScalarAsync<string?>(new CommandDefinition("""
            SELECT status FROM federation.geographic_areas WHERE id = @areaId FOR UPDATE;
            """, new { areaId }, work.Transaction, cancellationToken: ct)).ConfigureAwait(false);

        if (rootStatus == "INACTIVE")
        {
            return null;
        }

        var areas = (await c.QueryAsync<string>(new CommandDefinition("""
            SELECT name FROM federation.geographic_areas
            WHERE parent_area_id = @areaId AND status = 'ACTIVE';
            """, new { areaId }, work.Transaction, cancellationToken: ct))).ToList();

        if (areas.Count > 0 && strategy == ChildStrategy.Refuse)
        {
            return new DeactivationConflict(areas);
        }

        if (strategy == ChildStrategy.Reparent && areas.Count > 0)
        {
            if (newParentId is null)
            {
                throw new InvalidOperationException("Reparenting requires a new parent area.");
            }

            // The destination must itself be live (finding 5-M8).
            await RequireActiveAreaAsync(c, work.Transaction, newParentId, "newParentId", ct)
                .ConfigureAwait(false);

            // Checked before anything moves: a target inside the branch being deactivated would
            // detach that subtree from the root, and every containment query would stop matching
            // it — silently removing access rather than reporting an error.
            var wouldDetach = await c.ExecuteScalarAsync<bool>(new CommandDefinition("""
                SELECT EXISTS (SELECT 1 FROM federation.geographic_area_descendants(@areaId) d
                               WHERE d.id = @newParentId);
                """, new { areaId, newParentId }, work.Transaction, cancellationToken: ct));

            if (wouldDetach)
            {
                throw new InvalidOperationException(
                    "The new parent is inside the branch being deactivated, which would detach "
                    + "that subtree from the hierarchy.");
            }

            await c.ExecuteAsync(new CommandDefinition("""
                UPDATE federation.geographic_areas SET parent_area_id = @newParentId, updated_at = now()
                WHERE parent_area_id = @areaId;
                """, new { areaId, newParentId }, work.Transaction, cancellationToken: ct));
        }

        if (strategy == ChildStrategy.Cascade)
        {
            await c.ExecuteAsync(new CommandDefinition("""
                UPDATE federation.geographic_areas SET status = 'INACTIVE', updated_at = now()
                WHERE id IN (SELECT id FROM federation.geographic_area_descendants(@areaId));
                """, new { areaId }, work.Transaction, cancellationToken: ct));
        }
        else
        {
            await c.ExecuteAsync(new CommandDefinition("""
                UPDATE federation.geographic_areas SET status = 'INACTIVE', updated_at = now()
                WHERE id = @areaId;
                """, new { areaId }, work.Transaction, cancellationToken: ct));
        }
        return null;
    }

    /// <summary>Operator-defined level names, used to render and validate the hierarchy.</summary>
    public async Task<IReadOnlyList<(string Code, string Name, int LevelOrder)>> ListAreaTypesAsync(
        CancellationToken ct)
    {
        await using var c = await _dataSource.OpenConnectionAsync(ct);
        var rows = await c.QueryAsync<(string, string, int)>(new CommandDefinition("""
            SELECT code, name, level_order FROM federation.geographic_area_types
            WHERE status = 'ACTIVE' ORDER BY level_order;
            """, cancellationToken: ct));
        return rows.ToList();
    }

    /// <summary>Whether the caller is unrestricted by GEOGRAPHY for a permission.</summary>
    /// <remarks>
    /// Not <c>CallerContext.IsUnscopedFor</c>, which answers the ORGANIZATION question. The two
    /// dimensions are independent: a group confined to one department may still reach every
    /// district, and using the organization answer here would apply one dimension's limit to the
    /// other -- either hiding areas the caller may see, or showing areas they may not.
    /// </remarks>
    private static bool Geo(CallerContext caller, string permission) =>
        caller.IsUnscopedForGeography(permission);

    /// <summary>Throws unless the caller's geographic scope covers the area.</summary>
    /// <remarks>
    /// Runs on the <see cref="UnitOfWork"/>'s connection and must enlist in its transaction —
    /// Npgsql rejects an un-enlisted command once a transaction is open, which is why a scoped
    /// caller previously got a 500 on every create and deactivate.
    /// </remarks>
    private static async Task RequireAreaAsync(
        NpgsqlConnection c, NpgsqlTransaction tx, CallerContext caller, Guid? areaId,
        CancellationToken ct)
    {
        if (areaId is null)
        {
            return;
        }

        var reachable = await c.ExecuteScalarAsync<bool>(new CommandDefinition(
            """
            SELECT EXISTS (SELECT 1 FROM federation.authorized_geographic_areas(
                               @UserId, @ApiKeyId, 'geography.manage')
                           WHERE geographic_area_id = @areaId);
            """, new { caller.UserId, caller.ApiKeyId, areaId }, tx, cancellationToken: ct));

        if (!reachable)
        {
            throw new ForbiddenException("geography.manage");
        }
    }

    /// <summary>
    /// Throws <see cref="InvalidReferenceException"/> unless the given area exists and is ACTIVE.
    /// A null id passes — it means "root" or "not supplied". Runs in the caller's transaction.
    /// </summary>
    private static async Task RequireActiveAreaAsync(
        NpgsqlConnection c, NpgsqlTransaction tx, Guid? areaId, string field, CancellationToken ct)
    {
        if (areaId is null)
        {
            return;
        }

        var active = await c.ExecuteScalarAsync<bool>(new CommandDefinition("""
            SELECT EXISTS (SELECT 1 FROM federation.geographic_areas
                           WHERE id = @areaId AND status = 'ACTIVE');
            """, new { areaId }, tx, cancellationToken: ct));

        if (!active)
        {
            throw new InvalidReferenceException(field, "does not exist or is not ACTIVE");
        }
    }
}
