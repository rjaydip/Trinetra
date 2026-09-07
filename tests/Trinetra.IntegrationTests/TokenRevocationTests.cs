using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using Trinetra.Federation.Api.Auth;
using Trinetra.Federation.Api.Contracts;
using Trinetra.Federation.Api.Endpoints;
using Trinetra.Federation.Storage;
using Trinetra.Federation.Storage.Repositories;

namespace Trinetra.IntegrationTests;

/// <summary>
/// PR4: token_version revocation + refresh tokens. Covers the repository mechanics
/// (<see cref="RefreshTokenRepository"/>, <see cref="UserRepository.BumpTokenVersionAsync"/>),
/// the bump triggers, and the <c>/auth/refresh</c> / <c>/auth/logout</c> / <c>/auth/password</c>
/// handler branches — all against a real Postgres via <see cref="PostgresFixture"/>.
/// </summary>
/// <remarks>
/// Not covered here, flagged as an accepted gap (same posture as PR3's 9-H2): the end-to-end
/// assertion that a stale-<c>token_version</c> JWT is rejected by the middleware with a generic
/// 401 — <c>OnTokenValidated</c> only fires through the real ASP.NET Core JwtBearer pipeline,
/// which the suite has deliberately not taken on a <c>WebApplicationFactory</c> to exercise. The
/// pieces it depends on (<see cref="UserRepository.GetTokenVersionAsync"/> and every bump
/// trigger) are covered below.
/// </remarks>
public sealed class TokenRevocationTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly PostgresFixture _fixture;

    private static readonly Guid Alice = Guid.Parse("d5555555-5555-5555-5555-555555555555");
    private static readonly Guid Bob   = Guid.Parse("d6666666-6666-6666-6666-666666666666");

    public TokenRevocationTests(PostgresFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        await _fixture.ExecuteAsync($"""
            DELETE FROM federation.refresh_token;
            DELETE FROM federation.platform_users WHERE id IN ('{Alice}', '{Bob}');

            INSERT INTO federation.platform_users
                (id, username, display_name, password_hash, password_salt, password_iterations)
            VALUES
                ('{Alice}', 'alice-tr', 'Alice', '\x00', '\x00', 600000),
                ('{Bob}',   'bob-tr',   'Bob',   '\x00', '\x00', 600000);
            """);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private RefreshTokenRepository RefreshRepo => new(_fixture.DataSource);
    private UserRepository Users => new(_fixture.DataSource);

    private static JwtTokenService NewJwt() => new(Options.Create(new AuthOptions
    {
        Jwt = new JwtOptions
        {
            SigningKey = Convert.ToBase64String(new byte[48]),
            AccessLifetime = TimeSpan.FromMinutes(15),
            RefreshLifetime = TimeSpan.FromHours(8),
            RefreshReuseGrace = TimeSpan.FromSeconds(10),
        },
    }));

    private static readonly IServiceProvider Services = new ServiceCollection()
        .AddLogging()
        .AddProblemDetails()
        .BuildServiceProvider();

    private static DefaultHttpContext ContextFor(params Claim[] claims) => new()
    {
        User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")),
        RequestServices = Services,
    };

    /// <summary>Executes an <see cref="IResult"/> (or a <c>Results&lt;...&gt;</c> union) and returns the status code.</summary>
    private static async Task<int> StatusOf(IResult result) => (await ExecuteAsync(result)).Status;

    private static async Task<(int Status, string Body)> ExecuteAsync(IResult result)
    {
        var body = new MemoryStream();
        var ctx = new DefaultHttpContext
        {
            RequestServices = Services,
            Response = { Body = body },
        };
        await result.ExecuteAsync(ctx);
        return (ctx.Response.StatusCode, Encoding.UTF8.GetString(body.ToArray()));
    }

    private async Task<int> TokenVersionOf(Guid userId) =>
        await _fixture.ScalarAsync<int>(
            $"SELECT token_version FROM federation.platform_users WHERE id = '{userId}'");

    private async Task<long> LiveRefreshCount(Guid userId) =>
        await _fixture.ScalarAsync<long>(
            $"SELECT count(*) FROM federation.refresh_token "
            + $"WHERE user_id = '{userId}' AND revoked_at IS NULL AND used_at IS NULL");

    private static string Sha256Hex(string raw) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));

    // ---- RefreshTokenRepository mechanics --------------------------------

    [Fact]
    public async Task RefreshRepo_Issue_ThenFind_ReturnsLiveRow()
    {
        Guid id;
        await using (var work = await UnitOfWork.BeginAsync(_fixture.DataSource, CancellationToken.None))
        {
            id = await RefreshRepo.IssueAsync(
                Alice, "hash-1", DateTimeOffset.UtcNow.AddHours(8), work, CancellationToken.None);
            await work.CommitAsync(CancellationToken.None);
        }

        var row = await RefreshRepo.FindAsync("hash-1", TimeSpan.FromSeconds(10), CancellationToken.None);
        row.ShouldNotBeNull();
        row.Id.ShouldBe(id);
        row.UsedAt.ShouldBeNull();
        row.RevokedAt.ShouldBeNull();
    }

    [Fact]
    public async Task RefreshRepo_Find_UnknownHash_ReturnsNull() =>
        (await RefreshRepo.FindAsync("nope", TimeSpan.FromSeconds(10), CancellationToken.None)).ShouldBeNull();

    [Fact]
    public async Task RefreshRepo_RevokeAllForUser_OnlyLiveRows_OtherUsersUntouched()
    {
        await using (var work = await UnitOfWork.BeginAsync(_fixture.DataSource, CancellationToken.None))
        {
            await RefreshRepo.IssueAsync(Alice, "a1", DateTimeOffset.UtcNow.AddHours(8), work, CancellationToken.None);
            await RefreshRepo.IssueAsync(Alice, "a2", DateTimeOffset.UtcNow.AddHours(8), work, CancellationToken.None);
            await RefreshRepo.IssueAsync(Bob, "b1", DateTimeOffset.UtcNow.AddHours(8), work, CancellationToken.None);
            await work.CommitAsync(CancellationToken.None);
        }

        await using (var work = await UnitOfWork.BeginAsync(_fixture.DataSource, CancellationToken.None))
        {
            await RefreshRepo.RevokeAllForUserAsync(Alice, work, CancellationToken.None);
            await work.CommitAsync(CancellationToken.None);
        }

        (await LiveRefreshCount(Alice)).ShouldBe(0);
        (await LiveRefreshCount(Bob)).ShouldBe(1);
    }

    // ---- token_version bump / read -------------------------------------

    [Fact]
    public async Task BumpTokenVersion_Increments()
    {
        var before = await TokenVersionOf(Alice);
        await using var work = await UnitOfWork.BeginAsync(_fixture.DataSource, CancellationToken.None);
        await Users.BumpTokenVersionAsync(Alice, work, CancellationToken.None);
        await work.CommitAsync(CancellationToken.None);

        (await TokenVersionOf(Alice)).ShouldBe(before + 1);
    }

    [Fact]
    public async Task GetTokenVersion_ActiveReturnsValue_InactiveOrUnknownReturnsNull()
    {
        (await Users.GetTokenVersionAsync(Alice, CancellationToken.None)).ShouldBe(0);

        await _fixture.ExecuteAsync(
            $"UPDATE federation.platform_users SET status = 'INACTIVE' WHERE id = '{Alice}'");
        (await Users.GetTokenVersionAsync(Alice, CancellationToken.None)).ShouldBeNull();

        (await Users.GetTokenVersionAsync(Guid.NewGuid(), CancellationToken.None)).ShouldBeNull();
    }

    // ---- /auth/refresh handler branches --------------------------------

    private async Task<string> SeedRefreshToken(Guid userId, DateTimeOffset expiresAt)
    {
        var raw = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        await _fixture.ExecuteAsync($"""
            INSERT INTO federation.refresh_token (user_id, token_hash, expires_at)
            VALUES ('{userId}', '{Sha256Hex(raw)}', '{expiresAt:O}');
            """);
        return raw;
    }

    private AuthAuditRepository Audit => new(
        _fixture.DataSource,
        Microsoft.Extensions.Logging.Abstractions.NullLogger<AuthAuditRepository>.Instance);

    private async Task<int> CallRefresh(string token) => await StatusOf(
        await AuthEndpoints.RefreshAsync(
            new RefreshRequest(token), ContextFor(), Users, RefreshRepo, Audit,
            _fixture.DataSource, NewJwt(), CancellationToken.None));

    [Fact]
    public async Task Refresh_HappyPath_RotatesAndSlidesWindow_ThenReplayOutsideGraceNukes()
    {
        var raw = await SeedRefreshToken(Alice, DateTimeOffset.UtcNow.AddHours(2));

        (await CallRefresh(raw)).ShouldBe(200);

        // The presented token is now rotated: marked used, pointing at a live successor.
        var (usedSet, hasSuccessor) = await _fixture.ScalarAsync<string>(
            $"SELECT (used_at IS NOT NULL)::text || ':' || (replaced_by IS NOT NULL)::text "
            + $"FROM federation.refresh_token WHERE token_hash = '{Sha256Hex(raw)}'") switch
        { var s => (s!.Split(':')[0], s.Split(':')[1]) };
        usedSet.ShouldBe("true");
        hasSuccessor.ShouldBe("true");

        // The successor is live with a fresh (slid-forward) 8h window.
        (await LiveRefreshCount(Alice)).ShouldBe(1, "the successor is the only live token");

        // Age the presented token past the grace window and re-present it → theft response.
        await _fixture.ExecuteAsync(
            $"UPDATE federation.refresh_token SET used_at = now() - interval '1 minute' "
            + $"WHERE token_hash = '{Sha256Hex(raw)}'");

        (await CallRefresh(raw)).ShouldBe(401);
        (await LiveRefreshCount(Alice)).ShouldBe(0, "replay outside grace revokes every session");
        (await TokenVersionOf(Alice)).ShouldBe(1, "replay also bumps token_version");
    }

    [Fact]
    public async Task Refresh_ExpiredToken_401_NoNuke()
    {
        var raw = await SeedRefreshToken(Alice, DateTimeOffset.UtcNow.AddMinutes(-1));
        (await CallRefresh(raw)).ShouldBe(401);
        (await TokenVersionOf(Alice)).ShouldBe(0, "an expiry is not a theft signal");
    }

    [Fact]
    public async Task Refresh_RevokedToken_401()
    {
        var raw = await SeedRefreshToken(Alice, DateTimeOffset.UtcNow.AddHours(8));
        await _fixture.ExecuteAsync(
            $"UPDATE federation.refresh_token SET revoked_at = now() WHERE user_id = '{Alice}'");
        (await CallRefresh(raw)).ShouldBe(401);
    }

    [Fact]
    public async Task Refresh_ReplayWithinGrace_401_ButNoNuke()
    {
        var raw = await SeedRefreshToken(Alice, DateTimeOffset.UtcNow.AddHours(8));
        await _fixture.ExecuteAsync(
            $"UPDATE federation.refresh_token SET used_at = now() - interval '2 seconds' "
            + $"WHERE token_hash = '{Sha256Hex(raw)}'");

        (await CallRefresh(raw)).ShouldBe(401);
        (await TokenVersionOf(Alice)).ShouldBe(0, "a two-tab race must not nuke the session");
    }

    [Fact]
    public async Task Refresh_DeactivatedUser_401()
    {
        var raw = await SeedRefreshToken(Alice, DateTimeOffset.UtcNow.AddHours(8));
        await _fixture.ExecuteAsync(
            $"UPDATE federation.platform_users SET status = 'INACTIVE' WHERE id = '{Alice}'");
        (await CallRefresh(raw)).ShouldBe(401);
    }

    // ---- /auth/logout --------------------------------------------------

    [Fact]
    public async Task Logout_BumpsVersion_RevokesAllRefreshTokens_204()
    {
        await SeedRefreshToken(Alice, DateTimeOffset.UtcNow.AddHours(8));
        await SeedRefreshToken(Alice, DateTimeOffset.UtcNow.AddHours(8));

        var status = await StatusOf(await AuthEndpoints.LogoutAsync(
            ContextFor(new Claim("sub", Alice.ToString())),
            Users, RefreshRepo, Audit, _fixture.DataSource, CancellationToken.None));

        status.ShouldBe(204);
        (await TokenVersionOf(Alice)).ShouldBe(1);
        (await LiveRefreshCount(Alice)).ShouldBe(0);
    }

    [Fact]
    public async Task Logout_ApiKeyCaller_400()
    {
        var status = await StatusOf(await AuthEndpoints.LogoutAsync(
            ContextFor(new Claim("trinetra:apikey", "ak_x"), new Claim("sub", Guid.NewGuid().ToString())),
            Users, RefreshRepo, Audit, _fixture.DataSource, CancellationToken.None));

        status.ShouldBe(400);
    }

    // ---- /auth/password status re-check (self-reversal hole) -----------

    [Fact]
    public async Task ChangePassword_DeactivatedUser_401_LockoutNotCleared()
    {
        // A REAL current password, so PasswordHasher.Verify passes and only the new
        // status/lockout re-check can reject this — isolating the fix for the self-reversible-
        // deactivation hole.
        var pw = Trinetra.Federation.Storage.Security.PasswordHasher.Hash("CorrectHorse-2026!");
        await _fixture.ExecuteAsync($"""
            UPDATE federation.platform_users
            SET password_hash = '\x{Convert.ToHexString(pw.Hash)}',
                password_salt = '\x{Convert.ToHexString(pw.Salt)}',
                password_iterations = {pw.Iterations},
                password_algorithm = '{pw.Algorithm}',
                status = 'INACTIVE', locked_until = now() + interval '10 minutes',
                failed_login_count = 8
            WHERE id = '{Alice}';
            """);

        var status = await StatusOf(await AuthEndpoints.ChangePasswordAsync(
            new ChangePasswordRequest("CorrectHorse-2026!", "BrandNewValue-2026!"),
            ContextFor(new Claim("sub", Alice.ToString())),
            Users, RefreshRepo, Audit, _fixture.DataSource, NewJwt(), CancellationToken.None));

        status.ShouldBe(401);

        var lockedAndFailed = await _fixture.ScalarAsync<string>(
            $"SELECT (locked_until IS NOT NULL)::text || ':' || failed_login_count "
            + $"FROM federation.platform_users WHERE id = '{Alice}'");
        lockedAndFailed.ShouldBe("true:8",
            "SetPasswordAsync must not have run — it would clear lockout and failed_login_count");
    }

    [Fact]
    public async Task ChangePassword_ActiveUser_HappyPath_BumpsVersion_RevokesOldSessions_ReturnsNewPair()
    {
        var pw = Trinetra.Federation.Storage.Security.PasswordHasher.Hash("CorrectHorse-2026!");
        await _fixture.ExecuteAsync($"""
            UPDATE federation.platform_users
            SET password_hash = '\x{Convert.ToHexString(pw.Hash)}',
                password_salt = '\x{Convert.ToHexString(pw.Salt)}',
                password_iterations = {pw.Iterations},
                password_algorithm = '{pw.Algorithm}'
            WHERE id = '{Alice}';
            """);
        await SeedRefreshToken(Alice, DateTimeOffset.UtcNow.AddHours(8));

        var status = await StatusOf(await AuthEndpoints.ChangePasswordAsync(
            new ChangePasswordRequest("CorrectHorse-2026!", "BrandNewValue-2026!"),
            ContextFor(new Claim("sub", Alice.ToString())),
            Users, RefreshRepo, Audit, _fixture.DataSource, NewJwt(), CancellationToken.None));

        status.ShouldBe(200);
        (await TokenVersionOf(Alice)).ShouldBe(1);
        // The old refresh token is revoked; exactly one new one (from the response) is live.
        (await LiveRefreshCount(Alice)).ShouldBe(1);
    }

    [Fact]
    public async Task ChangePassword_HappyPath_NewAccessToken_CarriesPostBumpTokenVersion()
    {
        // dotnet-expert bug #1 regression: the access token minted in the same transaction as the
        // token_version bump must carry the *post-bump* value, not the stale one a fresh pooled
        // connection would still see — otherwise it 401s on its very first request.
        var pw = Trinetra.Federation.Storage.Security.PasswordHasher.Hash("CorrectHorse-2026!");
        await _fixture.ExecuteAsync($"""
            UPDATE federation.platform_users
            SET password_hash = '\x{Convert.ToHexString(pw.Hash)}',
                password_salt = '\x{Convert.ToHexString(pw.Salt)}',
                password_iterations = {pw.Iterations},
                password_algorithm = '{pw.Algorithm}'
            WHERE id = '{Alice}';
            """);

        var (status, body) = await ExecuteAsync(await AuthEndpoints.ChangePasswordAsync(
            new ChangePasswordRequest("CorrectHorse-2026!", "BrandNewValue-2026!"),
            ContextFor(new Claim("sub", Alice.ToString())),
            Users, RefreshRepo, Audit, _fixture.DataSource, NewJwt(), CancellationToken.None));

        status.ShouldBe(200);
        var access = System.Text.Json.JsonDocument.Parse(body).RootElement
            .GetProperty("accessToken").GetString()!;
        var claimed = new Microsoft.IdentityModel.JsonWebTokens.JsonWebTokenHandler()
            .ReadJsonWebToken(access).GetClaim(TrinetraClaims.TokenVersion).Value;

        claimed.ShouldBe((await TokenVersionOf(Alice)).ToString());
        claimed.ShouldBe("1");
    }
}
