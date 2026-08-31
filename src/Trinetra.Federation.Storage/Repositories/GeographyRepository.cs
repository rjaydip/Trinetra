using Dapper;
using Npgsql;

namespace Trinetra.Federation.Storage.Repositories;

/// <summary>
/// Geographic areas and sites — the operator-defined location hierarchy.
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
            SELECT id, parent_area_id, code, name, area_type, status
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
            SELECT id, parent_area_id, code, name, area_type, status
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
            SELECT g.id, g.parent_area_id, g.code, g.name, g.area_type, g.status
            FROM federation.geographic_area_ancestors(@id) a
            JOIN federation.geographic_areas g ON g.id = a.id;
            """, new { id }, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<Guid> UpsertAreaAsync(
        GeographicArea area, CallerContext caller, UnitOfWork work, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(area);
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require("geography.manage");

        var c = work.Connection;

        if (!Geo(caller, "geography.manage"))
        {
            // Both the area and its parent: without the parent check a scoped caller could
            // re-parent their own area under someone else's district and take it out of view.
            await RequireAreaAsync(c, caller, area.ParentAreaId, ct).ConfigureAwait(false);

            if (area.Id != Guid.Empty)
            {
                await RequireAreaAsync(c, caller, area.Id, ct).ConfigureAwait(false);
            }
            else if (area.ParentAreaId is null)
            {
                // A root area sits outside every existing geographic scope.
                throw new ForbiddenException("geography.manage");
            }
        }

        return await c.ExecuteScalarAsync<Guid>(new CommandDefinition("""
            INSERT INTO federation.geographic_areas
                (id, parent_area_id, code, name, area_type, status)
            VALUES (COALESCE(NULLIF(@Id,'00000000-0000-0000-0000-000000000000'::uuid), gen_random_uuid()),
                    @ParentAreaId, @Code, @Name, @AreaType, @Status)
            ON CONFLICT (id) DO UPDATE
            SET parent_area_id = EXCLUDED.parent_area_id, code = EXCLUDED.code,
                name = EXCLUDED.name, area_type = EXCLUDED.area_type,
                status = EXCLUDED.status, updated_at = now()
            RETURNING id;
            """, area, work.Transaction, cancellationToken: ct));
    }

    /// <summary>
    /// Deactivates an area, resolving active children and sites by the chosen strategy.
    /// </summary>
    /// <returns>Null on success; what would be affected when the strategy is Refuse.</returns>
    public async Task<DeactivationConflict?> DeactivateAreaAsync(
        Guid areaId, ChildStrategy strategy, Guid? newParentId, CallerContext caller, UnitOfWork work, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require("geography.manage");

        var c = work.Connection;

        // Cascade deactivation takes every descendant area and site with it, so both the area
        // and any reparent destination must be inside the caller's own geographic scope.
        if (!Geo(caller, "geography.manage"))
        {
            await RequireAreaAsync(c, caller, areaId, ct).ConfigureAwait(false);
            await RequireAreaAsync(c, caller, newParentId, ct).ConfigureAwait(false);
        }

        var areas = (await c.QueryAsync<string>(new CommandDefinition("""
            SELECT name FROM federation.geographic_areas
            WHERE parent_area_id = @areaId AND status = 'ACTIVE';
            """, new { areaId }, work.Transaction, cancellationToken: ct))).ToList();

        var sites = (await c.QueryAsync<string>(new CommandDefinition("""
            SELECT name FROM federation.sites
            WHERE geographic_area_id = @areaId AND status = 'ACTIVE';
            """, new { areaId }, work.Transaction, cancellationToken: ct))).ToList();

        if ((areas.Count > 0 || sites.Count > 0) && strategy == ChildStrategy.Refuse)
        {
            return new DeactivationConflict(areas, sites);
        }

        if (strategy == ChildStrategy.Reparent && (areas.Count > 0 || sites.Count > 0))
        {
            if (newParentId is null)
            {
                throw new InvalidOperationException("Reparenting requires a new parent area.");
            }

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
                UPDATE federation.sites SET geographic_area_id = @newParentId, updated_at = now()
                WHERE geographic_area_id = @areaId;
                """, new { areaId, newParentId }, work.Transaction, cancellationToken: ct));
        }

        if (strategy == ChildStrategy.Cascade)
        {
            await c.ExecuteAsync(new CommandDefinition("""
                UPDATE federation.sites SET status = 'INACTIVE', updated_at = now()
                WHERE geographic_area_id IN (SELECT id FROM federation.geographic_area_descendants(@areaId));

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

    // ---- Sites -------------------------------------------------------------

    public async Task<IReadOnlyList<Site>> ListSitesAsync(
        Guid? areaId, CallerContext caller, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require("geography.read");

        await using var c = await _dataSource.OpenConnectionAsync(ct);
        var rows = await c.QueryAsync<Site>(new CommandDefinition("""
            SELECT id, code, name, geographic_area_id, site_type, address,
                   latitude, longitude, status
            FROM federation.sites
            WHERE (@areaId::uuid IS NULL OR geographic_area_id = @areaId)
              AND (@Unscoped OR geographic_area_id IN (
                       SELECT geographic_area_id
                       FROM federation.authorized_geographic_areas(
                           @UserId, @ApiKeyId, 'geography.read')))
            ORDER BY name;
            """, new
        {
            areaId, caller.UserId, caller.ApiKeyId, Unscoped = Geo(caller, "geography.read"),
        }, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<Guid> UpsertSiteAsync(Site site, CallerContext caller, UnitOfWork work, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(site);
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require("geography.manage");

        var c = work.Connection;

        if (!Geo(caller, "geography.manage"))
        {
            await RequireAreaAsync(c, caller, site.GeographicAreaId, ct).ConfigureAwait(false);
        }

        return await c.ExecuteScalarAsync<Guid>(new CommandDefinition("""
            INSERT INTO federation.sites
                (id, code, name, geographic_area_id, site_type, address, latitude, longitude, status)
            VALUES (COALESCE(NULLIF(@Id,'00000000-0000-0000-0000-000000000000'::uuid), gen_random_uuid()),
                    @Code, @Name, @GeographicAreaId, @SiteType, @Address, @Latitude, @Longitude, @Status)
            ON CONFLICT (id) DO UPDATE
            SET code = EXCLUDED.code, name = EXCLUDED.name,
                geographic_area_id = EXCLUDED.geographic_area_id,
                site_type = EXCLUDED.site_type, address = EXCLUDED.address,
                latitude = EXCLUDED.latitude, longitude = EXCLUDED.longitude,
                status = EXCLUDED.status, updated_at = now()
            RETURNING id;
            """, site, work.Transaction, cancellationToken: ct));
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
    private static async Task RequireAreaAsync(
        NpgsqlConnection c, CallerContext caller, Guid? areaId, CancellationToken ct)
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
            """, new { caller.UserId, caller.ApiKeyId, areaId }, cancellationToken: ct));

        if (!reachable)
        {
            throw new ForbiddenException("geography.manage");
        }
    }

}
