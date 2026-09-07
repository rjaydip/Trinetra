using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Trinetra.Federation.Api.Auth;
using Trinetra.Federation.Api.Contracts;
using Trinetra.Federation.Api.Endpoints;
using Trinetra.Federation.Storage;
using Trinetra.Federation.Storage.Repositories;
using Trinetra.Federation.Storage.Security;

namespace Trinetra.IntegrationTests;

/// <summary>PR7b — the authentication audit trail (finding 4-H3).</summary>
public sealed class AuthAuditTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly PostgresFixture _fixture;

    private static readonly Guid Alice = Guid.Parse("da000000-0000-0000-0000-000000000001");

    private const string GoodPassword = "AuditTrail-Password-2026!";

    public AuthAuditTests(PostgresFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        var h = PasswordHasher.Hash(GoodPassword);
        await _fixture.ExecuteAsync($"""
            DELETE FROM federation.auth_audit;
            DELETE FROM federation.refresh_token WHERE user_id = '{Alice}';
            DELETE FROM federation.platform_users WHERE id = '{Alice}';

            INSERT INTO federation.platform_users
                (id, username, display_name, password_hash, password_salt,
                 password_iterations, password_algorithm)
            VALUES ('{Alice}', 'aa-alice', 'Alice', '\x{Convert.ToHexString(h.Hash)}',
                    '\x{Convert.ToHexString(h.Salt)}', {h.Iterations}, '{h.Algorithm}');
            """);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private AuthAuditRepository Repo => new(_fixture.DataSource, NullLogger<AuthAuditRepository>.Instance);
    private UserRepository Users => new(_fixture.DataSource);
    private RefreshTokenRepository Refresh => new(_fixture.DataSource);

    private static readonly IServiceProvider Services = new ServiceCollection()
        .AddLogging().AddProblemDetails().BuildServiceProvider();

    private static JwtTokenService NewJwt() => new(Options.Create(new AuthOptions
    {
        Jwt = new JwtOptions { SigningKey = Convert.ToBase64String(new byte[48]) },
    }));

    private static DefaultHttpContext Ctx() => new()
    {
        Connection = { RemoteIpAddress = IPAddress.Parse("203.0.113.7") },
        RequestServices = Services,
    };

    private Task<long> RowCount(string where) => _fixture.ScalarAsync<long>(
        $"SELECT count(*) FROM federation.auth_audit WHERE {where}");

    private Task<string> Scalar(string sql) => _fixture.ScalarAsync<string>(sql)!;

    // ---- repository ------------------------------------------------------

    [Fact]
    public async Task WriteBestEffort_InsertsRow_AndTruncates()
    {
        await Repo.WriteBestEffortAsync(new AuthAuditEntry
        {
            EventType = "test.event", Outcome = "failure",
            PresentedUsername = new string('x', 300),
            SourceAddress = "203.0.113.7", UserAgent = new string('u', 700),
            Detail = new { a = 1 },
        }, CancellationToken.None);

        (await RowCount("event_type = 'test.event'")).ShouldBe(1);
        (await Scalar("SELECT char_length(presented_username)::text FROM federation.auth_audit "
                    + "WHERE event_type = 'test.event'")).ShouldBe("256");
        (await Scalar("SELECT char_length(user_agent)::text FROM federation.auth_audit "
                    + "WHERE event_type = 'test.event'")).ShouldBe("512");
        (await Scalar("SELECT detail->>'a' FROM federation.auth_audit "
                    + "WHERE event_type = 'test.event'")).ShouldBe("1");
    }

    [Fact]
    public async Task WriteBestEffort_NeverThrows_OnBrokenDataSource()
    {
        var broken = Npgsql.NpgsqlDataSource.Create(
            "Host=127.0.0.1;Port=1;Database=nope;Username=nope;Timeout=1;Command Timeout=1");
        var repo = new AuthAuditRepository(broken, NullLogger<AuthAuditRepository>.Instance);

        await Should.NotThrowAsync(() => repo.WriteBestEffortAsync(new AuthAuditEntry
        {
            EventType = "x", Outcome = "failure",
        }, CancellationToken.None));
    }

    [Fact]
    public async Task WriteAsync_RollbackDropsRow_CommitKeepsIt()
    {
        await using (var work = await UnitOfWork.BeginAsync(_fixture.DataSource, CancellationToken.None))
        {
            await AuthAuditRepository.WriteAsync(work, new AuthAuditEntry
            {
                EventType = "rollback.me", Outcome = "success",
            }, CancellationToken.None);
            // no commit
        }
        (await RowCount("event_type = 'rollback.me'")).ShouldBe(0);

        await using (var work = await UnitOfWork.BeginAsync(_fixture.DataSource, CancellationToken.None))
        {
            await AuthAuditRepository.WriteAsync(work, new AuthAuditEntry
            {
                EventType = "commit.me", Outcome = "success",
            }, CancellationToken.None);
            await work.CommitAsync(CancellationToken.None);
        }
        (await RowCount("event_type = 'commit.me'")).ShouldBe(1);
    }

    [Fact]
    public async Task List_PagesNewestFirst_KeysetStableAcrossTies()
    {
        // 250 rows, all sharing one occurred_at, so the keyset must fall back on id.
        await _fixture.ExecuteAsync("""
            INSERT INTO federation.auth_audit (occurred_at, event_type, outcome)
            SELECT now(), 'bulk', 'success' FROM generate_series(1, 250);
            """);

        var seen = new HashSet<long>();
        DateTimeOffset? ct = null;
        long? ci = null;
        var pages = 0;
        while (true)
        {
            var rows = await Repo.ListAsync(new AuthAuditQuery(
                DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddMinutes(5),
                "bulk", null, null, ct, ci, 100), CancellationToken.None);
            if (rows.Count == 0)
            {
                break;
            }
            foreach (var r in rows)
            {
                seen.Add(r.Id).ShouldBeTrue("no row seen twice across the cursor boundary");
            }
            ct = rows[^1].OccurredAt;
            ci = rows[^1].Id;
            pages++;
            if (rows.Count < 100)
            {
                break;
            }
        }

        seen.Count.ShouldBe(250);
        pages.ShouldBe(3);
    }

    // ---- endpoint wiring -----------------------------------------------

    [Fact]
    public async Task Login_Success_WritesLoginSuccess_WithIpAndJti()
    {
        var result = await AuthEndpoints.LoginAsync(
            new LoginRequest("aa-alice", GoodPassword), Ctx(), Users, Refresh, Repo,
            _fixture.DataSource, NewJwt(), CancellationToken.None);

        result.Result.ShouldBeOfType<Microsoft.AspNetCore.Http.HttpResults.Ok<AuthTokenResponse>>();

        (await RowCount($"event_type = 'login.success' AND user_id = '{Alice}' "
                      + "AND source_address = '203.0.113.7' AND jti IS NOT NULL")).ShouldBe(1);
    }

    [Fact]
    public async Task Login_UnknownUser_WritesCoarseFailure_NoUserId()
    {
        await AuthEndpoints.LoginAsync(
            new LoginRequest("ghost-user", GoodPassword), Ctx(), Users, Refresh, Repo,
            _fixture.DataSource, NewJwt(), CancellationToken.None);

        (await RowCount("event_type = 'login.failure' AND user_id IS NULL "
                      + "AND presented_username = 'ghost-user'")).ShouldBe(1);
    }

    [Fact]
    public async Task Login_WrongPassword_RealUser_WritesFailure_WithUserId_NoUsername()
    {
        await AuthEndpoints.LoginAsync(
            new LoginRequest("aa-alice", "wrong"), Ctx(), Users, Refresh, Repo,
            _fixture.DataSource, NewJwt(), CancellationToken.None);

        (await RowCount($"event_type = 'login.failure' AND user_id = '{Alice}' "
                      + "AND presented_username IS NULL")).ShouldBe(1);
    }

    [Fact]
    public async Task Login_NthFailure_WritesLockoutRow_Once()
    {
        await _fixture.ExecuteAsync(
            $"UPDATE federation.platform_users SET failed_login_count = 9 WHERE id = '{Alice}'");

        await AuthEndpoints.LoginAsync(
            new LoginRequest("aa-alice", "wrong"), Ctx(), Users, Refresh, Repo,
            _fixture.DataSource, NewJwt(), CancellationToken.None);
        // A further attempt while locked: no new lockout row.
        await AuthEndpoints.LoginAsync(
            new LoginRequest("aa-alice", "wrong"), Ctx(), Users, Refresh, Repo,
            _fixture.DataSource, NewJwt(), CancellationToken.None);

        (await RowCount($"event_type = 'login.lockout' AND user_id = '{Alice}'")).ShouldBe(1);
    }

    [Fact]
    public async Task Logout_WritesLogoutRow()
    {
        var http = Ctx();
        http.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", Alice.ToString())], "t"));

        await AuthEndpoints.LogoutAsync(http, Users, Refresh, Repo, _fixture.DataSource,
            CancellationToken.None);

        (await RowCount($"event_type = 'session.logout' AND user_id = '{Alice}'")).ShouldBe(1);
    }

    // ---- schema: partitions + retention ------------------------------

    [Fact]
    public async Task EnsureAuditPartitions_FunctionBodyIncludesAuthAudit()
    {
        // Calling the function on the shared fixture is unsafe (config_audit_default already has
        // rows), so assert the array membership instead — the mechanics are covered by the drop
        // test below.
        var body = await _fixture.ScalarAsync<string>(
            "SELECT pg_get_functiondef('federation.ensure_audit_partitions'::regproc)");
        body!.ShouldContain("auth_audit");
    }

    [Fact]
    public async Task AuthAuditRead_IsSuperAdminOnly_NotBundledWithAuditRead()
    {
        // authaudit.read is the record of who got into the system — it must NOT ride along with
        // the general audit.read that scoped admins (STATE_ADMIN etc.) already hold.
        var superAdminHasIt = await _fixture.ScalarAsync<long>("""
            SELECT count(*) FROM federation.role_permissions rp
            JOIN federation.roles r ON r.id = rp.role_id
            WHERE r.code = 'SUPER_ADMIN' AND rp.permission_code = 'authaudit.read';
            """);
        superAdminHasIt.ShouldBe(1);

        var othersWithIt = await _fixture.ScalarAsync<long>("""
            SELECT count(*) FROM federation.role_permissions rp
            JOIN federation.roles r ON r.id = rp.role_id
            WHERE r.code <> 'SUPER_ADMIN' AND rp.permission_code = 'authaudit.read';
            """);
        othersWithIt.ShouldBe(0);

        // Sanity: a role that DOES hold the general audit.read (STATE_ADMIN) does not get this one.
        var stateAdminHasGeneral = await _fixture.ScalarAsync<long>("""
            SELECT count(*) FROM federation.role_permissions rp
            JOIN federation.roles r ON r.id = rp.role_id
            WHERE r.code = 'STATE_ADMIN' AND rp.permission_code = 'audit.read';
            """);
        stateAdminHasGeneral.ShouldBe(1);
    }

    [Fact]
    public async Task DropAuthAuditPartitions_RespectsTheMonthBoundary()
    {
        await _fixture.ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS federation.auth_audit_202401
                PARTITION OF federation.auth_audit
                FOR VALUES FROM ('2024-01-01') TO ('2024-02-01');
            """);

        // Mid-month cutoff: not dropped yet.
        (await _fixture.ScalarAsync<long>(
            "SELECT count(*) FROM federation.drop_auth_audit_partitions_before('2024-01-15')"))
            .ShouldBe(0);

        // The whole month is now behind the cutoff.
        (await _fixture.ScalarAsync<long>(
            "SELECT count(*) FROM federation.drop_auth_audit_partitions_before('2024-02-01')"))
            .ShouldBe(1);
    }
}
