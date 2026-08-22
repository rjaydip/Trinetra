using Trinetra.Federation.Storage;
using Npgsql;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Trinetra.Federation.Api.Auth;
using Trinetra.Federation.Api.Contracts;
using Trinetra.Federation.Api.OpenApi;
using Trinetra.Federation.Storage.Repositories;
using Trinetra.Federation.Storage.Security;

namespace Trinetra.Federation.Api.Endpoints;

/// <summary>Login and self-service password rotation — the caller's own credentials only.</summary>
public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/auth").WithTags(ApiTags.Authentication);

        group.MapPost("/login", LoginAsync)
             .AllowAnonymous()
             // Rate limited: an unthrottled login endpoint on a reachable service is a
             // credential-stuffing target, and account lockout alone turns that into a
             // denial-of-service against every user an attacker can name.
             .RequireRateLimiting("login")
             .WithSummary("Exchange a username and password for a bearer token")
             .WithDescription(
                 "The only anonymous route on the API. Returns a token carrying the caller's "
                 + "resolved permissions and scope, its expiry, and whether a password change is "
                 + "due — a token issued with `mustChangePassword` is valid, so a client should "
                 + "route the user to `POST /auth/password` before anything else.\n\n"
                 + "Unknown user, wrong password, inactive account and lockout all return the "
                 + "same 401: distinguishing them would confirm which usernames exist. Rate "
                 + "limited to 10 attempts per minute per source address.\n\n"
                 + "Machine callers authenticate with an API key header instead and never call "
                 + "this route.");

        group.MapPost("/password", ChangePasswordAsync).RequireAuthorization()
            .AllowAnyAuthenticated("a user must always be able to rotate their own password")
            .WithSummary("Change your own password")
            .WithDescription(
                "Rotates the calling user's own password — never anyone else's; an administrator "
                + "resetting another account uses `POST /users/{id}/password`. Requires the "
                + "current password, rejects a new value equal to the old one, and returns a "
                + "fresh token because the existing one still carries the must-change flag.\n\n"
                + "Deliberately reachable by any authenticated user: gating it on a permission "
                + "would let an account be locked out of rotating its own credential. API-key "
                + "callers get 400 — there is no password to change.");
    }

    private static async Task<Results<Ok<LoginResponse>, ProblemHttpResult>> LoginAsync(
        [FromBody] LoginRequest request,
        UserRepository users,
        NpgsqlDataSource db,
        JwtTokenService tokens,
        CancellationToken ct)
    {
        var user = await users.FindForLoginAsync(request.Username, ct);

        // One uniform failure for unknown user, wrong password, inactive account and lockout.
        // Distinguishing them tells an attacker which usernames are real.
        if (user is null
            || user.Status != "ACTIVE"
            || (user.LockedUntil is { } locked && locked > DateTimeOffset.UtcNow)
            || !PasswordHasher.Verify(request.Password, user.Password))
        {
            if (user is not null)
            {
                await users.RecordFailedLoginAsync(user.Id, ct);
            }

            return TypedResults.Problem(
                title: "Authentication failed",
                detail: "The username or password is incorrect, or the account is unavailable.",
                statusCode: StatusCodes.Status401Unauthorized);
        }

        // The cost can be raised over time; an old hash verifies at its own cost and is upgraded
        // here, on the one occasion the plaintext is legitimately in hand.
        if (PasswordHasher.NeedsRehash(user.Password))
        {
            await using var rehash = await UnitOfWork.BeginAsync(db, ct);
            await users.SetPasswordAsync(
                user.Id, PasswordHasher.Hash(request.Password), user.MustChangePassword,
                rehash, ct);
            await rehash.CommitAsync(ct);
        }

        await users.RecordSuccessfulLoginAsync(user.Id, ct);

        var permissions = await users.GetPermissionsAsync(user.Id, ct);
        var unscoped = await users.GetUnscopedPermissionsAsync(user.Id, ct);
        var unscopedGeo = await users.GetUnscopedGeographyAsync(user.Id, ct);

        var (token, expires) = tokens.Issue(
            user.Id, user.Username, permissions, unscoped, unscopedGeo, user.MustChangePassword);

        return TypedResults.Ok(new LoginResponse(token, expires, user.MustChangePassword));
    }

    private static async Task<Results<Ok<LoginResponse>, ProblemHttpResult>> ChangePasswordAsync(
        [FromBody] ChangePasswordRequest request,
        HttpContext http,
        UserRepository users,
        NpgsqlDataSource db,
        JwtTokenService tokens,
        CancellationToken ct)
    {
        var caller = CallerContextFactory.From(http);

        if (caller.UserId is not { } userId)
        {
            // An API key has no password to rotate.
            return TypedResults.Problem(
                title: "Not applicable",
                detail: "Only interactive users can change a password.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var user = await users.FindForLoginByIdAsync(userId, ct);

        if (user is null || !PasswordHasher.Verify(request.CurrentPassword, user.Password))
        {
            return TypedResults.Problem(
                title: "Authentication failed",
                detail: "The current password is incorrect.",
                statusCode: StatusCodes.Status401Unauthorized);
        }

        if (!PasswordHasher.IsAcceptable(request.NewPassword, out var reason))
        {
            return TypedResults.Problem(
                title: "Password rejected", detail: reason,
                statusCode: StatusCodes.Status400BadRequest);
        }

        // Blocks re-setting the same value, which would satisfy a forced rotation without
        // actually rotating anything.
        if (PasswordHasher.Verify(request.NewPassword, user.Password))
        {
            return TypedResults.Problem(
                title: "Password rejected",
                detail: "The new password must differ from the current one.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        await using var work = await UnitOfWork.BeginAsync(db, ct);

        await users.SetPasswordAsync(
            userId, PasswordHasher.Hash(request.NewPassword), mustChange: false, work, ct);

        // A password change is a security event, and until now it left no audit row at all.
        await work.AuditAsync(caller, "update", "user_password", userId.ToString(),
            before: null, after: new { changed = true }, organizationUnitId: null, ct);

        await work.CommitAsync(ct);

        // A fresh token, because the old one still carries must-change-password.
        var permissions = await users.GetPermissionsAsync(userId, ct);
        var unscoped = await users.GetUnscopedPermissionsAsync(userId, ct);
        var unscopedGeo = await users.GetUnscopedGeographyAsync(userId, ct);
        var (token, expires) = tokens.Issue(
            userId, user.Username, permissions, unscoped, unscopedGeo,
            mustChangePassword: false);

        return TypedResults.Ok(new LoginResponse(token, expires, MustChangePassword: false));
    }
}
