using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Trinetra.Federation.Api.Auth;
using Trinetra.Federation.Api.Contracts;
using Trinetra.Federation.Api.Endpoints;
using Trinetra.Federation.Storage.Repositories;

namespace Trinetra.IntegrationTests;

/// <summary>
/// Finding 4-M5 / 6-M5: <c>email</c> had no uniqueness constraint — two accounts could share
/// one. Enforced in the database (`ux_platform_users_email`, v1.14.sql), case-insensitively,
/// `NULL` excluded — this proves the constraint is actually live and correctly surfaced as a
/// named `409`, not a raw `500`. Format validation itself is covered at the unit level
/// (<c>UserEmailValidationTests</c>).
/// </summary>
public sealed class UserEmailUniquenessTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly PostgresFixture _fixture;

    public UserEmailUniquenessTests(PostgresFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync() => await _fixture.ExecuteAsync("""
        DELETE FROM federation.platform_users
        WHERE username IN ('email-uniq-a', 'email-uniq-b', 'email-uniq-c', 'email-uniq-d');
        """);

    public Task DisposeAsync() => Task.CompletedTask;

    private UserRepository Repo => new(_fixture.DataSource);

    private static readonly IServiceProvider Services = new ServiceCollection()
        .AddLogging()
        .AddProblemDetails()
        .BuildServiceProvider();

    private static DefaultHttpContext EndpointContext() => new()
    {
        User = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("sub", Guid.NewGuid().ToString()),
            new Claim(TrinetraClaims.Permission, "user.manage"),
            new Claim(TrinetraClaims.UnscopedPermission, "user.manage"),
        ], "test")),
        RequestServices = Services,
    };

    private async Task<int> CreateUserAsync(string username, string? email)
    {
        var result = await UserEndpoints.CreateAsync(
            new CreateUserRequest(username, "Test User", "CorrectHorse-2026!", email),
            Repo, _fixture.DataSource, EndpointContext(), CancellationToken.None);

        var ctx = new DefaultHttpContext { RequestServices = Services };
        await result.Result.ExecuteAsync(ctx);
        return ctx.Response.StatusCode;
    }

    [Fact]
    public async Task DuplicateEmail_DifferentCase_IsRejectedAs409NotRaw500()
    {
        (await CreateUserAsync("email-uniq-a", "Shared@Example.com")).ShouldBe(201);
        (await CreateUserAsync("email-uniq-b", "shared@example.com")).ShouldBe(409);
    }

    [Fact]
    public async Task TwoAccountsWithNoEmail_BothSucceed()
    {
        (await CreateUserAsync("email-uniq-c", null)).ShouldBe(201);
        (await CreateUserAsync("email-uniq-d", null)).ShouldBe(201);
    }
}
