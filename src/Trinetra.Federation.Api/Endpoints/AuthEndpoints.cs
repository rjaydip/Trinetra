using System.Security.Cryptography;
using System.Text;
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

/// <summary>
/// Login, token refresh, logout, and self-service password rotation — the caller's own
/// credentials only.
/// </summary>
public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this IEndpointRouteBuilder app, bool enableDevPasswordFlow)
    {
        var group = app.MapGroup("/api/v1/auth").WithTags(ApiTags.Authentication);

        if (enableDevPasswordFlow)
        {
            // OAuth2 "password" grant, development only. Its sole purpose is to let Scalar's
            // Authorize dialog take a username and password, fetch a token itself, and apply it
            // to every request — no copy-paste. Access-token-only (Scalar has no refresh
            // machinery — it re-POSTs credentials when the token expires). Not mapped outside
            // Development; kept out of the OpenAPI document because it is UI plumbing.
            group.MapPost("/token", TokenAsync)
                 .AllowAnonymous()
                 .RequireRateLimiting("login")
                 .DisableAntiforgery()
                 .ExcludeFromDescription();
        }

        group.MapPost("/login", LoginAsync)
             .AllowAnonymous()
             // Rate limited: an unthrottled login endpoint on a reachable service is a
             // credential-stuffing target, and account lockout alone turns that into a
             // denial-of-service against every user an attacker can name.
             .RequireRateLimiting("login")
             .WithSummary("Exchange a username and password for an access + refresh token pair")
             .WithDescription(
                 "The only anonymous route on the API that takes credentials. Returns a short "
                 + "access token (~15 minutes, sent as `Authorization: Bearer`) carrying the "
                 + "caller's resolved permissions and scope, and an opaque refresh token — store "
                 + "it and present it to `POST /auth/refresh` before the access token expires to "
                 + "get a new pair without re-entering credentials. `mustChangePassword: true` "
                 + "means the client must route the user to `POST /auth/password` first.\n\n"
                 + "Unknown user, wrong password, inactive account and lockout all return the "
                 + "same 401: distinguishing them would confirm which usernames exist. Rate "
                 + "limited to 10 attempts per minute per source address.\n\n"
                 + "Machine callers authenticate with an API key header instead and never call "
                 + "this route.");

        group.MapPost("/refresh", RefreshAsync)
             .AllowAnonymous()
             .RequireRateLimiting("login")
             .WithSummary("Exchange a refresh token for a new token pair")
             .WithDescription(
                 "Rotates the session: returns a new ~15-minute access token and a **new** "
                 + "refresh token, and invalidates the one presented. The 8-hour refresh window "
                 + "slides forward on each call; 8 hours of inactivity ends the session and the "
                 + "user must log in again.\n\n"
                 + "Permissions and scope are **re-resolved from the database** on every call, so "
                 + "a group added or removed since login takes effect here (a removed group "
                 + "still lingers in the old access token for up to its ~15-minute life).\n\n"
                 + "An unknown, expired, revoked, or already-rotated token returns the same 401 "
                 + "as a bad password. Presenting an already-rotated token outside a short grace "
                 + "window is treated as theft: every refresh token for that user is revoked, "
                 + "every access token is invalidated, and re-login is required. API-key callers "
                 + "get 400.");

        group.MapPost("/logout", LogoutAsync).RequireAuthorization()
             .AllowAnyAuthenticated("a user must always be able to end their own session")
             .WithSummary("End your session on every device")
             .WithDescription(
                 "Revokes all of the calling user's refresh tokens and invalidates every access "
                 + "token already issued to them — not just the one on this request. There is no "
                 + "per-device logout; the token-pair model keeps no per-session identity to "
                 + "target. Idempotent. API-key callers get 400 — a key has no session, and is "
                 + "revoked through `DELETE /api/v1/api-keys/{id}` instead.");

        group.MapPost("/password", ChangePasswordAsync).RequireAuthorization()
            .AllowAnyAuthenticated("a user must always be able to rotate their own password")
            .WithSummary("Change your own password")
            .WithDescription(
                "Rotates the calling user's own password — never anyone else's; an administrator "
                + "resetting another account uses `POST /users/{id}/password`. Requires the "
                + "current password and rejects a new value equal to the old one.\n\n"
                + "Re-checks that the account is active and not locked **before** verifying the "
                + "current password — a deactivated account cannot use this route to clear its "
                + "own lockout. On success it **ends every other session you have**: all your "
                + "other access tokens and every refresh token are invalidated, and the response "
                + "is a fresh access + refresh token pair (the same shape as `POST /auth/login`). "
                + "API-key callers get 400 — there is no password to change.");
    }

    private static async Task<Results<Ok<AuthTokenResponse>, ProblemHttpResult>> LoginAsync(
        [FromBody] LoginRequest request,
        UserRepository users,
        RefreshTokenRepository refreshTokens,
        NpgsqlDataSource db,
        JwtTokenService tokens,
        CancellationToken ct)
    {
        await using var work = await UnitOfWork.BeginAsync(db, ct);

        var auth = await TryAuthenticateAsync(
            request.Username, request.Password, persistRefresh: true,
            users, refreshTokens, tokens, work, ct);

        if (auth is not { } result)
        {
            return TypedResults.Problem(
                title: "Authentication failed",
                detail: "The username or password is incorrect, or the account is unavailable.",
                statusCode: StatusCodes.Status401Unauthorized);
        }

        // Login now opens a transaction (for the refresh-token row); per invariant #10 a
        // mutation shares its transaction with an audit row. `last_login_at` records the fact
        // too, but that is best-effort and outside the transaction.
        await work.AuditAsync(
            CallerContext.System("auth:login") with { UserId = result.UserId },
            "login", "session", result.UserId.ToString(),
            before: null, after: new { }, organizationUnitId: null, ct);

        await work.CommitAsync(ct);

        return TypedResults.Ok(ToResponse(result));
    }

    /// <summary>
    /// OAuth2 "password" grant — development only, and deliberately absent from the OpenAPI
    /// document. Access token only; Scalar has no refresh machinery.
    /// </summary>
    private static async Task<Results<Ok<OAuthTokenResponse>, ProblemHttpResult>> TokenAsync(
        [FromForm] string username,
        [FromForm] string password,
        UserRepository users,
        RefreshTokenRepository refreshTokens,
        NpgsqlDataSource db,
        JwtTokenService tokens,
        CancellationToken ct)
    {
        await using var work = await UnitOfWork.BeginAsync(db, ct);

        var auth = await TryAuthenticateAsync(
            username, password, persistRefresh: false, users, refreshTokens, tokens, work, ct);

        if (auth is not { } result)
        {
            // RFC 6749 error code, so Scalar surfaces a real message instead of a blank failure.
            return TypedResults.Problem(
                title: "invalid_grant",
                detail: "The username or password is incorrect, or the account is unavailable.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        // Commits the rehash, if one happened; otherwise a no-op.
        await work.CommitAsync(ct);

        return TypedResults.Ok(
            new OAuthTokenResponse(result.AccessToken, "Bearer", result.AccessExpiresIn));
    }

    internal static async Task<Results<Ok<AuthTokenResponse>, ProblemHttpResult>> RefreshAsync(
        [FromBody] RefreshRequest request,
        UserRepository users,
        RefreshTokenRepository refreshTokens,
        NpgsqlDataSource db,
        JwtTokenService tokens,
        CancellationToken ct)
    {
        static ProblemHttpResult Reject() => (ProblemHttpResult)TypedResults.Problem(
            title: "Authentication failed",
            detail: "The refresh token is invalid, expired, or has already been used.",
            statusCode: StatusCodes.Status401Unauthorized);

        if (string.IsNullOrWhiteSpace(request.RefreshToken))
        {
            return Reject();
        }

        var presentedHash = HashToken(request.RefreshToken);
        var row = await refreshTokens.FindAsync(presentedHash, tokens.RefreshReuseGrace, ct);

        if (row is null || row.RevokedAt is not null || row.IsExpired)
        {
            return Reject();
        }

        // Already rotated. Inside the grace window this is a benign two-tab race — the sibling
        // request already holds the new pair, so reject softly. Outside it, a well-behaved
        // client would never re-present a rotated token: treat it as theft. (Both the expiry
        // and the grace judgement above are computed on the DB clock — see FindAsync.)
        if (row.UsedAt is not null)
        {
            if (row.IsReplayPastGrace)
            {
                await using var nuke = await UnitOfWork.BeginAsync(db, ct);
                await SessionRevocation.EndAllAsync(users, refreshTokens, row.UserId, nuke, ct);
                await nuke.AuditAsync(
                    CallerContext.System("auth:refresh-replay") with { UserId = row.UserId },
                    "revoke", "refresh_token", row.Id.ToString(),
                    before: null,
                    after: new { reason = "refresh token replay detected", allSessionsRevoked = true },
                    organizationUnitId: null, ct);
                await nuke.CommitAsync(ct);
            }

            return Reject();
        }

        var user = await users.FindForLoginByIdAsync(row.UserId, ct);
        if (user is null
            || user.Status != "ACTIVE"
            || (user.LockedUntil is { } locked && locked > DateTimeOffset.UtcNow))
        {
            return Reject();
        }

        await using var work = await UnitOfWork.BeginAsync(db, ct);

        var result = await IssuePairAsync(
            user.Id, user.Username, user.MustChangePassword, persistRefresh: true,
            users, refreshTokens, tokens, work, ct);

        await refreshTokens.MarkRotatedAsync(row.Id, result.RefreshTokenId!.Value, work, ct);
        await work.CommitAsync(ct);

        return TypedResults.Ok(ToResponse(result));
    }

    internal static async Task<Results<NoContent, ProblemHttpResult>> LogoutAsync(
        HttpContext http,
        UserRepository users,
        RefreshTokenRepository refreshTokens,
        NpgsqlDataSource db,
        CancellationToken ct)
    {
        var caller = CallerContextFactory.From(http);

        if (caller.UserId is not { } userId)
        {
            return TypedResults.Problem(
                title: "Not applicable",
                detail: "API keys have no session to end; revoke the key instead.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        await using var work = await UnitOfWork.BeginAsync(db, ct);

        await SessionRevocation.EndAllAsync(users, refreshTokens, userId, work, ct);
        await work.AuditAsync(caller, "logout", "session", userId.ToString(),
            before: null, after: new { allSessionsEnded = true }, organizationUnitId: null, ct);

        await work.CommitAsync(ct);

        return TypedResults.NoContent();
    }

    internal static async Task<Results<Ok<AuthTokenResponse>, ProblemHttpResult>> ChangePasswordAsync(
        [FromBody] ChangePasswordRequest request,
        HttpContext http,
        UserRepository users,
        RefreshTokenRepository refreshTokens,
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

        // Status/lockout re-check FIRST, folded into the same generic 401 as a wrong password: a
        // token can outlive a deactivation by up to one access lifetime, and without this a
        // just-deactivated user could call this route, get a fresh pair, and — because
        // SetPasswordAsync clears locked_until — undo their own lockout.
        if (user is null
            || user.Status != "ACTIVE"
            || (user.LockedUntil is { } locked && locked > DateTimeOffset.UtcNow)
            || !PasswordHasher.Verify(request.CurrentPassword, user.Password))
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

        // End every other session BEFORE issuing the new pair, so the new refresh row is not
        // caught by the revoke — and mint the new access token against the RETURNING'd
        // post-bump version, not a stale value re-read on a different connection.
        var newVersion = await SessionRevocation.EndAllAsync(
            users, refreshTokens, userId, work, ct);

        await work.AuditAsync(caller, "update", "user_password", userId.ToString(),
            before: null, after: new { changed = true, otherSessionsEnded = true },
            organizationUnitId: null, ct);

        var result = await IssuePairAsync(
            userId, user.Username, mustChangePassword: false, persistRefresh: true,
            users, refreshTokens, tokens, work, ct, knownTokenVersion: newVersion);

        await work.CommitAsync(ct);

        return TypedResults.Ok(ToResponse(result));
    }

    // -------------------------------------------------------------------
    // Shared credential + token-issue path
    // -------------------------------------------------------------------

    private readonly record struct AuthResult(
        Guid UserId,
        string AccessToken, int AccessExpiresIn,
        string RefreshToken, int RefreshExpiresIn,
        bool MustChangePassword,
        Guid? RefreshTokenId);

    private static AuthTokenResponse ToResponse(AuthResult r) => new(
        r.AccessToken, r.AccessExpiresIn, r.RefreshToken, r.RefreshExpiresIn, r.MustChangePassword);

    /// <summary>
    /// The shared credential path for <see cref="LoginAsync"/> and <see cref="TokenAsync"/>.
    /// Returns <see langword="null"/> for unknown user, wrong password, inactive account and
    /// lockout alike — the callers must not distinguish them, or they leak which usernames exist.
    /// The rehash, if one is needed, rides <paramref name="work"/>; the caller commits.
    /// </summary>
    private static async Task<AuthResult?> TryAuthenticateAsync(
        string username, string password, bool persistRefresh,
        UserRepository users, RefreshTokenRepository refreshTokens, JwtTokenService tokens,
        UnitOfWork work, CancellationToken ct)
    {
        var user = await users.FindForLoginAsync(username, ct);

        if (user is null
            || user.Status != "ACTIVE"
            || (user.LockedUntil is { } locked && locked > DateTimeOffset.UtcNow)
            || !PasswordHasher.Verify(password, user.Password))
        {
            if (user is not null)
            {
                await users.RecordFailedLoginAsync(user.Id, ct);
            }

            return null;
        }

        // The cost can be raised over time; an old hash verifies at its own cost and is upgraded
        // here, on the one occasion the plaintext is legitimately in hand.
        if (PasswordHasher.NeedsRehash(user.Password))
        {
            await users.SetPasswordAsync(
                user.Id, PasswordHasher.Hash(password), user.MustChangePassword, work, ct);
        }

        await users.RecordSuccessfulLoginAsync(user.Id, ct);

        return await IssuePairAsync(
            user.Id, user.Username, user.MustChangePassword, persistRefresh,
            users, refreshTokens, tokens, work, ct);
    }

    /// <summary>
    /// Resolves the user's current permissions and scope, mints an access + refresh token pair,
    /// and (unless <c>persistRefresh</c> is false, for the dev OAuth flow) writes the
    /// refresh-token row inside <c>work</c>. The caller commits.
    /// </summary>
    /// <remarks>
    /// Pass <c>knownTokenVersion</c> — the post-bump <c>token_version</c> — when the caller has
    /// just incremented it in <c>work</c> (which <see cref="UserRepository.GetTokenVersionAsync"/>,
    /// on its own connection, could not yet see). Omit it and the current value is read.
    /// </remarks>
    private static async Task<AuthResult> IssuePairAsync(
        Guid userId, string username, bool mustChangePassword, bool persistRefresh,
        UserRepository users, RefreshTokenRepository refreshTokens, JwtTokenService tokens,
        UnitOfWork work, CancellationToken ct, int? knownTokenVersion = null)
    {
        var permissions = await users.GetPermissionsAsync(userId, ct);
        var unscoped = await users.GetUnscopedPermissionsAsync(userId, ct);
        var unscopedGeo = await users.GetUnscopedGeographyAsync(userId, ct);

        var tokenVersion = knownTokenVersion
            ?? await users.GetTokenVersionAsync(userId, ct)
            ?? throw new InvalidOperationException("User became inactive during token issue.");

        var (access, accessExpires) = tokens.Issue(
            userId, username, permissions, unscoped, unscopedGeo, tokenVersion, mustChangePassword);

        var rawRefresh = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var refreshExpires = DateTimeOffset.UtcNow.Add(tokens.RefreshLifetime);

        Guid? refreshId = null;
        if (persistRefresh)
        {
            refreshId = await refreshTokens.IssueAsync(
                userId, HashToken(rawRefresh), refreshExpires, work, ct);
        }

        return new AuthResult(
            userId,
            access,
            (int)Math.Max(0, (accessExpires - DateTimeOffset.UtcNow).TotalSeconds),
            rawRefresh,
            (int)Math.Max(0, (refreshExpires - DateTimeOffset.UtcNow).TotalSeconds),
            mustChangePassword,
            refreshId);
    }

    private static string HashToken(string raw) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
}
