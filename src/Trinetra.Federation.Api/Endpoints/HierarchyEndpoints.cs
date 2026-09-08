using Npgsql;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Trinetra.Federation.Api.Auth;
using Trinetra.Federation.Api.Contracts;
using Trinetra.Federation.Api.OpenApi;
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
                       .WithTags(ApiTags.Organizations)
                       .RequireAuthorization();

        group.MapGet("/", ListOrganizationsAsync)
          .RequirePermission("organization.read")
          .WithSummary("List organizations")
          .WithDescription(
              "The top of the organizational dimension — one row per force, agency or operator "
              + "whose units own VMS targets. Start here when building a scope picker, then walk "
              + "down with `GET /organizations/{id}/units`.");

        group.MapGet("/{id:guid}", GetOrganizationAsync)
          .RequirePermission("organization.read")
          .WithSummary("Read one organization")
          .WithDescription(
              "Code, name, type, description and status. An organization the caller has no grant "
              + "over returns 404, matching the rest of the API.");

        group.MapPost("/", CreateOrganizationAsync)
          .RequirePermission("organization.manage")
          .WithSummary("Create an organization")
          .WithDescription(
              "Creates the root of an organizational tree. `code` is the stable identifier "
              + "operators and integrations refer to; `name` is display text and can change. "
              + "Status defaults to `ACTIVE`.\n\n"
              + "An organization on its own owns nothing — add units beneath it before a VMS "
              + "target can be assigned anywhere.");

        group.MapPut("/{id:guid}", UpdateOrganizationAsync)
          .RequirePermission("organization.manage")
          .WithSummary("Update an organization")
          .WithDescription(
              "Replaces `code`, `name`, `organizationType` and `description`; `status` is left "
              + "as-is when omitted (deactivate through the lifecycle route, not by clearing a "
              + "field). Unscoped `organization.manage` only, the same as create — editing an "
              + "organization affects every department inside it. 404 if the id is unknown.");

        group.MapGet("/{id:guid}/units", ListUnitsAsync)
          .RequirePermission("organization.read")
          .WithSummary("List an organization's units")
          .WithDescription(
              "The unit tree beneath one organization, each row carrying its `parentUnitId` so a "
              + "client can assemble the hierarchy. These ids are what `organizationUnitId` on a "
              + "VMS target and on an access-group scope grant refer to.");

        group.MapPost("/{id:guid}/units", CreateUnitAsync)
          .RequirePermission("organization.manage")
          .WithSummary("Create a unit inside an organization")
          .WithDescription(
              "Adds a department, division, zone or station to the tree. Omit `parentUnitId` for "
              + "a top-level unit, or set it to nest one.\n\n"
              + "Placement is a security decision, not just taxonomy: a caller granted a unit is "
              + "granted everything beneath it, so a unit created in the wrong place widens who "
              + "can see the targets assigned to it.");

        app.MapGet("/api/v1/organization-units/{id:guid}", GetUnitAsync)
          .RequireAuthorization().WithTags(ApiTags.Organizations)
          .RequirePermission("organization.read")
          .WithSummary("Read one organization unit")
          .WithDescription(
              "Code, name, type, parent and status for a single unit. A unit the caller has no "
              + "grant over returns 404.");

        app.MapPut("/api/v1/organization-units/{id:guid}", UpdateUnitAsync)
          .RequireAuthorization().WithTags(ApiTags.Organizations)
          .RequirePermission("organization.manage")
          .WithSummary("Update an organization unit")
          .WithDescription(
              "Edits `code`, `name` and `unitType`; `status` is left as-is when omitted. "
              + "`organizationId` in the body is ignored (a unit cannot move between "
              + "organizations) and `parentUnitId` must match the current parent — re-parenting "
              + "has its own locked flow (`/deactivate` with `childStrategy: \"reparent\"`) and a "
              + "PUT that changes it is refused with 400. 404 if the id is unknown or out of the "
              + "caller's reach.");

        app.MapPost("/api/v1/organization-units/{id:guid}/deactivate", DeactivateUnitAsync)
          .RequireAuthorization().WithTags(ApiTags.Organizations)
          .RequirePermission("organization.manage")
          .WithSummary("Deactivate an organization unit")
          .WithDescription(
              "Marks a unit inactive. Units are never hard-deleted — audit rows and VMS targets "
              + "reference them, and a deleted unit would orphan both.\n\n"
              + "**A unit with active children is refused with 409**, listing what would be "
              + "affected. Re-send with `childStrategy: \"cascade\"` to deactivate the whole "
              + "branch, or `\"reparent\"` plus `newParentId` to move the children somewhere "
              + "else first. Neither is ever applied by default: cascading silently takes a "
              + "department's whole estate out of scope, and silently reparenting hides a "
              + "structural change no one approved.");
    }

    private static void MapGeography(IEndpointRouteBuilder app)
    {
        var areas = app.MapGroup("/api/v1/geographic-areas")
                       .WithTags(ApiTags.Geography)
                       .RequireAuthorization();

        areas.MapGet("/", ListAreasAsync)
          .RequirePermission("geography.read")
          .WithSummary("List geographic areas")
          .WithDescription(
              "Areas within the caller's geographic scope. `rootsOnly=true` returns the top of "
              + "the tree; `parentId` returns one level beneath a node. Send neither and the "
              + "whole in-scope set comes back — fine for a small deployment, worth paging by "
              + "level in a large one.");

        areas.MapGet("/{id:guid}", GetAreaAsync)
          .RequirePermission("geography.read")
          .WithSummary("Read one geographic area")
          .WithDescription(
              "Code, name, area type, parent and status for a single node in the geographic "
              + "hierarchy.");

        // Both directions of the tree, because a UI needs to render downward and a scope check
        // needs to reason upward.
        areas.MapGet("/{id:guid}/children", ListAreaChildrenAsync)
          .RequirePermission("geography.read")
          .WithSummary("List an area's immediate children")
          .WithDescription(
              "One level down, for rendering a tree as the user expands it rather than fetching "
              + "the whole hierarchy up front.");

        areas.MapGet("/{id:guid}/ancestors", ListAreaAncestorsAsync)
          .RequirePermission("geography.read")
          .WithSummary("Walk an area's ancestor chain to the root")
          .WithDescription(
              "The other direction of the tree: a UI renders downward, but explaining *why* a "
              + "caller can see something means reasoning upward — a grant on a district covers "
              + "every ward inside it. Use this to build a breadcrumb, or to show which ancestor "
              + "a permission was actually granted on.");

        areas.MapGet("/types", ListAreaTypesAsync)
          .RequirePermission("geography.read")
          .WithSummary("List the area types and their nesting order")
          .WithDescription(
              "Reference data: the levels this deployment models — state, district, zone, ward "
              + "and so on — each with a `levelOrder` giving where it sits in the hierarchy. "
              + "Clients should populate the `areaType` picker from here rather than hard-coding "
              + "a list, since the levels are deployment configuration.");

        areas.MapPost("/", CreateAreaAsync)
          .RequirePermission("geography.manage")
          .WithSummary("Create a geographic area")
          .WithDescription(
              "Adds a node to the geographic hierarchy. `areaType` must be one of the codes from "
              + "`GET /geographic-areas/types`; omit `parentAreaId` for a root. The area's level "
              + "must be strictly finer than its parent's (`levelOrder`) — a District under a "
              + "Village is refused with 400.\n\n"
              + "Like an organization unit, where an area sits decides who can see what is inside "
              + "it — a grant on a parent reaches every descendant.");

        areas.MapPut("/{id:guid}", UpdateAreaAsync)
          .RequirePermission("geography.manage")
          .WithSummary("Update a geographic area")
          .WithDescription(
              "Edits `code`, `name`, `areaType` and `description`; `status` is left as-is when "
              + "omitted. `areaType` must be a code from `GET /geographic-areas/types`, and "
              + "changing it to a level equal to or coarser than this area's parent — or coarser "
              + "than one of its own children — is refused with 400. `parentAreaId` must match "
              + "the current parent — re-parenting has its own locked flow (`/deactivate` with "
              + "`childStrategy: \"reparent\"`) and a PUT that changes it is refused with 400. "
              + "404 if the id is unknown or out of the caller's reach.");

        areas.MapPost("/{id:guid}/deactivate", DeactivateAreaAsync)
          .RequirePermission("geography.manage")
          .WithSummary("Deactivate a geographic area")
          .WithDescription(
              "Marks an area inactive, with the same two-step refusal as unit deactivation: an "
              + "area with active child areas beneath it returns 409 listing what would be "
              + "affected, and the caller re-sends with `childStrategy` `cascade` or `reparent` "
              + "plus `newParentId`.\n\n"
              + "A reparent target inside the branch being deactivated is a 400 — it would leave "
              + "the children under an inactive ancestor.");

    }

    // Explicit projections rather than serialising the storage records directly: a column added
    // for internal use would otherwise appear in the public contract without anyone deciding it
    // should.
    private static OrganizationResponse ToResponse(Organization o) =>
        new(o.Id, o.Code, o.Name, o.OrganizationType, o.Description, o.Status);

    private static OrganizationUnitResponse ToResponse(OrganizationUnit u) =>
        new(u.Id, u.OrganizationId, u.ParentUnitId, u.Code, u.Name, u.UnitType,
            u.Description, u.GeographicAreaId, u.Status);

    private static GeographicAreaResponse ToResponse(GeographicArea a) =>
        new(a.Id, a.ParentAreaId, a.Code, a.Name, a.AreaType, a.Description, a.Status);

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
                        ["affectedChildren"] = conflict.AffectedChildren,
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

    private static async Task<Ok<IReadOnlyList<OrganizationResponse>>> ListOrganizationsAsync(
        OrganizationRepository repo, HttpContext http, CancellationToken ct)
    {
        var caller = CallerContextFactory.From(http);
        var orgs = await repo.ListAsync(caller, ct);
        return TypedResults.Ok<IReadOnlyList<OrganizationResponse>>([.. orgs.Select(ToResponse)]);
    }

    private static async Task<Results<Ok<OrganizationResponse>, NotFound>> GetOrganizationAsync(
        Guid id, OrganizationRepository repo, HttpContext http, CancellationToken ct)
    {
        var caller = CallerContextFactory.From(http);
        var org = await repo.GetAsync(id, caller, ct);
        return org is null ? TypedResults.NotFound() : TypedResults.Ok(ToResponse(org));
    }

    private static async Task<Created<CreatedResponse>> CreateOrganizationAsync(
        [FromBody] OrganizationRequest request, OrganizationRepository repo,
        NpgsqlDataSource db, HttpContext http, CancellationToken ct)
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
    }

    private static async Task<Results<Ok<OrganizationResponse>, NotFound>> UpdateOrganizationAsync(
        Guid id, [FromBody] OrganizationRequest request, OrganizationRepository repo,
        NpgsqlDataSource db, HttpContext http, CancellationToken ct)
    {
        var caller = CallerContextFactory.From(http);
        caller.Require("organization.manage");

        if (await repo.GetAsync(id, caller, ct) is not { } prior)
        {
            return TypedResults.NotFound();
        }

        await using var work = await UnitOfWork.BeginAsync(db, ct);

        var updated = new Organization
        {
            Id = id,
            Code = request.Code,
            Name = request.Name,
            OrganizationType = request.OrganizationType,
            Description = request.Description,
            Status = request.Status ?? prior.Status,
        };
        await repo.UpsertAsync(updated, caller, work, ct);

        await work.AuditAsync(caller, "update", "organization", id.ToString(),
            before: ToResponse(prior), after: ToResponse(updated), organizationUnitId: null, ct);
        await work.CommitAsync(ct);

        return TypedResults.Ok(ToResponse(updated));
    }

    private static async Task<Results<Ok<OrganizationUnitResponse>, NotFound>> GetUnitAsync(
        Guid id, OrganizationRepository repo, HttpContext http, CancellationToken ct)
    {
        var caller = CallerContextFactory.From(http);
        var unit = await repo.GetUnitAsync(id, caller, ct);
        return unit is null ? TypedResults.NotFound() : TypedResults.Ok(ToResponse(unit));
    }

    private static async Task<Results<Ok<OrganizationUnitResponse>, NotFound, ProblemHttpResult>> UpdateUnitAsync(
        Guid id, [FromBody] OrganizationUnitRequest request, OrganizationRepository repo,
        NpgsqlDataSource db, HttpContext http, CancellationToken ct)
    {
        var caller = CallerContextFactory.From(http);
        caller.Require("organization.manage");

        if (await repo.GetUnitAsync(id, caller, ct) is not { } prior)
        {
            return TypedResults.NotFound();
        }

        // Re-parenting is structural, not a field edit: it moves a whole subtree and everyone's
        // view of it, and has its own locked flow. A PUT that tries to change the parent is
        // refused and pointed there.
        if (request.ParentUnitId != prior.ParentUnitId)
        {
            return TypedResults.Problem(
                title: "Cannot re-parent through this route",
                detail: "Changing parentUnitId moves the unit and everything beneath it. Use "
                      + "POST /api/v1/organization-units/{id}/deactivate with "
                      + "childStrategy 'reparent', or deactivate and recreate.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        await using var work = await UnitOfWork.BeginAsync(db, ct);

        var updated = new OrganizationUnit
        {
            Id = id,
            OrganizationId = prior.OrganizationId,   // a unit never moves between organizations
            ParentUnitId = prior.ParentUnitId,
            Code = request.Code,
            Name = request.Name,
            UnitType = request.UnitType,
            Description = request.Description,
            GeographicAreaId = request.GeographicAreaId,   // descriptive "home area" only (invariant 12)
            Status = request.Status ?? prior.Status,
        };
        // parentIsChanging: false — the 400 guard above proved the parent is unchanged, so a
        // rename must not be blocked by an INACTIVE current parent.
        await repo.UpsertUnitAsync(updated, caller, work, ct, parentIsChanging: false);

        await work.AuditAsync(caller, "update", "organization_unit", id.ToString(),
            before: ToResponse(prior), after: ToResponse(updated), organizationUnitId: id, ct);
        await work.CommitAsync(ct);

        return TypedResults.Ok(ToResponse(updated));
    }

    private static async Task<Ok<IReadOnlyList<OrganizationUnitResponse>>> ListUnitsAsync(
        Guid id, OrganizationRepository repo, HttpContext http, CancellationToken ct)
    {
        var caller = CallerContextFactory.From(http);
        var units = await repo.ListUnitsAsync(id, caller, ct);
        return TypedResults.Ok<IReadOnlyList<OrganizationUnitResponse>>([.. units.Select(ToResponse)]);
    }

    private static async Task<Created<CreatedResponse>> CreateUnitAsync(
        Guid id, [FromBody] OrganizationUnitRequest request, OrganizationRepository repo,
        NpgsqlDataSource db, HttpContext http, CancellationToken ct)
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
            Description = request.Description,
            GeographicAreaId = request.GeographicAreaId,   // descriptive "home area" only (invariant 12)
            Status = request.Status ?? "ACTIVE",
        }, caller, work, ct);

        await work.AuditAsync(caller, "create", "organization_unit", unitId.ToString(),
            before: null, after: request, organizationUnitId: unitId, ct);
        await work.CommitAsync(ct);

        return TypedResults.Created($"/api/v1/organization-units/{unitId}", new CreatedResponse(unitId));
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> DeactivateUnitAsync(
        Guid id, [FromBody] DeactivateRequest request, OrganizationRepository repo,
        NpgsqlDataSource db, HttpContext http, CancellationToken ct)
    {
        var caller = CallerContextFactory.From(http);
        caller.Require("organization.manage");

        await using var work = await UnitOfWork.BeginAsync(db, ct);

        return await DeactivateAsync(
            strategy => repo.DeactivateUnitAsync(id, strategy, request.NewParentId, caller, work, ct),
            request, caller, work, "organization_unit", id, ct);
    }

    private static async Task<Ok<IReadOnlyList<GeographicAreaResponse>>> ListAreasAsync(
        Guid? parentId, bool? rootsOnly, GeographyRepository repo, HttpContext http,
        CancellationToken ct)
    {
        var caller = CallerContextFactory.From(http);
        var areas = await repo.ListAreasAsync(parentId, rootsOnly ?? false, caller, ct);
        return TypedResults.Ok<IReadOnlyList<GeographicAreaResponse>>([.. areas.Select(ToResponse)]);
    }

    private static async Task<Results<Ok<GeographicAreaResponse>, NotFound>> GetAreaAsync(
        Guid id, GeographyRepository repo, HttpContext http, CancellationToken ct)
    {
        var caller = CallerContextFactory.From(http);
        var area = await repo.GetAreaAsync(id, caller, ct);
        return area is null ? TypedResults.NotFound() : TypedResults.Ok(ToResponse(area));
    }

    private static async Task<Ok<IReadOnlyList<GeographicAreaResponse>>> ListAreaChildrenAsync(
        Guid id, GeographyRepository repo, HttpContext http, CancellationToken ct)
    {
        var caller = CallerContextFactory.From(http);
        var children = await repo.ListAreasAsync(id, false, caller, ct);
        return TypedResults.Ok<IReadOnlyList<GeographicAreaResponse>>([.. children.Select(ToResponse)]);
    }

    private static async Task<Ok<IReadOnlyList<GeographicAreaResponse>>> ListAreaAncestorsAsync(
        Guid id, GeographyRepository repo, HttpContext http, CancellationToken ct)
    {
        var caller = CallerContextFactory.From(http);
        var chain = await repo.GetAncestorsAsync(id, caller, ct);
        return TypedResults.Ok<IReadOnlyList<GeographicAreaResponse>>([.. chain.Select(ToResponse)]);
    }

    private static async Task<Ok<IReadOnlyList<AreaTypeResponse>>> ListAreaTypesAsync(
        GeographyRepository repo, HttpContext http, CancellationToken ct)
    {
        CallerContextFactory.From(http).Require("geography.read");
        var types = await repo.ListAreaTypesAsync(ct);
        return TypedResults.Ok<IReadOnlyList<AreaTypeResponse>>(
            [.. types.Select(t => new AreaTypeResponse(t.Code, t.Name, t.LevelOrder))]);
    }

    private static async Task<Created<CreatedResponse>> CreateAreaAsync(
        [FromBody] GeographicAreaRequest request, GeographyRepository repo,
        NpgsqlDataSource db, HttpContext http, CancellationToken ct)
    {
        var caller = CallerContextFactory.From(http);
        caller.Require("geography.manage");

        await using var work = await UnitOfWork.BeginAsync(db, ct);

        // A bad area_type (not in the registry), a level-order containment violation from
        // trg_geo_area_acyclic, or a duplicate code under the same parent all surface as a
        // constraint / raised exception and are mapped to 400/409 by the global handler.
        var id = await repo.UpsertAreaAsync(new GeographicArea
        {
            ParentAreaId = request.ParentAreaId,
            Code = request.Code,
            Name = request.Name,
            AreaType = request.AreaType,
            Description = request.Description,
            Status = request.Status ?? "ACTIVE",
        }, caller, work, ct);

        await work.AuditAsync(caller, "create", "geographic_area", id.ToString(),
            before: null, after: request, organizationUnitId: null, ct);
        await work.CommitAsync(ct);

        return TypedResults.Created($"/api/v1/geographic-areas/{id}", new CreatedResponse(id));
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> DeactivateAreaAsync(
        Guid id, [FromBody] DeactivateRequest request, GeographyRepository repo,
        NpgsqlDataSource db, HttpContext http, CancellationToken ct)
    {
        var caller = CallerContextFactory.From(http);
        caller.Require("geography.manage");

        await using var work = await UnitOfWork.BeginAsync(db, ct);

        return await DeactivateAsync(
            strategy => repo.DeactivateAreaAsync(id, strategy, request.NewParentId, caller, work, ct),
            request, caller, work, "geographic_area", id, ct);
    }

    private static async Task<Results<Ok<GeographicAreaResponse>, NotFound, ProblemHttpResult>> UpdateAreaAsync(
        Guid id, [FromBody] GeographicAreaRequest request, GeographyRepository repo,
        NpgsqlDataSource db, HttpContext http, CancellationToken ct)
    {
        var caller = CallerContextFactory.From(http);
        caller.Require("geography.manage");

        if (await repo.GetAreaAsync(id, caller, ct) is not { } prior)
        {
            return TypedResults.NotFound();
        }

        // Re-parenting is structural — it moves a whole subtree and everyone's view of it — and
        // has its own locked flow. A PUT that tries to change the parent is refused and pointed
        // there.
        if (request.ParentAreaId != prior.ParentAreaId)
        {
            return TypedResults.Problem(
                title: "Cannot re-parent through this route",
                detail: "Changing parentAreaId moves the area and everything beneath it. Use "
                      + "POST /api/v1/geographic-areas/{id}/deactivate with "
                      + "childStrategy 'reparent', or deactivate and recreate.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        await using var work = await UnitOfWork.BeginAsync(db, ct);

        var updated = new GeographicArea
        {
            Id = id,
            ParentAreaId = prior.ParentAreaId,
            Code = request.Code,
            Name = request.Name,
            AreaType = request.AreaType,
            Description = request.Description,
            Status = request.Status ?? prior.Status,
        };
        // parentIsChanging: false — the 400 guard above proved the parent is unchanged, so a
        // rename must not be blocked by an INACTIVE current parent. An areaType change is still
        // level-order checked (against parent and children) by trg_geo_area_acyclic → 400.
        await repo.UpsertAreaAsync(updated, caller, work, ct, parentIsChanging: false);

        await work.AuditAsync(caller, "update", "geographic_area", id.ToString(),
            before: ToResponse(prior), after: ToResponse(updated), organizationUnitId: null, ct);
        await work.CommitAsync(ct);

        return TypedResults.Ok(ToResponse(updated));
    }
}
