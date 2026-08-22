using Npgsql;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Trinetra.Federation.Api.Auth;
using Trinetra.Federation.Api.Contracts;
using Trinetra.Federation.Storage;
using Trinetra.Federation.Storage.Repositories;

namespace Trinetra.Federation.Api.Endpoints;

/// <summary>Organization and geography — the two independent dimensions everything is scoped by.</summary>
public static class HierarchyEndpoints
{
    /// <summary>Offered back to the caller when a deactivation is refused.</summary>
    private static readonly string[] Resolutions = ["cascade", "reparent"];

    public static void MapHierarchyEndpoints(this IEndpointRouteBuilder app)
    {
        MapOrganizations(app);
        MapGeography(app);
    }

    private static void MapOrganizations(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/organizations")
                       .WithTags("Organizations")
                       .RequireAuthorization();

        group.MapGet("/", async Task<Ok<IReadOnlyList<OrganizationResponse>>> (
            OrganizationRepository repo, HttpContext http, CancellationToken ct) =>
        {
            var caller = CallerContextFactory.From(http);
            var orgs = await repo.ListAsync(caller, ct);
            return TypedResults.Ok<IReadOnlyList<OrganizationResponse>>([.. orgs.Select(ToResponse)]);
        }).RequirePermission("geography.read");

        group.MapGet("/{id:guid}", async Task<Results<Ok<OrganizationResponse>, NotFound>> (
            Guid id, OrganizationRepository repo, HttpContext http, CancellationToken ct) =>
        {
            var caller = CallerContextFactory.From(http);
            var org = await repo.GetAsync(id, caller, ct);
            return org is null ? TypedResults.NotFound() : TypedResults.Ok(ToResponse(org));
        }).RequirePermission("geography.read");

        group.MapPost("/", async Task<Created<CreatedResponse>> (
            [FromBody] OrganizationRequest request, OrganizationRepository repo,
            NpgsqlDataSource db, HttpContext http, CancellationToken ct) =>
        {
            var caller = CallerContextFactory.From(http);
            caller.Require("organization.manage");

            await using var work = await UnitOfWork.BeginAsync(db, ct);

            var id = await repo.UpsertAsync(new Organization
            {
                Code = request.Code,
                Name = request.Name,
                OrganizationType = request.OrganizationType,
                Description = request.Description,
                Status = request.Status ?? "ACTIVE",
            }, caller, work, ct);

            await work.AuditAsync(caller, "create", "organization", id.ToString(),
                before: null, after: request, organizationUnitId: null, ct);
            await work.CommitAsync(ct);

            return TypedResults.Created($"/api/v1/organizations/{id}", new CreatedResponse(id));
        }).RequirePermission("organization.manage");

        group.MapGet("/{id:guid}/units", async Task<Ok<IReadOnlyList<OrganizationUnitResponse>>> (
            Guid id, OrganizationRepository repo, HttpContext http, CancellationToken ct) =>
        {
            var caller = CallerContextFactory.From(http);
            var units = await repo.ListUnitsAsync(id, caller, ct);
            return TypedResults.Ok<IReadOnlyList<OrganizationUnitResponse>>([.. units.Select(ToResponse)]);
        }).RequirePermission("organization.read");

        group.MapPost("/{id:guid}/units", async Task<Created<CreatedResponse>> (
            Guid id, [FromBody] OrganizationUnitRequest request, OrganizationRepository repo,
            NpgsqlDataSource db, HttpContext http, CancellationToken ct) =>
        {
            var caller = CallerContextFactory.From(http);
            caller.Require("organization.manage");

            await using var work = await UnitOfWork.BeginAsync(db, ct);

            var unitId = await repo.UpsertUnitAsync(new OrganizationUnit
            {
                OrganizationId = id,
                ParentUnitId = request.ParentUnitId,
                Code = request.Code,
                Name = request.Name,
                UnitType = request.UnitType,
                Status = request.Status ?? "ACTIVE",
            }, caller, work, ct);

            await work.AuditAsync(caller, "create", "organization_unit", unitId.ToString(),
                before: null, after: request, organizationUnitId: unitId, ct);
            await work.CommitAsync(ct);

            return TypedResults.Created($"/api/v1/organization-units/{unitId}", new CreatedResponse(unitId));
        }).RequirePermission("organization.manage");

        app.MapPost("/api/v1/organization-units/{id:guid}/deactivate", async (
            Guid id, [FromBody] DeactivateRequest request, OrganizationRepository repo,
            NpgsqlDataSource db, HttpContext http, CancellationToken ct) =>
        {
            var caller = CallerContextFactory.From(http);
            caller.Require("organization.manage");

            await using var work = await UnitOfWork.BeginAsync(db, ct);

            return await DeactivateAsync(
                strategy => repo.DeactivateUnitAsync(id, strategy, request.NewParentId, caller, work, ct),
                request, caller, work, "organization_unit", id, ct);
        }).RequireAuthorization().WithTags("Organizations").RequirePermission("organization.manage");
    }

    private static void MapGeography(IEndpointRouteBuilder app)
    {
        var areas = app.MapGroup("/api/v1/geographic-areas")
                       .WithTags("Geography")
                       .RequireAuthorization();

        areas.MapGet("/", async Task<Ok<IReadOnlyList<GeographicAreaResponse>>> (
            Guid? parentId, bool? rootsOnly, GeographyRepository repo, HttpContext http,
            CancellationToken ct) =>
        {
            var caller = CallerContextFactory.From(http);
            var areas = await repo.ListAreasAsync(parentId, rootsOnly ?? false, caller, ct);
            return TypedResults.Ok<IReadOnlyList<GeographicAreaResponse>>([.. areas.Select(ToResponse)]);
        }).RequirePermission("geography.read");

        areas.MapGet("/{id:guid}", async Task<Results<Ok<GeographicAreaResponse>, NotFound>> (
            Guid id, GeographyRepository repo, HttpContext http, CancellationToken ct) =>
        {
            var caller = CallerContextFactory.From(http);
            var area = await repo.GetAreaAsync(id, caller, ct);
            return area is null ? TypedResults.NotFound() : TypedResults.Ok(ToResponse(area));
        }).RequirePermission("geography.read");

        // Both directions of the tree, because a UI needs to render downward and a scope check
        // needs to reason upward.
        areas.MapGet("/{id:guid}/children", async Task<Ok<IReadOnlyList<GeographicAreaResponse>>> (
            Guid id, GeographyRepository repo, HttpContext http, CancellationToken ct) =>
        {
            var caller = CallerContextFactory.From(http);
            var children = await repo.ListAreasAsync(id, false, caller, ct);
            return TypedResults.Ok<IReadOnlyList<GeographicAreaResponse>>([.. children.Select(ToResponse)]);
        }).RequirePermission("geography.read");

        areas.MapGet("/{id:guid}/ancestors", async Task<Ok<IReadOnlyList<GeographicAreaResponse>>> (
            Guid id, GeographyRepository repo, HttpContext http, CancellationToken ct) =>
        {
            var caller = CallerContextFactory.From(http);
            var chain = await repo.GetAncestorsAsync(id, caller, ct);
            return TypedResults.Ok<IReadOnlyList<GeographicAreaResponse>>([.. chain.Select(ToResponse)]);
        }).RequirePermission("geography.read");

        areas.MapGet("/types", async Task<Ok<IReadOnlyList<AreaTypeResponse>>> (
            GeographyRepository repo, HttpContext http, CancellationToken ct) =>
        {
            CallerContextFactory.From(http).Require("geography.read");
            var types = await repo.ListAreaTypesAsync(ct);
            return TypedResults.Ok<IReadOnlyList<AreaTypeResponse>>(
                [.. types.Select(t => new AreaTypeResponse(t.Code, t.Name, t.LevelOrder))]);
        }).RequirePermission("geography.read");

        areas.MapPost("/", async Task<Created<CreatedResponse>> (
            [FromBody] GeographicAreaRequest request, GeographyRepository repo,
            NpgsqlDataSource db, HttpContext http, CancellationToken ct) =>
        {
            var caller = CallerContextFactory.From(http);
            caller.Require("geography.manage");

            await using var work = await UnitOfWork.BeginAsync(db, ct);

            var id = await repo.UpsertAreaAsync(new GeographicArea
            {
                ParentAreaId = request.ParentAreaId,
                Code = request.Code,
                Name = request.Name,
                AreaType = request.AreaType,
                Status = request.Status ?? "ACTIVE",
            }, caller, work, ct);

            await work.AuditAsync(caller, "create", "geographic_area", id.ToString(),
                before: null, after: request, organizationUnitId: null, ct);
            await work.CommitAsync(ct);

            return TypedResults.Created($"/api/v1/geographic-areas/{id}", new CreatedResponse(id));
        }).RequirePermission("geography.manage");

        areas.MapPost("/{id:guid}/deactivate", async (
            Guid id, [FromBody] DeactivateRequest request, GeographyRepository repo,
            NpgsqlDataSource db, HttpContext http, CancellationToken ct) =>
        {
            var caller = CallerContextFactory.From(http);
            caller.Require("geography.manage");

            await using var work = await UnitOfWork.BeginAsync(db, ct);

            return await DeactivateAsync(
                strategy => repo.DeactivateAreaAsync(id, strategy, request.NewParentId, caller, work, ct),
                request, caller, work, "geographic_area", id, ct);
        }).RequirePermission("geography.manage");

        var sites = app.MapGroup("/api/v1/sites").WithTags("Geography").RequireAuthorization();

        sites.MapGet("/", async Task<Ok<IReadOnlyList<SiteResponse>>> (
            Guid? areaId, GeographyRepository repo, HttpContext http, CancellationToken ct) =>
        {
            var caller = CallerContextFactory.From(http);
            var found = await repo.ListSitesAsync(areaId, caller, ct);
            return TypedResults.Ok<IReadOnlyList<SiteResponse>>([.. found.Select(ToResponse)]);
        }).RequirePermission("geography.read");

        sites.MapPost("/", async Task<Created<CreatedResponse>> (
            [FromBody] SiteRequest request, GeographyRepository repo,
            NpgsqlDataSource db, HttpContext http, CancellationToken ct) =>
        {
            var caller = CallerContextFactory.From(http);
            caller.Require("geography.manage");

            await using var work = await UnitOfWork.BeginAsync(db, ct);

            var id = await repo.UpsertSiteAsync(new Site
            {
                Code = request.Code,
                Name = request.Name,
                GeographicAreaId = request.GeographicAreaId,
                SiteType = request.SiteType,
                Address = request.Address,
                Latitude = request.Latitude,
                Longitude = request.Longitude,
                Status = request.Status ?? "ACTIVE",
            }, caller, work, ct);

            await work.AuditAsync(caller, "create", "site", id.ToString(),
                before: null, after: request, organizationUnitId: null, ct);
            await work.CommitAsync(ct);

            return TypedResults.Created($"/api/v1/sites/{id}", new CreatedResponse(id));
        }).RequirePermission("geography.manage");
    }

    // Explicit projections rather than serialising the storage records directly: a column added
    // for internal use would otherwise appear in the public contract without anyone deciding it
    // should.
    private static OrganizationResponse ToResponse(Organization o) =>
        new(o.Id, o.Code, o.Name, o.OrganizationType, o.Description, o.Status);

    private static OrganizationUnitResponse ToResponse(OrganizationUnit u) =>
        new(u.Id, u.OrganizationId, u.ParentUnitId, u.Code, u.Name, u.UnitType, u.Status);

    private static GeographicAreaResponse ToResponse(GeographicArea a) =>
        new(a.Id, a.ParentAreaId, a.Code, a.Name, a.AreaType, a.Status);

    private static SiteResponse ToResponse(Site s) =>
        new(s.Id, s.Code, s.Name, s.GeographicAreaId, s.SiteType, s.Address,
            s.Latitude, s.Longitude, s.Status);

    /// <summary>
    /// Shared deactivation flow for both hierarchies.
    /// </summary>
    /// <remarks>
    /// Without an explicit strategy the request is refused with 409 and a list of what would be
    /// affected. The operator then re-sends saying <c>cascade</c> or <c>reparent</c>. Neither
    /// outcome is ever applied by default — see GEOGRAPHY-SCHEMA.md for why both silent
    /// behaviours are harmful.
    /// </remarks>
    private static async Task<Results<NoContent, ProblemHttpResult>> DeactivateAsync(
        Func<ChildStrategy, Task<DeactivationConflict?>> deactivate,
        DeactivateRequest request,
        CallerContext caller,
        // The caller opens the unit and passes it in: the deactivation and its audit row must
        // commit together, and the helper is where the audit row is written.
        UnitOfWork work,
        string entityType,
        Guid id,
        CancellationToken ct)
    {
        var strategy = request.ChildStrategy?.ToUpperInvariant() switch
        {
            "CASCADE" => ChildStrategy.Cascade,
            "REPARENT" => ChildStrategy.Reparent,
            _ => ChildStrategy.Refuse,
        };

        try
        {
            var conflict = await deactivate(strategy);

            if (conflict is not null)
            {
                return TypedResults.Problem(
                    title: "Active children must be resolved first",
                    detail: "Re-send with childStrategy 'cascade' to deactivate everything "
                          + "beneath this node, or 'reparent' with newParentId to move its "
                          + "children elsewhere first.",
                    statusCode: StatusCodes.Status409Conflict,
                    extensions: new Dictionary<string, object?>
                    {
                        ["affectedAreas"] = conflict.AffectedAreas,
                        ["affectedSites"] = conflict.AffectedSites,
                        ["resolutions"] = Resolutions,
                    });
            }
        }
        catch (InvalidOperationException ex)
        {
            // Raised when a reparent target is invalid — inside the branch being deactivated, or
            // absent altogether.
            return TypedResults.Problem(
                title: "Invalid reparent target", detail: ex.Message,
                statusCode: StatusCodes.Status400BadRequest);
        }

        await work.AuditAsync(caller, "update", entityType, id.ToString(),
            before: new { status = "ACTIVE" },
            after: new { status = "INACTIVE", childStrategy = strategy.ToString() },
            organizationUnitId: null, ct);
        await work.CommitAsync(ct);

        return TypedResults.NoContent();
    }
}
