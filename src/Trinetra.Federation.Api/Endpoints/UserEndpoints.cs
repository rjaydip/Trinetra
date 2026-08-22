using Npgsql;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Trinetra.Federation.Api.Auth;
using Trinetra.Federation.Api.Contracts;
using Trinetra.Federation.Storage;
using Trinetra.Federation.Storage.Repositories;
using Trinetra.Federation.Storage.Security;

namespace Trinetra.Federation.Api.Endpoints;

/// <summary>
/// User administration.
/// </summary>
/// <remarks>
/// <para>
/// The dangerous surface of the whole API. Every mutation here is guarded against two failures
/// that are easy to miss and expensive to discover:
/// </para>
/// <list type="bullet">
/// <item><b>Privilege escalation</b> — a departmental administrator granting themselves, or
/// anyone else, rights they do not hold.</item>
/// <item><b>Lockout</b> — removing the last account able to administer the platform, which
/// cannot be undone without direct database access.</item>
/// </list>
/// </remarks>
public static class UserEndpoints
{
    // Projection kept explicit so a column added to storage never becomes part of the contract
    // by accident — which is exactly how password material ends up in a response.
    private static UserResponse ToResponse(PlatformUser u) => new(
        u.Id, u.Username, u.DisplayName, u.Email,
        u.MustChangePassword, u.Status, u.LastLoginAt, u.IsSystem);

    /// <summary>
    /// Refuses a mutation on a user the caller has no authority over.
    /// </summary>
    /// <remarks>
    /// Returns 404 rather than 403 for the same reason out-of-scope targets do: confirming that
    /// a user id exists is itself a disclosure to someone who may not administer them.
    /// </remarks>
    private static async Task<ProblemHttpResult?> RefuseIfBeyondAuthorityAsync(
        Guid targetUserId, CallerContext caller, UserRepository users, CancellationToken ct)
    {
        var unscoped = caller.IsUnscopedFor("user.manage");

        // The seeded account is the one every other was created from. Only an unscoped caller
        // may touch it, whatever their permissions otherwise say.
        if (!unscoped && await users.IsSystemAccountAsync(targetUserId, ct))
        {
            return TypedResults.Problem(
                title: "Not found", statusCode: StatusCodes.Status404NotFound);
        }

        if (!await users.CanAdministerAsync(targetUserId, caller.UserId, unscoped, ct))
        {
            return TypedResults.Problem(
                title: "Not found", statusCode: StatusCodes.Status404NotFound);
        }

        return null;
    }

    public static void MapUserEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/users").WithTags("Users").RequireAuthorization();

        group.MapGet("/", async Task<Ok<IReadOnlyList<UserResponse>>> (
            UserRepository users, HttpContext http, CancellationToken ct) =>
        {
            CallerContextFactory.From(http).Require("user.read");
            var all = await users.ListAsync(ct);
            return TypedResults.Ok<IReadOnlyList<UserResponse>>([.. all.Select(ToResponse)]);
        }).RequirePermission("user.read");

        group.MapGet("/{id:guid}", async Task<Results<Ok<UserResponse>, NotFound>> (
            Guid id, UserRepository users, HttpContext http, CancellationToken ct) =>
        {
            CallerContextFactory.From(http).Require("user.read");
            var user = await users.GetAsync(id, ct);
            return user is null ? TypedResults.NotFound() : TypedResults.Ok(ToResponse(user));
        }).RequirePermission("user.read");

        group.MapGet("/{id:guid}/groups", async Task<Ok<IReadOnlyList<UserGroupResponse>>> (
            Guid id, UserRepository users, HttpContext http, CancellationToken ct) =>
        {
            CallerContextFactory.From(http).Require("user.read");
            var groups = await users.ListGroupsAsync(id, ct);
            return TypedResults.Ok<IReadOnlyList<UserGroupResponse>>(
                [.. groups.Select(g => new UserGroupResponse(g.GroupId, g.Code, g.Name, g.ExpiresAt))]);
        }).RequirePermission("user.read");

        // Effective permissions, for diagnosing "why can this person not see that camera".
        group.MapGet("/{id:guid}/permissions", async Task<Ok<IReadOnlyList<string>>> (
            Guid id, UserRepository users, HttpContext http, CancellationToken ct) =>
        {
            CallerContextFactory.From(http).Require("user.read");
            var permissions = await users.GetPermissionsAsync(id, ct);
            return TypedResults.Ok<IReadOnlyList<string>>([.. permissions.OrderBy(p => p, StringComparer.Ordinal)]);
        }).RequirePermission("user.read");

        group.MapPost("/", async Task<Results<Created<CreatedResponse>, ProblemHttpResult>> (
            [FromBody] CreateUserRequest request, UserRepository users,
            NpgsqlDataSource db, HttpContext http, CancellationToken ct) =>
        {
            var caller = CallerContextFactory.From(http);
            caller.Require("user.manage");

            if (!PasswordHasher.IsAcceptable(request.Password, out var reason))
            {
                return TypedResults.Problem(
                    title: "Password rejected", detail: reason,
                    statusCode: StatusCodes.Status400BadRequest);
            }

            if (await users.ExistsAsync(request.Username, ct))
            {
                return TypedResults.Problem(
                    title: "Username taken",
                    detail: $"A user named '{request.Username}' already exists.",
                    statusCode: StatusCodes.Status409Conflict);
            }

            // A new account holds nothing until it is put in a group, so creating one grants no
            // access by itself. Forcing a password change means the creator never knows the
            // credential the user ends up with.
            await using var work = await UnitOfWork.BeginAsync(db, ct);

            var id = await users.CreateAsync(
                request.Username, request.DisplayName, request.Email,
                PasswordHasher.Hash(request.Password),
                mustChangePassword: true, isSystem: false, work, ct);

            await work.AuditAsync(caller, "create", "user", id.ToString(),
                before: null,
                after: new { request.Username, request.DisplayName, request.Email },
                organizationUnitId: null, ct);
            await work.CommitAsync(ct);

            return TypedResults.Created($"/api/v1/users/{id}", new CreatedResponse(id));
        }).RequirePermission("user.manage");

        group.MapPut("/{id:guid}", async Task<Results<NoContent, NotFound, ProblemHttpResult>> (
            Guid id, [FromBody] UpdateUserRequest request, UserRepository users,
            AccessGroupRepository groups, NpgsqlDataSource db,
            HttpContext http, CancellationToken ct) =>
        {
            var caller = CallerContextFactory.From(http);
            caller.Require("user.manage");

            var before = await users.GetAsync(id, ct);
            if (before is null)
            {
                return TypedResults.NotFound();
            }

            if (await RefuseIfBeyondAuthorityAsync(id, caller, users, ct) is { } refused)
            {
                return refused;
            }

            var status = request.Status ?? before.Status;

            // Deactivating the last user who can administer the platform is unrecoverable
            // without direct database access, so it is refused outright.
            if (status != "ACTIVE" && before.Status == "ACTIVE"
                && await groups.CountOtherHoldersAsync("user.manage", id, ct) == 0)
            {
                return TypedResults.Problem(
                    title: "Would lock everyone out",
                    detail: "This is the only active account that can administer users. "
                          + "Grant another user administrative access before deactivating it.",
                    statusCode: StatusCodes.Status409Conflict);
            }

            await using var work = await UnitOfWork.BeginAsync(db, ct);

            await users.UpdateAsync(id, request.DisplayName, request.Email, status, work, ct);

            await work.AuditAsync(caller, "update", "user", id.ToString(), before,
                new { request.DisplayName, request.Email, status }, organizationUnitId: null, ct);
            await work.CommitAsync(ct);

            return TypedResults.NoContent();
        }).RequirePermission("user.manage");

        group.MapPost("/{id:guid}/password", async Task<Results<NoContent, NotFound, ProblemHttpResult>> (
            Guid id, [FromBody] ResetPasswordRequest request, UserRepository users,
            NpgsqlDataSource db, HttpContext http, CancellationToken ct) =>
        {
            var caller = CallerContextFactory.From(http);
            caller.Require("user.manage");

            if (!PasswordHasher.IsAcceptable(request.NewPassword, out var reason))
            {
                return TypedResults.Problem(
                    title: "Password rejected", detail: reason,
                    statusCode: StatusCodes.Status400BadRequest);
            }

            if (await users.GetAsync(id, ct) is null)
            {
                return TypedResults.NotFound();
            }

            // Without this, user.manage alone was enough to reset the bootstrap administrator's
            // password and take the platform.
            if (await RefuseIfBeyondAuthorityAsync(id, caller, users, ct) is { } refused)
            {
                return refused;
            }

            // Always forces a change: an administrator resetting a password necessarily knows
            // it, so it must not remain the user's working credential.
            await using var work = await UnitOfWork.BeginAsync(db, ct);

            await users.SetPasswordAsync(
                id, PasswordHasher.Hash(request.NewPassword), mustChange: true, work, ct);

            // Records that a reset happened, never the value.
            await work.AuditAsync(caller, "update", "user_password", id.ToString(),
                before: null, after: new { reset = true, mustChangePassword = true }, organizationUnitId: null, ct);
            await work.CommitAsync(ct);

            return TypedResults.NoContent();
        }).RequirePermission("user.manage");

        // ---- Group membership ----------------------------------------------

        group.MapPost("/{id:guid}/groups", async Task<Results<NoContent, NotFound, ProblemHttpResult>> (
            Guid id, [FromBody] AssignGroupRequest request, UserRepository users,
            AccessGroupRepository groups, NpgsqlDataSource db,
            HttpContext http, CancellationToken ct) =>
        {
            var caller = CallerContextFactory.From(http);
            caller.Require("user.manage");

            if (await users.GetAsync(id, ct) is null)
            {
                return TypedResults.NotFound();
            }

            if (await RefuseIfBeyondAuthorityAsync(id, caller, users, ct) is { } refused)
            {
                return refused;
            }

            var target = await groups.GetAsync(request.GroupId, ct);
            if (target is null)
            {
                return TypedResults.Problem(
                    title: "Unknown group", detail: "No such access group.",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            // THE escalation guard. Without it, anyone holding user.manage could put themselves
            // in the platform administrators group and own the estate — a one-request takeover
            // from the lowest privilege that can manage users at all.
            // THE escalation chokepoint. Three independent things must hold, and checking only
            // the first was a real takeover path: a caller could create a group carrying their
            // own role, never scope it — an unscoped group reaches every department — and assign
            // it to themselves. The permission-subset check passed, because the permissions were
            // identical to their own. Only the scope checks catch it.
            if (!caller.IsUnscopedFor("user.manage"))
            {
                // 1. Cannot hand out permissions you do not hold.
                var granted = await groups.GetGroupPermissionsAsync(request.GroupId, ct);
                var exceeding = granted.Where(p => !caller.Has(p)).OrderBy(p => p).ToList();

                if (exceeding.Count > 0)
                {
                    return TypedResults.Problem(
                        title: "Would grant more than you hold",
                        detail: $"'{target.Code}' grants permissions you do not have: "
                              + $"{string.Join(", ", exceeding)}. You cannot give away access "
                              + "you were not given.",
                        statusCode: StatusCodes.Status403Forbidden);
                }

                // 2. Cannot hand out a group that is unrestricted by organization. This is the
                // one that closes the takeover: an unscoped group grants its permissions over
                // every department, so granting one is granting the estate.
                if (!await groups.HasOrganizationScopeAsync(request.GroupId, ct))
                {
                    return TypedResults.Problem(
                        title: "Group is unrestricted by organization",
                        detail: $"'{target.Code}' declares no organization scope, so it applies "
                              + "across every department. Only an administrator who is already "
                              + "unscoped may grant it.",
                        statusCode: StatusCodes.Status403Forbidden);
                }

                // 3. Cannot hand out your own permissions over someone else's department.
                if (!await groups.GroupScopesWithinReachAsync(
                        request.GroupId, caller, "user.manage", ct))
                {
                    return TypedResults.Problem(
                        title: "Group reaches beyond your scope",
                        detail: $"'{target.Code}' is scoped to organization units you do not "
                              + "administer.",
                        statusCode: StatusCodes.Status403Forbidden);
                }
            }

            await using var work = await UnitOfWork.BeginAsync(db, ct);

            await users.AssignGroupAsync(id, request.GroupId, caller.UserId, request.ExpiresAt, work, ct);

            await work.AuditAsync(caller, "update", "user_group", id.ToString(),
                before: null,
                after: new { groupId = request.GroupId, group = target.Code, request.ExpiresAt },
                organizationUnitId: null, ct);
            await work.CommitAsync(ct);

            return TypedResults.NoContent();
        }).RequirePermission("user.manage");

        group.MapDelete("/{id:guid}/groups/{groupId:guid}", async Task<Results<NoContent, NotFound, ProblemHttpResult>> (
            Guid id, Guid groupId, UserRepository users, AccessGroupRepository groups,
            NpgsqlDataSource db, HttpContext http, CancellationToken ct) =>
        {
            var caller = CallerContextFactory.From(http);
            caller.Require("user.manage");

            if (await RefuseIfBeyondAuthorityAsync(id, caller, users, ct) is { } refused)
            {
                return refused;
            }

            var target = await groups.GetAsync(groupId, ct);

            // Same lockout guard as deactivation: revoking the last administrative membership is
            // equally unrecoverable, and easier to do by accident.
            if (target is not null && target.Permissions.Contains("user.manage")
                && await groups.CountOtherHoldersAsync("user.manage", id, ct) == 0)
            {
                return TypedResults.Problem(
                    title: "Would lock everyone out",
                    detail: "This membership is the only remaining grant of user administration. "
                          + "Grant it to another user first.",
                    statusCode: StatusCodes.Status409Conflict);
            }

            await using var work = await UnitOfWork.BeginAsync(db, ct);

            if (!await groups.RevokeMembershipAsync(id, groupId, work, ct))
            {
                return TypedResults.NotFound();
            }

            await work.AuditAsync(caller, "delete", "user_group", id.ToString(),
                before: new { groupId }, after: null, organizationUnitId: null, ct);
            await work.CommitAsync(ct);

            return TypedResults.NoContent();
        }).RequirePermission("user.manage");
    }
}
