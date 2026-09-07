using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using Trinetra.Federation.Api.Auth;
using Trinetra.Federation.Api.Contracts;
using Trinetra.Federation.Api.Endpoints;
using Trinetra.Federation.Storage;
using Trinetra.Federation.Storage.Repositories;
using Trinetra.Federation.Storage.Security;

namespace Trinetra.IntegrationTests;

/// <summary>
/// PR7e — password history (block last <see cref="PasswordPolicy.HistoryDepth"/>) and minimum
/// age on self-service change (finding 4-M5). Repository-level; the endpoint wiring is exercised
/// in <see cref="TokenRevocationTests"/>-style suites elsewhere.
/// </summary>
public sealed class PasswordHistoryTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly PostgresFixture _fixture;

    private static readonly Guid Alice = Guid.Parse("d9999999-9999-9999-9999-999999999991");
    private static readonly Guid Bob   = Guid.Parse("d9999999-9999-9999-9999-999999999992");

    public PasswordHistoryTests(PostgresFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        var h = PasswordHasher.Hash("initial-password-1");
        await _fixture.ExecuteAsync($"""
            DELETE FROM federation.password_history WHERE user_id IN ('{Alice}', '{Bob}');
            DELETE FROM federation.platform_users WHERE id IN ('{Alice}', '{Bob}');

            INSERT INTO federation.platform_users
                (id, username, display_name, password_hash, password_salt,
                 password_iterations, password_algorithm)
            VALUES
                ('{Alice}', 'ph-alice', 'Alice', '\x{Convert.ToHexString(h.Hash)}',
                 '\x{Convert.ToHexString(h.Salt)}', {h.Iterations}, '{h.Algorithm}'),
                ('{Bob}', 'ph-bob', 'Bob', '\x{Convert.ToHexString(h.Hash)}',
                 '\x{Convert.ToHexString(h.Salt)}', {h.Iterations}, '{h.Algorithm}');
            """);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private UserRepository Users => new(_fixture.DataSource);

    private async Task Set(Guid user, string password)
    {
        await using var work = await UnitOfWork.BeginAsync(_fixture.DataSource, CancellationToken.None);
        await Users.SetPasswordAsync(user, PasswordHasher.Hash(password), mustChange: false, work,
            CancellationToken.None);
        await work.CommitAsync(CancellationToken.None);
    }

    private Task<long> HistoryCount(Guid user) => _fixture.ScalarAsync<long>(
        $"SELECT count(*) FROM federation.password_history WHERE user_id = '{user}'");

    [Fact]
    public async Task SetPassword_AppendsOutgoingHashToHistory()
    {
        await Set(Alice, "second-password-2");

        (await HistoryCount(Alice)).ShouldBe(1);

        var history = await Users.LoadPasswordHistoryAsync(Alice, CancellationToken.None);
        PasswordHasher.Verify("initial-password-1", history[0].Hash).ShouldBeTrue();
    }

    [Fact]
    public async Task SetPassword_PrunesBeyondDepth()
    {
        for (var i = 2; i <= 8; i++)
        {
            await Set(Alice, $"password-number-{i}");
        }

        (await HistoryCount(Alice)).ShouldBe(PasswordPolicy.HistoryDepth);
    }

    [Fact]
    public async Task SetPassword_OtherUsersHistoryUntouched()
    {
        await Set(Alice, "a-two");
        await Set(Alice, "a-three");

        (await HistoryCount(Bob)).ShouldBe(0);
    }

    [Fact]
    public async Task SetPassword_HistoryWriteRidesTheUnitOfWork()
    {
        await using (var work = await UnitOfWork.BeginAsync(_fixture.DataSource, CancellationToken.None))
        {
            await Users.SetPasswordAsync(Alice, PasswordHasher.Hash("rolled-back"), mustChange: false,
                work, CancellationToken.None);
            // no commit
        }

        (await HistoryCount(Alice)).ShouldBe(0);
    }

    [Fact]
    public async Task RehashPassword_WritesNoHistoryRow()
    {
        await using var work = await UnitOfWork.BeginAsync(_fixture.DataSource, CancellationToken.None);
        await Users.RehashPasswordAsync(Alice, PasswordHasher.Hash("initial-password-1"), work,
            CancellationToken.None);
        await work.CommitAsync(CancellationToken.None);

        (await HistoryCount(Alice)).ShouldBe(0);
    }

    [Fact]
    public async Task LoadPasswordHistory_NewestFirst_RespectsDepth()
    {
        for (var i = 2; i <= 8; i++)
        {
            await Set(Alice, $"password-number-{i}");
        }

        var history = await Users.LoadPasswordHistoryAsync(Alice, CancellationToken.None);
        history.Count.ShouldBe(PasswordPolicy.HistoryDepth);
        history.Select(h => h.SetAt).ShouldBeInOrder(SortDirection.Descending);
    }

    [Fact]
    public async Task LoadPasswordHistory_NeverChanged_ReturnsEmpty() =>
        (await Users.LoadPasswordHistoryAsync(Bob, CancellationToken.None)).ShouldBeEmpty();

    // ---- endpoint wiring (self-service change) ---------------------------

    private static readonly IServiceProvider Services = new ServiceCollection()
        .AddLogging().AddProblemDetails().BuildServiceProvider();

    private static JwtTokenService NewJwt() => new(Options.Create(new AuthOptions
    {
        Jwt = new JwtOptions { SigningKey = Convert.ToBase64String(new byte[48]) },
    }));

    private async Task<int> ChangePassword(Guid user, string current, string next)
    {
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim("sub", user.ToString())], "test")),
            RequestServices = Services,
        };
        var audit = new AuthAuditRepository(
            _fixture.DataSource,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AuthAuditRepository>.Instance);
        var result = await AuthEndpoints.ChangePasswordAsync(
            new ChangePasswordRequest(current, next), http,
            Users, new RefreshTokenRepository(_fixture.DataSource), audit, _fixture.DataSource,
            NewJwt(), CancellationToken.None);

        var body = new MemoryStream();
        var ctx = new DefaultHttpContext { RequestServices = Services, Response = { Body = body } };
        await result.ExecuteAsync(ctx);
        return ctx.Response.StatusCode;
    }

    private async Task PrimeCurrentPassword(Guid user, string password, string setAt)
    {
        var h = PasswordHasher.Hash(password);
        await _fixture.ExecuteAsync($"""
            UPDATE federation.platform_users
            SET password_hash = '\x{Convert.ToHexString(h.Hash)}',
                password_salt = '\x{Convert.ToHexString(h.Salt)}',
                password_iterations = {h.Iterations}, password_algorithm = '{h.Algorithm}'
            WHERE id = '{user}';
            INSERT INTO federation.password_history
                (user_id, password_hash, password_salt, password_iterations, password_algorithm, set_at)
            VALUES ('{user}', '\x{Convert.ToHexString(h.Hash)}', '\x{Convert.ToHexString(h.Salt)}',
                    {h.Iterations}, '{h.Algorithm}', {setAt});
            """);
    }

    [Fact]
    public async Task ChangePassword_RejectsAPreviousPassword()
    {
        // Current password old enough that min-age doesn't fire; a history row holds "old-secret".
        await PrimeCurrentPassword(Alice, "current-secret-x", "now() - interval '2 days'");
        await _fixture.ExecuteAsync($"""
            INSERT INTO federation.password_history
                (user_id, password_hash, password_salt, password_iterations, password_algorithm, set_at)
            SELECT '{Alice}', password_hash, password_salt, password_iterations, password_algorithm,
                   now() - interval '5 days'
            FROM federation.platform_users WHERE id = '{Alice}';
            """);
        // Make the second history row actually be "old-secret".
        var old = PasswordHasher.Hash("old-secret-y");
        await _fixture.ExecuteAsync($"""
            UPDATE federation.password_history
            SET password_hash = '\x{Convert.ToHexString(old.Hash)}',
                password_salt = '\x{Convert.ToHexString(old.Salt)}',
                password_iterations = {old.Iterations}, password_algorithm = '{old.Algorithm}'
            WHERE user_id = '{Alice}' AND set_at < now() - interval '3 days';
            """);

        (await ChangePassword(Alice, "current-secret-x", "old-secret-y")).ShouldBe(400);
    }

    [Fact]
    public async Task ChangePassword_RejectsWhenCurrentYoungerThan24h()
    {
        await PrimeCurrentPassword(Alice, "fresh-secret-z", "now() - interval '1 hour'");

        (await ChangePassword(Alice, "fresh-secret-z", "another-secret-w")).ShouldBe(400);
    }

    [Fact]
    public async Task ChangePassword_AllowsWhenCurrentOlderThan24h_AndNotReused()
    {
        await PrimeCurrentPassword(Alice, "settled-secret-a", "now() - interval '25 hours'");

        (await ChangePassword(Alice, "settled-secret-a", "totally-new-secret-b")).ShouldBe(200);
    }

    [Fact]
    public async Task ChangePassword_ForcedChange_NotAgeBlocked()
    {
        // An admin reset stamps a history row at now(); the mandatory follow-up self-service
        // change must not be 400'd by the 24h minimum age (finding 4-M5, BA review fix).
        await PrimeCurrentPassword(Alice, "just-reset-secret", "now() - interval '30 seconds'");
        await _fixture.ExecuteAsync(
            $"UPDATE federation.platform_users SET must_change_password = TRUE WHERE id = '{Alice}'");

        (await ChangePassword(Alice, "just-reset-secret", "chosen-by-user-secret")).ShouldBe(200);
    }

    [Fact]
    public async Task ChangePassword_ForcedChange_StillRejectsAReusedPassword()
    {
        await PrimeCurrentPassword(Alice, "reset-value-a", "now() - interval '30 seconds'");
        await _fixture.ExecuteAsync($"""
            UPDATE federation.platform_users SET must_change_password = TRUE WHERE id = '{Alice}';
            INSERT INTO federation.password_history
                (user_id, password_hash, password_salt, password_iterations, password_algorithm, set_at)
            SELECT '{Alice}', password_hash, password_salt, password_iterations, password_algorithm,
                   now() - interval '10 days'
            FROM federation.platform_users WHERE id = '{Alice}';
            """);
        var old = PasswordHasher.Hash("previously-used-b");
        await _fixture.ExecuteAsync($"""
            UPDATE federation.password_history
            SET password_hash = '\x{Convert.ToHexString(old.Hash)}',
                password_salt = '\x{Convert.ToHexString(old.Salt)}',
                password_iterations = {old.Iterations}, password_algorithm = '{old.Algorithm}'
            WHERE user_id = '{Alice}' AND set_at < now() - interval '5 days';
            """);

        (await ChangePassword(Alice, "reset-value-a", "previously-used-b")).ShouldBe(400);
    }

    [Fact]
    public async Task ChangePassword_NeverChanged_NotAgeLimited()
    {
        // Bob has no history rows at all.
        var h = PasswordHasher.Hash("bobs-current-c");
        await _fixture.ExecuteAsync($"""
            UPDATE federation.platform_users
            SET password_hash = '\x{Convert.ToHexString(h.Hash)}',
                password_salt = '\x{Convert.ToHexString(h.Salt)}',
                password_iterations = {h.Iterations}, password_algorithm = '{h.Algorithm}'
            WHERE id = '{Bob}';
            """);

        (await ChangePassword(Bob, "bobs-current-c", "bobs-new-secret-d")).ShouldBe(200);
    }
}
