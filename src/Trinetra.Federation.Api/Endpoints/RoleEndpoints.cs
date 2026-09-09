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
        new(r.Id, r.Code, r.Name, r.Description, r.IsSystem, r.Status, r.Customized,
            r.CreatedAt, r.UpdatedAt, r.CustomizedAt, r.UsageCount,
            r.Permissions,
            r.PermissionDetails is null
                ? null
                : [.. r.PermissionDetails.Select(d =>
                    new RolePermissionDetailResponse(d.Code, d.Name, d.Category, d.Description))],
            r.UsingGroups is null
                ? null
                : [.. r.UsingGroups.Select(g => new RoleUsedByResponse(g.Id, g.Code, g.Name, g.Status))]);

    public static void MapRoleEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/roles")
                       .WithTags(ApiTags.AccessControl)
                       .RequireAuthorization();

        group.MapGet("/", ListAsync)
          .RequirePermission("role.read")
          .WithSummary("List roles")
          .WithDescription(
              "Roles with the permission codes each grants and a `usageCount` of the access "
              + "groups on it. `isSystem` marks a preset shipped with the platform; `customized` "
              + "is true once someone has edited one. Defaults to `ACTIVE` roles only — pass "
              + "`includeInactive=true` to also see `DRAFT` and `INACTIVE` roles. Roles carry no "
              + "scope of their own — scope is attached to the group.");

        group.MapGet("/{id:guid}", GetAsync)
          .RequirePermission("role.read")
          .WithSummary("Read one role")
          .WithDescription(
              "The role, the permission codes it composes plus their metadata, its lifecycle "
              + "timestamps, and `usedBy` — the access groups on this role that you may see "
              + "(`usageCount` is the true total regardless of visibility).");

        group.MapPost("/", CreateAsync)
          .RequirePermission("role.manage")
          .WithSummary("Create a custom role")
          .WithDescription(
              "Composes a new role from a set of permission codes (see `GET /api/v1/permissions`). "
              + "Always a non-preset role, and **created as `DRAFT`** unless `status` is given — "
              + "compose it, then flip it to `ACTIVE` with `PUT`. A `DRAFT` role grants nothing.\n\n"
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
          .WithSummary("Soft-delete a custom role")
          .WithDescription(
              "Sets the role to `INACTIVE`. The row and its permission rows are kept, so a later "
              + "`PUT status: ACTIVE` restores it. **A preset cannot be deleted** (409) — disable "
              + "it instead — and **`SUPER_ADMIN` cannot be touched** (409). A role still "
              + "referenced by access groups **can** be soft-deleted: those groups keep their "
              + "`roleId` and simply grant nothing onward. Idempotent on an already-`INACTIVE` "
              + "role.");
    }

    private static async Task<Ok<IReadOnlyList<RoleResponse>>> ListAsync(
        bool? includeInactive, RoleRepository repo, HttpContext http, CancellationToken ct)
    {
        CallerContextFactory.From(http).Require("role.read");
        var roles = await repo.ListAsync(includeInactive ?? false, ct);
        return TypedResults.Ok<IReadOnlyList<RoleResponse>>([.. roles.Select(ToResponse)]);
    }

    private static async Task<Results<Ok<RoleResponse>, NotFound>> GetAsync(
        Guid id, RoleRepository repo, HttpContext http, CancellationToken ct)
    {
        var caller = CallerContextFactory.From(http);
        caller.Require("role.read");
        var role = await repo.GetAsync(id, caller, ct);
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

        // Audit the persisted role — generated id, the status the server applied (DRAFT unless
        // asked otherwise), the exact permission set — not the raw request DTO.
        await work.AuditAsync(caller, "create", "role", result.Id.ToString(),
            before: null, after: ToResponse(result.Detail!), organizationUnitId: null, ct);
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

        var prior = await repo.GetAsync(id, caller, ct);
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

        // result.Detail is the role read back INSIDE the transaction — re-reading via GetAsync
        // here would open a fresh connection that cannot see the uncommitted change.
        var updated = result.Detail!;
        await work.AuditAsync(caller, "update", "role", id.ToString(),
            before: prior is null ? null : ToResponse(prior), after: ToResponse(updated),
            organizationUnitId: null, ct);
        await work.CommitAsync(ct);

        return TypedResults.Ok(ToResponse(updated));
    }

    private static async Task<Results<NoContent, NotFound, ProblemHttpResult>> DeleteAsync(
        Guid id, RoleRepository repo, NpgsqlDataSource db, HttpContext http, CancellationToken ct)
    {
        var caller = CallerContextFactory.From(http);
        caller.Require("role.manage");

        var prior = await repo.GetAsync(id, caller, ct);

        await using var work = await UnitOfWork.BeginAsync(db, ct);
        var result = await repo.DeleteAsync(id, caller, work, ct);

        if (result.Error == RoleWriteError.NotFound)
        {
            return TypedResults.NotFound();
        }

        if (Problem(result) is { } problem)
        {
            return problem;
        }

        await work.AuditAsync(caller, "delete", "role", id.ToString(),
            before: prior is null ? null : ToResponse(prior),
            after: new { status = "INACTIVE" },
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

        RoleWriteError.InvalidStatus => TypedResults.Problem(
            title: "Invalid role status",
            detail: "status must be DRAFT, ACTIVE or INACTIVE, and a role that has left DRAFT "
                  + "cannot be returned to it.",
            statusCode: StatusCodes.Status400BadRequest),

        _ => null,
    };
}
