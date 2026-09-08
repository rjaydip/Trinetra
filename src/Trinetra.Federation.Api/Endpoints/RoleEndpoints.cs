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
/// Roles — the composable permission bundles a group is built on.
/// </summary>
/// <remarks>
/// The seeded roles are <b>presets</b>: a starting point an operator renames and re-composes,
/// not a fixed set. Only <c>SUPER_ADMIN</c> is immutable. A preset's <c>code</c> is fixed and a
/// preset cannot be deleted. Because a role is a global object with no scope of its own, editing
/// or disabling a preset changes every group built on it everywhere — so preset writes require
/// <c>role.manage</c> held <b>unscoped</b>; a scoped holder is limited to custom roles.
/// <para>
/// Every write also enforces the escalation rule (a caller who is not unscoped for
/// <c>role.manage</c> may only touch permissions the caller already holds), checked in
/// <see cref="RoleRepository"/> against the role row read <c>FOR UPDATE</c>.
/// </para>
/// </remarks>
public static class RoleEndpoints
{
    private static RoleResponse ToResponse(RoleDetail r) =>
        new(r.Id, r.Code, r.Name, r.Description, r.IsSystem, r.Status, r.Customized, r.Permissions);

    public static void MapRoleEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/roles")
                       .WithTags(ApiTags.AccessControl)
                       .RequireAuthorization();

        group.MapGet("/", ListAsync)
          .RequirePermission("group.read")
          .WithSummary("List roles")
          .WithDescription(
              "Every role with the permission codes it grants. `isSystem` marks a preset shipped "
              + "with the platform; `customized` is true once someone has edited one. Roles carry "
              + "no scope of their own — scope is attached to the group.");

        group.MapGet("/{id:guid}", GetAsync)
          .RequirePermission("group.read")
          .WithSummary("Read one role")
          .WithDescription("The role and the exact set of permission codes it composes.");

        group.MapPost("/", CreateAsync)
          .RequirePermission("role.manage")
          .WithSummary("Create a custom role")
          .WithDescription(
              "Composes a new role from a set of permission codes (see `GET /api/v1/permissions`). "
              + "Always created as a non-preset role.\n\n"
              + "A caller who is not unscoped for `role.manage` cannot include a permission they "
              + "do not themselves hold — 403 naming the excess.");

        group.MapPut("/{id:guid}", UpdateAsync)
          .RequirePermission("role.manage")
          .WithSummary("Edit a role")
          .WithDescription(
              "Replaces `name`, `description`, `status` and the **entire** permission set with "
              + "what is sent. `code` in the body is ignored — a role's code never changes.\n\n"
              + "**Editing a preset (`isSystem`) requires `role.manage` held unscoped** (409 "
              + "otherwise) — a scoped admin must not re-compose a role the whole estate uses. "
              + "**`SUPER_ADMIN` cannot be edited at all** (409). Setting `status` to `INACTIVE` "
              + "makes every group on this role grant nothing. Same escalation guard as create.");

        group.MapDelete("/{id:guid}", DeleteAsync)
          .RequirePermission("role.manage")
          .WithSummary("Delete a custom role")
          .WithDescription(
              "Removes a role and its permission rows. **A preset cannot be deleted** (409) — "
              + "disable it instead. A role still referenced by an access group cannot be deleted "
              + "either (409); repoint or remove those groups first.");
    }

    private static async Task<Ok<IReadOnlyList<RoleResponse>>> ListAsync(
        RoleRepository repo, HttpContext http, CancellationToken ct)
    {
        CallerContextFactory.From(http).Require("group.read");
        var roles = await repo.ListAsync(ct);
        return TypedResults.Ok<IReadOnlyList<RoleResponse>>([.. roles.Select(ToResponse)]);
    }

    private static async Task<Results<Ok<RoleResponse>, NotFound>> GetAsync(
        Guid id, RoleRepository repo, HttpContext http, CancellationToken ct)
    {
        CallerContextFactory.From(http).Require("group.read");
        var role = await repo.GetAsync(id, ct);
        return role is null ? TypedResults.NotFound() : TypedResults.Ok(ToResponse(role));
    }

    private static async Task<Results<Created<CreatedResponse>, ProblemHttpResult>> CreateAsync(
        [FromBody] RoleWriteRequest request, RoleRepository repo,
        NpgsqlDataSource db, HttpContext http, CancellationToken ct)
    {
        var caller = CallerContextFactory.From(http);
        caller.Require("role.manage");

        await using var work = await UnitOfWork.BeginAsync(db, ct);

        var result = await repo.CreateAsync(
            request.Code, request.Name, request.Description, request.Status,
            request.Permissions, caller, work, ct);

        if (Problem(result) is { } problem)
        {
            return problem;
        }

        await work.AuditAsync(caller, "create", "role", result.Id.ToString(),
            before: null, after: request, organizationUnitId: null, ct);
        await work.CommitAsync(ct);

        return TypedResults.Created($"/api/v1/roles/{result.Id}", new CreatedResponse(result.Id));
    }

    private static async Task<Results<Ok<RoleResponse>, NotFound, ProblemHttpResult>> UpdateAsync(
        Guid id, [FromBody] RoleWriteRequest request, RoleRepository repo,
        NpgsqlDataSource db, HttpContext http, CancellationToken ct)
    {
        var caller = CallerContextFactory.From(http);
        caller.Require("role.manage");

        await using var work = await UnitOfWork.BeginAsync(db, ct);

        var prior = await repo.GetAsync(id, ct);
        var result = await repo.UpdateAsync(
            id, request.Name, request.Description, request.Status,
            request.Permissions, caller, work, ct);

        if (result.Error == RoleWriteError.NotFound)
        {
            return TypedResults.NotFound();
        }

        if (Problem(result) is { } problem)
        {
            return problem;
        }

        var updated = await repo.GetAsync(id, ct);
        await work.AuditAsync(caller, "update", "role", id.ToString(),
            before: prior is null ? null : ToResponse(prior), after: request,
            organizationUnitId: null, ct);
        await work.CommitAsync(ct);

        return TypedResults.Ok(ToResponse(updated!));
    }

    private static async Task<Results<NoContent, NotFound, ProblemHttpResult>> DeleteAsync(
        Guid id, RoleRepository repo, NpgsqlDataSource db, HttpContext http, CancellationToken ct)
    {
        var caller = CallerContextFactory.From(http);
        caller.Require("role.manage");

        var prior = await repo.GetAsync(id, ct);

        await using var work = await UnitOfWork.BeginAsync(db, ct);
        var result = await repo.DeleteAsync(id, work, ct);

        if (result.Error == RoleWriteError.NotFound)
        {
            return TypedResults.NotFound();
        }

        if (Problem(result) is { } problem)
        {
            return problem;
        }

        await work.AuditAsync(caller, "delete", "role", id.ToString(),
            before: prior is null ? null : ToResponse(prior), after: null,
            organizationUnitId: null, ct);
        await work.CommitAsync(ct);

        return TypedResults.NoContent();
    }

    private static ProblemHttpResult? Problem(RoleWriteResult result) => result.Error switch
    {
        RoleWriteError.None or RoleWriteError.NotFound => null,

        RoleWriteError.Escalation => TypedResults.Problem(
            title: "Would grant more than you hold",
            detail: "This role would carry permissions you do not have: "
                  + $"{string.Join(", ", result.Exceeding)}.",
            statusCode: StatusCodes.Status403Forbidden),

        RoleWriteError.Locked => TypedResults.Problem(
            title: "SUPER_ADMIN cannot be edited",
            detail: "It is the recovery role the platform depends on. Compose a new role instead.",
            statusCode: StatusCodes.Status409Conflict),

        RoleWriteError.PresetRequiresUnscoped => TypedResults.Problem(
            title: "Preset edits require unscoped role.manage",
            detail: "This is a preset role used across the estate. Editing or disabling it "
                  + "requires role.manage held without an organization or geography scope. Create "
                  + "a custom role instead.",
            statusCode: StatusCodes.Status409Conflict),

        RoleWriteError.PresetShapeFixed => TypedResults.Problem(
            title: "Preset roles cannot be deleted",
            detail: "A role shipped with the platform can be edited or disabled, not removed.",
            statusCode: StatusCodes.Status409Conflict),

        RoleWriteError.InUse => TypedResults.Problem(
            title: "Role is in use",
            detail: "An access group still references this role. Repoint or remove those groups "
                  + "first.",
            statusCode: StatusCodes.Status409Conflict),

        RoleWriteError.CodeInUse => TypedResults.Problem(
            title: "Role code already exists",
            detail: "Another role already uses that code.",
            statusCode: StatusCodes.Status409Conflict),

        RoleWriteError.UnknownPermission => TypedResults.Problem(
            title: "Unknown permission",
            detail: "One of the supplied permission codes is not in the vocabulary. See "
                  + "GET /api/v1/permissions.",
            statusCode: StatusCodes.Status400BadRequest),

        _ => null,
    };
}
