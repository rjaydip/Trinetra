using System.Diagnostics;
using Microsoft.Extensions.Options;
using Shouldly;
using Trinetra.Federation.Api.Auth;
using Trinetra.Federation.Api.Contracts;
using Trinetra.Federation.Api.Endpoints;
using Trinetra.Federation.Storage.Repositories;
using Trinetra.Federation.Storage.Security;

namespace Trinetra.IntegrationTests;

/// <summary>
/// PR7a — login hardening. Covers 4-H4 (the login path must spend a full PBKDF2 grind even for a
/// missing / inactive / locked account, so response time does not reveal which usernames exist)
/// and 4-L3 (a failed attempt against an already-locked account must not extend the lockout).
/// </summary>
public sealed class LoginHardeningTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly PostgresFixture _fixture;

    private static readonly Guid Live   = Guid.Parse("d8888888-8888-8888-8888-888888888881");
    private static readonly Guid Locked = Guid.Parse("d8888888-8888-8888-8888-888888888882");

    private const string GoodPassword = "correct horse battery staple";

    private PasswordHash _hash;

    public LoginHardeningTests(PostgresFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        var h = PasswordHasher.Hash(GoodPassword);
        _hash = h;

        await _fixture.ExecuteAsync($"""
            DELETE FROM federation.refresh_token WHERE user_id IN ('{Live}', '{Locked}');
            DELETE FROM federation.platform_users WHERE id IN ('{Live}', '{Locked}');

            INSERT INTO federation.platform_users
                (id, username, display_name, password_hash, password_salt, password_iterations,
                 password_algorithm, status, failed_login_count, locked_until)
            VALUES
                ('{Live}', 'lh-live', 'Live', '\x{Convert.ToHexString(h.Hash)}',
                 '\x{Convert.ToHexString(h.Salt)}', {h.Iterations}, '{h.Algorithm}',
                 'ACTIVE', 0, NULL),
                ('{Locked}', 'lh-locked', 'Locked', '\x{Convert.ToHexString(h.Hash)}',
                 '\x{Convert.ToHexString(h.Salt)}', {h.Iterations}, '{h.Algorithm}',
                 'ACTIVE', 7, now() + interval '10 minutes');
            """);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private UserRepository Users => new(_fixture.DataSource);
    private RefreshTokenRepository Refresh => new(_fixture.DataSource);

    private static JwtTokenService Jwt() => new(Options.Create(new AuthOptions
    {
        Jwt = new JwtOptions { SigningKey = Convert.ToBase64String(new byte[48]) },
    }));

    private AuthAuditRepository Audit => new(
        _fixture.DataSource,
        Microsoft.Extensions.Logging.Abstractions.NullLogger<AuthAuditRepository>.Instance);

    private async Task<(long elapsedMs, bool ok)> TimeLogin(string username, string password)
    {
        var sw = Stopwatch.StartNew();
        var result = await AuthEndpoints.LoginAsync(
            new LoginRequest(username, password), new Microsoft.AspNetCore.Http.DefaultHttpContext(),
            Users, Refresh, Audit, _fixture.DataSource, Jwt(), CancellationToken.None);
        sw.Stop();
        return (sw.ElapsedMilliseconds,
            result.Result is Microsoft.AspNetCore.Http.HttpResults.Ok<AuthTokenResponse>);
    }

    /// <summary>
    /// Cost of a single PBKDF2 verify on this machine, measured now. The login-hardening
    /// assertions are relative to this rather than a hardcoded millisecond floor — a fast CI
    /// runner would flake an absolute threshold.
    /// </summary>
    private long OneVerifyMs()
    {
        PasswordHasher.Verify("warm up the jit", _hash); // discard first, JIT + cache
        var sw = Stopwatch.StartNew();
        PasswordHasher.Verify("still wrong", _hash);
        sw.Stop();
        return sw.ElapsedMilliseconds;
    }

    // ---- 4-H4: no fast path that betrays which usernames exist -------------

    [Fact]
    public async Task Login_UnknownUser_SpendsAFullHash()
    {
        // The regression: FindForLoginAsync returns null, and pre-fix the || chain skipped the
        // verify entirely — returning in ~1ms and marking the username as "not real". Assert the
        // unknown-user path costs at least half of one real PBKDF2 grind; a skipped verify is
        // ~2 orders of magnitude under that.
        var floor = OneVerifyMs() / 2;

        var (ms, ok) = await TimeLogin("no-such-user-at-all", GoodPassword);
        ok.ShouldBeFalse();
        ms.ShouldBeGreaterThan(floor);
    }

    [Fact]
    public async Task Login_LockedUser_SpendsAFullHash()
    {
        var floor = OneVerifyMs() / 2;

        var (ms, ok) = await TimeLogin("lh-locked", GoodPassword);
        ok.ShouldBeFalse();
        ms.ShouldBeGreaterThan(floor);
    }

    [Fact]
    public async Task Login_KnownUserWrongPassword_SpendsAFullHash()
    {
        var floor = OneVerifyMs() / 2;

        var (ms, ok) = await TimeLogin("lh-live", "wrong-password");
        ok.ShouldBeFalse();
        ms.ShouldBeGreaterThan(floor);
    }

    [Fact]
    public async Task Login_LiveUserCorrectPassword_StillSucceeds()
    {
        var (_, ok) = await TimeLogin("lh-live", GoodPassword);
        ok.ShouldBeTrue();
    }

    // ---- 4-L3: a locked account's lockout window is frozen ----------------

    [Fact]
    public async Task RecordFailedLogin_WhileLocked_DoesNotExtendTheLockout()
    {
        // locked_until read as text — PostgresFixture.ScalarAsync<DateTimeOffset> has a
        // pre-existing DateTime→DateTimeOffset cast bug, unrelated to this PR.
        var before = await _fixture.ScalarAsync<string>(
            $"SELECT locked_until::text FROM federation.platform_users WHERE id = '{Locked}'");
        var countBefore = await _fixture.ScalarAsync<int>(
            $"SELECT failed_login_count FROM federation.platform_users WHERE id = '{Locked}'");

        await Users.RecordFailedLoginAsync(Locked, CancellationToken.None);
        await Users.RecordFailedLoginAsync(Locked, CancellationToken.None);

        var after = await _fixture.ScalarAsync<string>(
            $"SELECT locked_until::text FROM federation.platform_users WHERE id = '{Locked}'");
        var countAfter = await _fixture.ScalarAsync<int>(
            $"SELECT failed_login_count FROM federation.platform_users WHERE id = '{Locked}'");

        after.ShouldBe(before);
        countAfter.ShouldBe(countBefore);
    }

    [Fact]
    public async Task RecordFailedLogin_WhileUnlocked_IncrementsTheCounter()
    {
        await Users.RecordFailedLoginAsync(Live, CancellationToken.None);

        var count = await _fixture.ScalarAsync<int>(
            $"SELECT failed_login_count FROM federation.platform_users WHERE id = '{Live}'");
        count.ShouldBe(1);
    }
}
