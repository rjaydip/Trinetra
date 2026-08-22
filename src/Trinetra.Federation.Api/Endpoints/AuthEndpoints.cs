using Trinetra.Federation.Storage;
using Npgsql;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Trinetra.Federation.Api.Auth;
using Trinetra.Federation.Api.Contracts;
using Trinetra.Federation.Storage.Repositories;
using Trinetra.Federation.Storage.Security;

namespace Trinetra.Federation.Api.Endpoints;

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/auth").WithTags("Authentication");

        group.MapPost("/login", LoginAsync)
             .AllowAnonymous()
             // Rate limited: an unthrottled login endpoint on a reachable service is a
             // credential-stuffing target, and account lockout alone turns that into a
             // denial-of-service against every user an attacker can name.
             .RequireRateLimiting("login");

        group.MapPost("/password", ChangePasswordAsync).RequireAuthorization()
            .AllowAnyAuthenticated("a user must always be able to rotate their own password");
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
