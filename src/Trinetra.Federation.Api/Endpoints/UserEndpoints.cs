using Npgsql;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Trinetra.Federation.Api.Auth;
using Trinetra.Federation.Api.Contracts;
using Trinetra.Federation.Api.OpenApi;
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

    public static void MapUserEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/users").WithTags(ApiTags.Users).RequireAuthorization();

        group.MapGet("/", ListAsync)
          .WithPaginatedResponse<UserResponse>()
          .RequirePermission("user.read")
          .WithSummary("List user accounts")
          .WithDescription(
              "The accounts you may administer, each with its username, display name, email and "
              + "status. The directory a client draws an administration screen from. Password "
              + "material is not part of the response type at all.\n\n"
              + "A scoped administrator sees only accounts within their organizational reach — "
              + "not the seed account, not anyone held through an estate-wide group, not another "
              + "department's users. An administrator unscoped for `user.read` sees everyone.\n\n"
              + Paginate.Doc);

        group.MapGet("/{id:guid}", GetAsync)
          .RequirePermission("user.read")
          .WithSummary("Read one user account")
          .WithDescription(
              "Profile and status for a single account. What the account can *do* is not here — "
              + "that comes from group membership, under `/users/{id}/groups` and "
              + "`/users/{id}/permissions`.\n\n"
              + "An account outside your authority returns `404`, identical to one that does not "
              + "exist — confirming an id is itself a disclosure.");

        group.MapGet("/{id:guid}/groups", GroupsAsync)
          .RequirePermission("user.read")
          .WithSummary("List a user's group memberships")
          .WithDescription(
              "The access groups this account belongs to, each with an optional `expiresAt`. An "
              + "expired membership stops granting anything without anyone having to revoke it — "
              + "the intended way to give temporary access.\n\n"
              + "This is the only route to what a user is entitled to: permissions are never "
              + "granted to an account directly. Requires authority over the account — otherwise "
              + "`404`, so a scoped administrator cannot enumerate a higher administrator's "
              + "entitlements.");

        // Effective permissions, for diagnosing "why can this person not see that camera".
        group.MapGet("/{id:guid}/permissions", PermissionsAsync)
          .RequirePermission("user.read")
          .WithSummary("Resolve a user's effective permissions")
          .WithDescription(
              "The flattened set of permission codes this account currently holds, resolved "
              + "through every unexpired group membership. **Computed, not stored** — there is "
              + "nothing to write here; change membership instead.\n\n"
              + "This is the route for answering 'why can this person not see that camera'. Note "
              + "that a permission code alone does not say *where* it applies: scope comes from "
              + "the grants on the groups that carried it, under `/access-groups/{id}`.\n\n"
              + "Requires authority over the account — otherwise `404`.");

        group.MapPost("/", CreateAsync)
          .RequirePermission("user.manage")
          .WithSummary("Create a user account")
          .WithDescription(
              "Creates an account and nothing else: **a new user holds no permissions until "
              + "added to a group**, so this grants no access by itself.\n\n"
              + "The initial password must meet the policy and is always flagged must-change, so "
              + "the credential the creator typed never stays the user's working one. A duplicate "
              + "username is a 409.");

        group.MapPut("/{id:guid}", UpdateAsync)
          .RequirePermission("user.manage")
          .WithSummary("Update a user's profile or status")
          .WithDescription(
              "Changes display name, email or status. Membership is not touched here — use "
              + "`/users/{id}/groups`.\n\n"
              + "Deactivating an account (setting `status` to anything other than `ACTIVE`) "
              + "takes effect **immediately**: its access tokens fail on their next request and "
              + "all its refresh tokens are revoked, in the same transaction as the status "
              + "change. Re-activating later does not revive those tokens.\n\n"
              + "Deactivating the **last active account that can administer users** is refused "
              + "with 409: nothing in the running system could undo it, and recovery would mean "
              + "direct database access. Editing an account with more authority than the caller "
              + "is refused as well.");

        group.MapPost("/{id:guid}/password", ResetPasswordAsync)
          .RequirePermission("user.manage")
          .WithSummary("Reset another user's password")
          .WithDescription(
              "The administrative reset, for an account whose holder cannot log in. A user "
              + "changing their own password uses `POST /auth/password` instead, which requires "
              + "the current one.\n\n"
              + "The new password always lands flagged must-change: an administrator performing a "
              + "reset necessarily knows the value, so it must not remain the user's working "
              + "credential. The reset also **ends every session the account has** — access "
              + "tokens fail on next use, refresh tokens are revoked.\n\n"
              + "Resetting an account with more authority than the caller is refused — otherwise "
              + "`user.manage` alone would be enough to reset the bootstrap administrator and "
              + "take the platform. The audit row records that a reset happened, never the "
              + "value.");

        // ---- Group membership ----------------------------------------------

        group.MapPost("/{id:guid}/groups", AddToGroupAsync)
          .RequirePermission("user.manage")
          .WithSummary("Add a user to an access group")
          .WithDescription(
              "**How permissions are granted.** Optionally with `expiresAt`, which is the "
              + "intended way to give access for an incident or a secondment — it lapses on its "
              + "own rather than depending on someone remembering to revoke it.\n\n"
              + "This is the platform's privilege-escalation chokepoint, and a scoped caller must "
              + "clear three checks, each a 403:\n"
              + "1. the group grants no permission the caller does not already hold;\n"
              + "2. the group declares an organization scope — an unscoped group applies across "
              + "every department, so granting one is granting the estate;\n"
              + "3. the group's scopes are within the units the caller administers.\n\n"
              + "A caller who is unscoped for `user.manage` bypasses all three, which is what "
              + "being unscoped means.\n\n"
              + "Granting a membership does **not** force the user to log in again — a newly "
              + "granted permission appears in their access token the next time their client "
              + "refreshes it (within ~15 minutes). Only *removal* is applied immediately.");

        group.MapDelete("/{id:guid}/groups/{groupId:guid}", RemoveFromGroupAsync)
          .RequirePermission("user.manage")
          .WithSummary("Remove a user from an access group")
          .WithDescription(
              "Revokes a membership and every permission it carried. **Takes effect "
              + "immediately:** the removal bumps the user's token version and revokes all their "
              + "refresh tokens in the same transaction, so an access token still carrying the "
              + "removed permission fails on its next request and the user must sign in again. "
              + "Scope is also resolved against the database on every query.\n\n"
              + "Revoking the last remaining grant of user administration is refused with 409, "
              + "the same lockout guard as deactivation and easier to trip by accident.");
    }

    private const int ListHardCap = 1000;

    private static async Task<IResult> ListAsync(
        int? page, int? pageSize, UserRepository users, HttpContext http, CancellationToken ct)
    {
        var caller = CallerContextFactory.From(http);
        caller.Require("user.read");

        var q = new PageQuery(page, pageSize);
        var rows = await users.ListPageAsync(
            caller, new PageWindow(q.Limit(ListHardCap, ListHardCap), q.Offset(ListHardCap)), ct);

        return Paginate.Render(
            http, q, ListHardCap, [.. rows.Items.Select(ToResponse)], rows.Total);
    }

    private static async Task<Results<Ok<UserResponse>, NotFound, ProblemHttpResult>> GetAsync(
        Guid id, UserRepository users, HttpContext http, CancellationToken ct)
    {
        var caller = CallerContextFactory.From(http);
        caller.Require("user.read");

        if (await UserAuthorityGuard.RefuseAsync(id, caller, "user.read", users, ct) is { } refused)
        {
            return refused;
        }

        var user = await users.GetAsync(id, ct);
        return user is null ? TypedResults.NotFound() : TypedResults.Ok(ToResponse(user));
    }

    private static async Task<Results<Ok<IReadOnlyList<UserGroupResponse>>, ProblemHttpResult>> GroupsAsync(
        Guid id, UserRepository users, HttpContext http, CancellationToken ct)
    {
        var caller = CallerContextFactory.From(http);
        caller.Require("user.read");

        if (await UserAuthorityGuard.RefuseAsync(id, caller, "user.read", users, ct) is { } refused)
        {
            return refused;
        }

        var groups = await users.ListGroupsAsync(id, ct);
        return TypedResults.Ok<IReadOnlyList<UserGroupResponse>>(
            [.. groups.Select(g => new UserGroupResponse(g.GroupId, g.Code, g.Name, g.ExpiresAt))]);
    }

    private static async Task<Results<Ok<IReadOnlyList<string>>, ProblemHttpResult>> PermissionsAsync(
        Guid id, UserRepository users, HttpContext http, CancellationToken ct)
    {
        var caller = CallerContextFactory.From(http);
        caller.Require("user.read");

        if (await UserAuthorityGuard.RefuseAsync(id, caller, "user.read", users, ct) is { } refused)
        {
            return refused;
        }

        var permissions = await users.GetPermissionsAsync(id, ct);
        return TypedResults.Ok<IReadOnlyList<string>>([.. permissions.OrderBy(p => p, StringComparer.Ordinal)]);
    }

    private static async Task<Results<Created<CreatedResponse>, ProblemHttpResult>> CreateAsync(
        [FromBody] CreateUserRequest request, UserRepository users,
        NpgsqlDataSource db, HttpContext http, CancellationToken ct)
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
    }

    private static async Task<Results<NoContent, NotFound, ProblemHttpResult>> UpdateAsync(
        Guid id, [FromBody] UpdateUserRequest request, UserRepository users,
        AccessGroupRepository groups, RefreshTokenRepository refreshTokens, NpgsqlDataSource db,
        HttpContext http, CancellationToken ct)
    {
        var caller = CallerContextFactory.From(http);
        caller.Require("user.manage");

        var before = await users.GetAsync(id, ct);
        if (before is null)
        {
            return TypedResults.NotFound();
        }

        if (await UserAuthorityGuard.RefuseAsync(id, caller, "user.manage", users, ct) is { } refused)
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

        // Deactivation is immediate: kill every session the account has, in this same
        // transaction, so an access token issued minutes ago cannot outlive it.
        if (status != "ACTIVE" && before.Status == "ACTIVE")
        {
            await SessionRevocation.EndAllAsync(users, refreshTokens, id, work, ct);
        }

        await work.AuditAsync(caller, "update", "user", id.ToString(), before,
            new { request.DisplayName, request.Email, status }, organizationUnitId: null, ct);
        await work.CommitAsync(ct);

        return TypedResults.NoContent();
    }

    private static async Task<Results<NoContent, NotFound, ProblemHttpResult>> ResetPasswordAsync(
        Guid id, [FromBody] ResetPasswordRequest request, UserRepository users,
        RefreshTokenRepository refreshTokens, AuthAuditRepository authAudit, NpgsqlDataSource db,
        HttpContext http, CancellationToken ct)
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
        if (await UserAuthorityGuard.RefuseAsync(id, caller, "user.manage", users, ct) is { } refused)
        {
            return refused;
        }

        // History is enforced (a reset can't put the password back to a recently-used value);
        // the minimum-age check is NOT — an admin unlocking a stuck account must not be blocked
        // by how recently the account's own password changed (finding 4-M5).
        var history = await users.LoadPasswordHistoryAsync(id, ct);
        if (PasswordChangeGuard.Check(request.NewPassword, history, enforceMinimumAge: false)
            is { } historyRejected)
        {
            return historyRejected;
        }

        // Always forces a change: an administrator resetting a password necessarily knows
        // it, so it must not remain the user's working credential.
        await using var work = await UnitOfWork.BeginAsync(db, ct);

        await users.SetPasswordAsync(
            id, PasswordHasher.Hash(request.NewPassword), mustChange: true, work, ct);

        // The reset also ends every session the account currently has — the leaked or forgotten
        // credential that prompted the reset must not keep working through an old token.
        await SessionRevocation.EndAllAsync(users, refreshTokens, id, work, ct);

        // Records that a reset happened, never the value.
        await work.AuditAsync(caller, "update", "user_password", id.ToString(),
            before: null,
            after: new { reset = true, mustChangePassword = true, sessionsEnded = true },
            organizationUnitId: null, ct);

        var (ip, ua) = RequestClientMeta.From(http);
        await AuthAuditRepository.WriteAsync(work, new AuthAuditEntry
        {
            EventType = AuthAuditEvents.PasswordAdminReset, Outcome = "success",
            UserId = id, SourceAddress = ip, UserAgent = ua,
            Detail = new { actorUserId = caller.UserId, actor = caller.Actor, sessionsEnded = true },
        }, ct);

        await work.CommitAsync(ct);

        return TypedResults.NoContent();
    }

    private static async Task<Results<NoContent, NotFound, ProblemHttpResult>> AddToGroupAsync(
        Guid id, [FromBody] AssignGroupRequest request, UserRepository users,
        AccessGroupRepository groups, NpgsqlDataSource db,
        HttpContext http, CancellationToken ct)
    {
        var caller = CallerContextFactory.From(http);
        caller.Require("user.manage");

        if (await users.GetAsync(id, ct) is null)
        {
            return TypedResults.NotFound();
        }

        if (await UserAuthorityGuard.RefuseAsync(id, caller, "user.manage", users, ct) is { } refused)
        {
            return refused;
        }

        var target = await groups.GetAsync(request.GroupId, ct);
        if (target is null)
        {
            return TypedResults.Problem(
                title: "Unknown group", detail: "No such access group.",
                statusCode: StatusCodes.Status404NotFound);
        }

        // THE escalation chokepoint. A membership and a minted API key confer the same grant,
        // so both run one shared guard (GroupGrantGuard) — checking only the permission subset
        // was a real takeover path: a caller could build a group carrying their own role, never
        // scope it (an unscoped group reaches every department), and assign it to themselves;
        // the subset check passed because the permissions matched theirs. Only the scope checks
        // catch it.
        if (await GroupGrantGuard.CheckAsync(groups, caller, request.GroupId, "user.manage", ct)
            is { } denied)
        {
            return denied;
        }

        await using var work = await UnitOfWork.BeginAsync(db, ct);

        await users.AssignGroupAsync(id, request.GroupId, caller.UserId, request.ExpiresAt, work, ct);

        await work.AuditAsync(caller, "update", "user_group", id.ToString(),
            before: null,
            after: new { groupId = request.GroupId, group = target.Code, request.ExpiresAt },
            organizationUnitId: null, ct);
        await work.CommitAsync(ct);

        return TypedResults.NoContent();
    }

    private static async Task<Results<NoContent, NotFound, ProblemHttpResult>> RemoveFromGroupAsync(
        Guid id, Guid groupId, UserRepository users, AccessGroupRepository groups,
        RefreshTokenRepository refreshTokens, NpgsqlDataSource db, HttpContext http,
        CancellationToken ct)
    {
        var caller = CallerContextFactory.From(http);
        caller.Require("user.manage");

        if (await UserAuthorityGuard.RefuseAsync(id, caller, "user.manage", users, ct) is { } refused)
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

        // Immediate: a removed permission must not linger in an access token for up to its
        // ~15-minute life. Bump + revoke-all forces the user to re-authenticate; their next
        // token is issued without the removed group. (Only removal — see AddToGroupAsync.)
        await SessionRevocation.EndAllAsync(users, refreshTokens, id, work, ct);

        await work.AuditAsync(caller, "delete", "user_group", id.ToString(),
            before: new { groupId }, after: new { sessionsEnded = true },
            organizationUnitId: null, ct);
        await work.CommitAsync(ct);

        return TypedResults.NoContent();
    }
}
