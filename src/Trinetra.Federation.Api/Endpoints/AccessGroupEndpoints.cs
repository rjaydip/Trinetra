using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using Trinetra.Federation.Api.Auth;
using Trinetra.Federation.Api.Contracts;
using Trinetra.Federation.Storage;
using Trinetra.Federation.Storage.Repositories;

namespace Trinetra.Federation.Api.Endpoints;

/// <summary>
/// Access groups, roles, permissions and scopes.
/// </summary>
/// <remarks>
/// A group is a reusable pairing of one role with one or more scopes — the mechanism that avoids
/// the role explosion in <c>RBAC-LOGICAL-FLOW.md</c> §15. One <c>CAMERA_OPERATOR</c> role is
/// reused across many groups rather than creating AhmedabadPoliceCameraOperator,
/// SuratPoliceCameraOperator and so on indefinitely.
/// </remarks>
public static class AccessGroupEndpoints
{
    private static AccessGroupResponse ToResponse(AccessGroupDetail g) => new(
        g.Id, g.Code, g.Name, g.Description, g.Status, g.RoleCode, g.Permissions,
        [.. g.Scopes.Select(s => new ScopeResponse(
            s.Id, s.ScopeType, s.OrganizationUnitId, s.GeographicAreaId,
            s.ResourceType, s.ResourceId, s.Description))],
        g.MemberCount);

    public static void MapAccessGroupEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/access-groups")
                       .WithTags("Access control")
                       .RequireAuthorization();

        group.MapGet("/", async Task<Ok<IReadOnlyList<AccessGroupResponse>>> (
            AccessGroupRepository repo, HttpContext http, CancellationToken ct) =>
        {
            CallerContextFactory.From(http).Require("group.read");
            var groups = await repo.ListAsync(ct);
            return TypedResults.Ok<IReadOnlyList<AccessGroupResponse>>([.. groups.Select(ToResponse)]);
        }).RequirePermission("group.read");

        group.MapGet("/{id:guid}", async Task<Results<Ok<AccessGroupResponse>, NotFound>> (
            Guid id, AccessGroupRepository repo, HttpContext http, CancellationToken ct) =>
        {
            CallerContextFactory.From(http).Require("group.read");
            var found = await repo.GetAsync(id, ct);
            return found is null ? TypedResults.NotFound() : TypedResults.Ok(ToResponse(found));
        }).RequirePermission("group.read");

        group.MapGet("/{id:guid}/members", async Task<Ok<IReadOnlyList<GroupMemberResponse>>> (
            Guid id, AccessGroupRepository repo, HttpContext http, CancellationToken ct) =>
        {
            CallerContextFactory.From(http).Require("group.read");
            var members = await repo.ListMembersAsync(id, ct);
            return TypedResults.Ok<IReadOnlyList<GroupMemberResponse>>(
                [.. members.Select(m => new GroupMemberResponse(m.UserId, m.Username, m.ExpiresAt))]);
        }).RequirePermission("group.read");

        group.MapPost("/", async Task<Results<Created<CreatedResponse>, ProblemHttpResult>> (
            [FromBody] CreateGroupRequest request, AccessGroupRepository repo,
            NpgsqlDataSource db, HttpContext http, CancellationToken ct) =>
        {
            var caller = CallerContextFactory.From(http);
            caller.Require("group.manage");

            // The same escalation guard as group assignment, applied one step earlier. Without
            // it a caller could build a SUPER_ADMIN group and then hand it to themselves through
            // a route that only checks the group's contents at assignment time.
            if (!caller.IsUnscopedFor("group.manage"))
            {
                var granted = await repo.GetRolePermissionsAsync(request.RoleId, ct);
                var exceeding = granted.Where(p => !caller.Has(p)).OrderBy(p => p).ToList();

                if (exceeding.Count > 0)
                {
                    return TypedResults.Problem(
                        title: "Would grant more than you hold",
                        detail: "That role grants permissions you do not have: "
                              + $"{string.Join(", ", exceeding)}.",
                        statusCode: StatusCodes.Status403Forbidden);
                }
            }

            await using var work = await UnitOfWork.BeginAsync(db, ct);

            var id = await repo.UpsertAsync(new AccessGroup
            {
                Code = request.Code,
                Name = request.Name,
                Description = request.Description,
                RoleId = request.RoleId,
                // DRAFT by default: a group is assembled and reviewed before it grants anything.
                Status = request.Status ?? "DRAFT",
            }, caller.UserId, work, ct);

            await work.AuditAsync(caller, "create", "access_group", id.ToString(),
                before: null, after: request, organizationUnitId: null, ct);
            await work.CommitAsync(ct);

            return TypedResults.Created($"/api/v1/access-groups/{id}", new CreatedResponse(id));
        }).RequirePermission("group.manage");

        // ---- Scopes --------------------------------------------------------

        group.MapPost("/{id:guid}/scopes", async Task<Results<Created<CreatedResponse>, NotFound, ProblemHttpResult>> (
            Guid id, [FromBody] AddScopeRequest request, AccessGroupRepository repo,
            NpgsqlDataSource db, HttpContext http, CancellationToken ct) =>
        {
            var caller = CallerContextFactory.From(http);
            caller.Require("group.manage");

            if (await repo.GetAsync(id, ct) is null)
            {
                return TypedResults.NotFound();
            }

            var scopeType = request.ScopeType?.ToUpperInvariant();

            if (scopeType is not ("ORGANIZATION" or "GEOGRAPHY" or "RESOURCE"))
            {
                return TypedResults.Problem(
                    title: "Unknown scope type",
                    detail: "Valid types: ORGANIZATION, GEOGRAPHY, RESOURCE.",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            // A scoped caller cannot widen a group beyond their own reach: granting access to an
            // organization unit they cannot themselves see would be escalation by another route.
            if (!caller.IsUnscopedFor("group.manage") && scopeType == "ORGANIZATION"
                && request.OrganizationUnitId is { } unitId)
            {
                var reachable = await repo.CanScopeToUnitAsync(unitId, caller, ct);

                if (!reachable)
                {
                    return TypedResults.Problem(
                        title: "Outside your scope",
                        detail: "You cannot scope a group to an organization unit you do not "
                              + "administer yourself.",
                        statusCode: StatusCodes.Status403Forbidden);
                }
            }

            await using var work = await UnitOfWork.BeginAsync(db, ct);

            var scopeId = await repo.AddScopeAsync(
                id, scopeType, request.OrganizationUnitId, request.GeographicAreaId,
                request.ResourceType, request.ResourceId, request.Description, work, ct);

            await work.AuditAsync(caller, "update", "group_scope", id.ToString(),
                before: null, after: request, organizationUnitId: null, ct);
            await work.CommitAsync(ct);

            return TypedResults.Created($"/api/v1/access-groups/{id}/scopes/{scopeId}", new CreatedResponse(scopeId));
        }).RequirePermission("group.manage");

        group.MapDelete("/{id:guid}/scopes/{scopeId:guid}", async Task<Results<NoContent, NotFound, ProblemHttpResult>> (
            Guid id, Guid scopeId, AccessGroupRepository repo, NpgsqlDataSource db,
            HttpContext http, CancellationToken ct) =>
        {
            var caller = CallerContextFactory.From(http);
            caller.Require("group.manage");

            // Removing a scope WIDENS a group: a dimension nobody constrains is unrestricted.
            // Dropping the last organization scope turns a departmental group into an
            // estate-wide one, which is the opposite of what "remove" suggests.
            var before = await repo.GetAsync(id, ct);
            if (before is null)
            {
                return TypedResults.NotFound();
            }

            var remaining = before.Scopes
                .Count(s => s.Id != scopeId && s.ScopeType == "ORGANIZATION");

            var removingLastOrgScope = before.Scopes
                .Any(s => s.Id == scopeId && s.ScopeType == "ORGANIZATION") && remaining == 0;

            if (removingLastOrgScope && !caller.IsUnscopedFor("group.manage"))
            {
                return TypedResults.Problem(
                    title: "Would widen the group to every organization",
                    detail: "Removing the last organization scope makes this group unrestricted "
                          + "by organization. Only an unscoped administrator can do that.",
                    statusCode: StatusCodes.Status403Forbidden);
            }

            await using var work = await UnitOfWork.BeginAsync(db, ct);

            if (!await repo.RemoveScopeAsync(id, scopeId, work, ct))
            {
                return TypedResults.NotFound();
            }

            await work.AuditAsync(caller, "delete", "group_scope", id.ToString(),
                before: new { scopeId }, after: null, organizationUnitId: null, ct);
            await work.CommitAsync(ct);

            return TypedResults.NoContent();
        }).RequirePermission("group.manage");

        // ---- Reference data -------------------------------------------------

        app.MapGet("/api/v1/roles", async Task<Ok<IReadOnlyList<RoleResponse>>> (
            AccessGroupRepository repo, HttpContext http, CancellationToken ct) =>
        {
            CallerContextFactory.From(http).Require("group.read");
            var roles = await repo.ListRolesAsync(ct);
            return TypedResults.Ok<IReadOnlyList<RoleResponse>>(
                [.. roles.Select(r => new RoleResponse(r.Id, r.Code, r.Name, r.Description, r.IsSystem))]);
        }).RequireAuthorization().WithTags("Access control").RequirePermission("group.read");

        app.MapGet("/api/v1/permissions", async Task<Ok<IReadOnlyList<PermissionResponse>>> (
            AccessGroupRepository repo, HttpContext http, CancellationToken ct) =>
        {
            CallerContextFactory.From(http).Require("group.read");
            var permissions = await repo.ListPermissionsAsync(ct);
            return TypedResults.Ok<IReadOnlyList<PermissionResponse>>(
                [.. permissions.Select(x => new PermissionResponse(x.Code, x.Name, x.Category, x.Description))]);
        }).RequireAuthorization().WithTags("Access control").RequirePermission("group.read");
    }
}
