using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using Trinetra.Federation.Api.Auth;
using Trinetra.Federation.Api.Contracts;
using Trinetra.Federation.Api.OpenApi;
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
                       .WithTags(ApiTags.AccessControl)
                       .RequireAuthorization();

        group.MapGet("/", ListAsync)
          .RequirePermission("group.read")
          .WithSummary("List access groups")
          .WithDescription(
              "The groups you may see, each with its role, the permissions that role carries, "
              + "its scopes and how many members it has. A group pairs one role with one or more "
              + "scopes, which is what keeps a single `CAMERA_OPERATOR` role reusable instead of "
              + "spawning AhmedabadPoliceCameraOperator, SuratPoliceCameraOperator and so on "
              + "without end.\n\n"
              + "A group is listed only if you could grant it — its scopes are within your reach "
              + "(the same rule as adding a user to it). A group with no organization or no "
              + "geography scope reaches every department or area, so it is visible only to an "
              + "administrator already unscoped on that dimension. The platform-admin group is "
              + "therefore invisible to every scoped administrator.");

        group.MapGet("/{id:guid}", GetAsync)
          .RequirePermission("group.read")
          .WithSummary("Read one access group with its scopes")
          .WithDescription(
              "The full grant: the role's permissions, and the scope rows saying where they "
              + "apply. **A dimension with no scope row is unrestricted on that dimension** — a "
              + "group with geography scopes but no organization scope grants its permissions "
              + "across every department. Read the scope list, not just the permission list, "
              + "before deciding what a group actually confers.\n\n"
              + "A group outside your reach returns `404`, identical to one that does not exist.");

        group.MapGet("/{id:guid}/members", MembersAsync)
          .RequirePermission("group.read")
          .WithSummary("List a group's members")
          .WithDescription(
              "Who currently holds this grant, with each membership's expiry. The reverse of "
              + "`GET /users/{id}/groups`, and the route for answering 'who can do this' during "
              + "a review. Membership is changed from the user side, not here.\n\n"
              + "A group you cannot see returns `404`. For a group you can see, the member list "
              + "is still limited to accounts within your own reach — a cross-department group "
              + "does not hand a single-department administrator its full roster.");

        group.MapPost("/", CreateAsync)
          .RequirePermission("group.manage")
          .WithSummary("Create an access group")
          .WithDescription(
              "Pairs a role with a scope set. **Created as `DRAFT`**, so a group is assembled and "
              + "reviewed before it grants anything; add its scopes, then activate it.\n\n"
              + "A scoped caller cannot choose a role granting permissions they do not hold "
              + "themselves — 403 naming the excess. The check is here as well as at assignment "
              + "time, or a caller could build the group first and hand it to themselves through "
              + "a route that only inspects it on the way out.");

        group.MapPut("/{id:guid}", UpdateAsync)
          .RequirePermission("group.manage")
          .WithSummary("Edit an access group")
          .WithDescription(
              "Changes `code`, `name`, `description` and `roleId`. `status` is not touched here — "
              + "use `/activate` and `/disable`.\n\n"
              + "Switching the role re-runs the escalation guard: a scoped caller cannot move a "
              + "group onto a role granting permissions they do not hold themselves. Editing the "
              + "role of an **active** group changes what every member can do immediately, and is "
              + "audited as such. 404 if the group is unknown or outside your reach.");

        group.MapPost("/{id:guid}/activate", ActivateAsync)
          .RequirePermission("group.manage")
          .WithSummary("Activate an access group")
          .WithDescription(
              "Moves a `DRAFT` or `DISABLED` group to `ACTIVE`, at which point its members hold "
              + "the grant.\n\n"
              + "**A group missing an ORGANIZATION scope or a GEOGRAPHY scope is unrestricted on "
              + "that dimension** — an estate-wide grant. Activating one is allowed only for a "
              + "caller already unscoped for `group.manage` on that dimension; anyone else gets "
              + "403 and must add the missing scope first.");

        group.MapPost("/{id:guid}/disable", DisableAsync)
          .RequirePermission("group.manage")
          .WithSummary("Disable an access group")
          .WithDescription(
              "Moves a group to `DISABLED`. The grant stops immediately for every member — the "
              + "authorization functions only consider `ACTIVE` groups. Membership rows are left "
              + "intact, so re-activating restores the same roster.");

        // ---- Scopes --------------------------------------------------------

        group.MapPost("/{id:guid}/scopes", AddScopeAsync)
          .RequirePermission("group.manage")
          .WithSummary("Add a scope to a group")
          .WithDescription(
              "Narrows where a group's permissions apply. Three types:\n"
              + "- `ORGANIZATION` — an organization unit and everything beneath it;\n"
              + "- `GEOGRAPHY` — a geographic area and its descendants;\n"
              + "- `RESOURCE` — one named resource.\n\n"
              + "Organization and geography are **independent dimensions and are ANDed**: a group "
              + "scoped to a department and to a district grants access only where the two "
              + "overlap. Adding a second scope on the same dimension widens within that "
              + "dimension.\n\n"
              + "A scoped caller cannot point a scope at an organization unit they do not "
              + "administer themselves — that would be escalation by another route.");

        group.MapDelete("/{id:guid}/scopes/{scopeId:guid}", RemoveScopeAsync)
          .RequirePermission("group.manage")
          .WithSummary("Remove a scope from a group")
          .WithDescription(
              "**Removing a scope widens the group**, which is the opposite of what 'remove' "
              + "suggests: a dimension nobody constrains is unrestricted, so dropping the last "
              + "`ORGANIZATION` scope turns a departmental group into an estate-wide one.\n\n"
              + "For that reason only a caller who is already unscoped for `group.manage` may "
              + "remove the last organization scope; anyone else gets 403. Removing one of "
              + "several is ordinary narrowing and is allowed.");

        // ---- Reference data -------------------------------------------------
        // Roles live at /api/v1/roles — see RoleEndpoints, which also owns their CRUD.

        app.MapGet("/api/v1/permissions", ListPermissionsAsync)
          .RequireAuthorization().WithTags(ApiTags.AccessControl).RequirePermission("group.read")
          .WithSummary("List every permission the platform defines")
          .WithDescription(
              "The catalogue behind the **Requires permission** line on each operation in this "
              + "document: every code, its category and what it allows. Read it to work out which "
              + "role a user needs, or to interpret `GET /users/{id}/permissions`.");
    }

    private static async Task<Ok<IReadOnlyList<AccessGroupResponse>>> ListAsync(
        AccessGroupRepository repo, HttpContext http, CancellationToken ct)
    {
        var caller = CallerContextFactory.From(http);
        caller.Require("group.read");
        var groups = await repo.ListAsync(caller, ct);
        return TypedResults.Ok<IReadOnlyList<AccessGroupResponse>>([.. groups.Select(ToResponse)]);
    }

    private static async Task<Results<Ok<AccessGroupResponse>, NotFound>> GetAsync(
        Guid id, AccessGroupRepository repo, HttpContext http, CancellationToken ct)
    {
        var caller = CallerContextFactory.From(http);
        caller.Require("group.read");

        // A group outside the caller's reach is reported as absent, not forbidden — same rule as
        // the list (it is simply omitted) and as out-of-scope user and camera reads.
        if (!await repo.IsVisibleToAsync(id, caller, "group.read", ct))
        {
            return TypedResults.NotFound();
        }

        var found = await repo.GetAsync(id, ct);
        return found is null ? TypedResults.NotFound() : TypedResults.Ok(ToResponse(found));
    }

    private static async Task<Results<Ok<IReadOnlyList<GroupMemberResponse>>, NotFound>> MembersAsync(
        Guid id, AccessGroupRepository repo, HttpContext http, CancellationToken ct)
    {
        var caller = CallerContextFactory.From(http);
        caller.Require("group.read");

        if (!await repo.IsVisibleToAsync(id, caller, "group.read", ct))
        {
            return TypedResults.NotFound();
        }

        // The group is visible; the member list is still filtered to accounts the caller could
        // see in the user directory, so a cross-department group does not leak its full roster.
        var members = await repo.ListMembersAsync(id, caller, ct);
        return TypedResults.Ok<IReadOnlyList<GroupMemberResponse>>(
            [.. members.Select(m => new GroupMemberResponse(m.UserId, m.Username, m.ExpiresAt))]);
    }

    private static async Task<Results<Created<CreatedResponse>, ProblemHttpResult>> CreateAsync(
        [FromBody] CreateGroupRequest request, AccessGroupRepository repo,
        NpgsqlDataSource db, HttpContext http, CancellationToken ct)
    {
        var caller = CallerContextFactory.From(http);
        caller.Require("group.manage");

        // The same escalation guard as group assignment, applied one step earlier. Without
        // it a caller could build a SUPER_ADMIN group and then hand it to themselves through
        // a route that only checks the group's contents at assignment time.
        if (await RoleEscalationProblemAsync(caller, repo, request.RoleId, ct) is { } problem)
        {
            return problem;
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
    }

    private static async Task<Results<Ok<AccessGroupResponse>, NotFound, ProblemHttpResult>> UpdateAsync(
        Guid id, [FromBody] UpdateGroupRequest request, AccessGroupRepository repo,
        NpgsqlDataSource db, HttpContext http, CancellationToken ct)
    {
        var caller = CallerContextFactory.From(http);
        caller.Require("group.manage");

        if (!await repo.IsVisibleToAsync(id, caller, "group.manage", ct)
            || await repo.GetAsync(id, ct) is not { } prior)
        {
            return TypedResults.NotFound();
        }

        // Re-checked on every edit, not just the ones that change the role: a caller who lost a
        // permission since the group was built must not be able to keep re-saving it either.
        if (await RoleEscalationProblemAsync(caller, repo, request.RoleId, ct) is { } problem)
        {
            return problem;
        }

        await using var work = await UnitOfWork.BeginAsync(db, ct);

        var updated = new AccessGroup
        {
            Id = id,
            Code = request.Code,
            Name = request.Name,
            Description = request.Description,
            RoleId = request.RoleId,
            Status = prior.Status,   // lifecycle is the activate / disable routes, not this one
        };
        await repo.UpsertAsync(updated, caller.UserId, work, ct);

        await work.AuditAsync(caller, "update", "access_group", id.ToString(),
            before: ToResponse(prior), after: request, organizationUnitId: null, ct);
        await work.CommitAsync(ct);

        var refreshed = await repo.GetAsync(id, ct);
        return TypedResults.Ok(ToResponse(refreshed!));
    }

    private static async Task<Results<NoContent, NotFound, ProblemHttpResult>> ActivateAsync(
        Guid id, AccessGroupRepository repo, NpgsqlDataSource db, HttpContext http,
        CancellationToken ct)
    {
        var caller = CallerContextFactory.From(http);
        caller.Require("group.manage");

        if (!await repo.IsVisibleToAsync(id, caller, "group.manage", ct)
            || await repo.GetAsync(id, ct) is not { } group)
        {
            return TypedResults.NotFound();
        }

        // Re-run the escalation guard on the group's effective permissions: a stale DRAFT may
        // carry a role the caller has since lost the standing to grant.
        if (!caller.IsUnscopedFor("group.manage"))
        {
            var granted = await repo.GetGroupPermissionsAsync(id, ct);
            var exceeding = granted.Where(p => !caller.Has(p)).OrderBy(p => p).ToList();
            if (exceeding.Count > 0)
            {
                return TypedResults.Problem(
                    title: "Would grant more than you hold",
                    detail: "This group's role grants permissions you do not have: "
                          + $"{string.Join(", ", exceeding)}.",
                    statusCode: StatusCodes.Status403Forbidden);
            }
        }

        // An unconstrained dimension is an estate-wide grant. Activating such a group is a
        // deliberate act only an already-unscoped administrator may take on that dimension.
        if (!await repo.HasOrganizationScopeAsync(id, ct) && !caller.IsUnscopedFor("group.manage"))
        {
            return UnscopedActivationProblem("organization");
        }

        if (!await repo.HasGeographyScopeAsync(id, ct)
            && !caller.IsUnscopedForGeography("group.manage"))
        {
            return UnscopedActivationProblem("geography");
        }

        await using var work = await UnitOfWork.BeginAsync(db, ct);
        await repo.SetStatusAsync(id, "ACTIVE", caller.UserId, work, ct);

        await work.AuditAsync(caller, "update", "access_group", id.ToString(),
            before: new { status = group.Status }, after: new { status = "ACTIVE" },
            organizationUnitId: null, ct);
        await work.CommitAsync(ct);

        return TypedResults.NoContent();
    }

    private static async Task<Results<NoContent, NotFound, ProblemHttpResult>> DisableAsync(
        Guid id, AccessGroupRepository repo, NpgsqlDataSource db, HttpContext http,
        CancellationToken ct)
    {
        var caller = CallerContextFactory.From(http);
        caller.Require("group.manage");

        if (!await repo.IsVisibleToAsync(id, caller, "group.manage", ct)
            || await repo.GetAsync(id, ct) is not { } group)
        {
            return TypedResults.NotFound();
        }

        await using var work = await UnitOfWork.BeginAsync(db, ct);
        await repo.SetStatusAsync(id, "DISABLED", caller.UserId, work, ct);

        await work.AuditAsync(caller, "update", "access_group", id.ToString(),
            before: new { status = group.Status }, after: new { status = "DISABLED" },
            organizationUnitId: null, ct);
        await work.CommitAsync(ct);

        return TypedResults.NoContent();
    }

    /// <summary>
    /// The escalation guard shared by group create and edit: a scoped caller may not point a
    /// group at a role granting permissions the caller does not hold. Unscoped callers are exempt.
    /// </summary>
    private static async Task<ProblemHttpResult?> RoleEscalationProblemAsync(
        CallerContext caller, AccessGroupRepository repo, Guid roleId, CancellationToken ct)
    {
        if (caller.IsUnscopedFor("group.manage"))
        {
            return null;
        }

        var granted = await repo.GetRolePermissionsAsync(roleId, ct);
        var exceeding = granted.Where(p => !caller.Has(p)).OrderBy(p => p).ToList();

        return exceeding.Count == 0
            ? null
            : TypedResults.Problem(
                title: "Would grant more than you hold",
                detail: "That role grants permissions you do not have: "
                      + $"{string.Join(", ", exceeding)}.",
                statusCode: StatusCodes.Status403Forbidden);
    }

    private static ProblemHttpResult UnscopedActivationProblem(string dimension) =>
        TypedResults.Problem(
            title: "Group is unrestricted on a dimension",
            detail: $"This group has no {dimension} scope, so activating it would grant its "
                  + $"permissions across every {(dimension == "organization" ? "department" : "area")}. "
                  + $"Add a {dimension} scope first, or activate it as an administrator already "
                  + $"unscoped for group.manage on that dimension.",
            statusCode: StatusCodes.Status403Forbidden);

    private static async Task<Results<Created<CreatedResponse>, NotFound, ProblemHttpResult>> AddScopeAsync(
        Guid id, [FromBody] AddScopeRequest request, AccessGroupRepository repo,
        NpgsqlDataSource db, HttpContext http, CancellationToken ct)
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
    }

    private static async Task<Results<NoContent, NotFound, ProblemHttpResult>> RemoveScopeAsync(
        Guid id, Guid scopeId, AccessGroupRepository repo, NpgsqlDataSource db,
        HttpContext http, CancellationToken ct)
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
    }

    private static async Task<Ok<IReadOnlyList<PermissionResponse>>> ListPermissionsAsync(
        AccessGroupRepository repo, HttpContext http, CancellationToken ct)
    {
        CallerContextFactory.From(http).Require("group.read");
        var permissions = await repo.ListPermissionsAsync(ct);
        return TypedResults.Ok<IReadOnlyList<PermissionResponse>>(
            [.. permissions.Select(x => new PermissionResponse(x.Code, x.Name, x.Category, x.Description))]);
    }
}
