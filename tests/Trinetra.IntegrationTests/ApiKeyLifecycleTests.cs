using Trinetra.Federation.Storage;
using Trinetra.Federation.Storage.Repositories;
using Shouldly;

namespace Trinetra.IntegrationTests;

/// <summary>
/// The list and revoke side of the API key feature — <see cref="ApiKeyRepository.ListAsync"/>
/// and <see cref="ApiKeyRepository.RevokeAsync"/>. The authorization consequences of a revoked
/// key are covered by <see cref="AuthorizationTests"/>; this exercises the lifecycle mechanics.
/// </summary>
public sealed class ApiKeyLifecycleTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly PostgresFixture _fixture;

    private static readonly Guid Admin    = Guid.Parse("d2222222-2222-2222-2222-222222222222");
    private static readonly Guid GroupId  = Guid.Parse("f2222222-2222-2222-2222-222222222222");
    private static readonly Guid LiveKey  = Guid.Parse("c2222222-0000-4000-8000-000000000001");
    private static readonly Guid OldKey   = Guid.Parse("c2222222-0000-4000-8000-000000000002");

    private const string KeyHash = "not-a-real-key-hash";

    public ApiKeyLifecycleTests(PostgresFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        await _fixture.ResetTargetsAsync();

        await _fixture.ExecuteAsync($"""
            DELETE FROM federation.api_key;
            DELETE FROM federation.access_groups;
            DELETE FROM federation.platform_users;

            INSERT INTO federation.platform_users
                (id, username, display_name, password_hash, password_salt, password_iterations)
            VALUES ('{Admin}', 'admin2', 'Admin Two', '\x00', '\x00', 600000);

            INSERT INTO federation.access_groups (id, code, name, role_id, status)
            SELECT '{GroupId}', 'REPORTING', 'Reporting integrations', r.id, 'ACTIVE'
            FROM federation.roles r WHERE r.code = 'VIEWER';

            -- Two keys, created a day apart, so list order is testable.
            INSERT INTO federation.api_key
                (id, key_id, key_hash, display_name, group_id, created_at)
            VALUES
                ('{OldKey}',  'ak_old',  '{KeyHash}', 'Old integration',  '{GroupId}', now() - interval '1 day'),
                ('{LiveKey}', 'ak_live', '{KeyHash}', 'Live integration', '{GroupId}', now());
            """);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private ApiKeyRepository Repo => new(_fixture.DataSource);

    private static CallerContext AdminCaller => new()
    {
        UserId = Admin,
        Actor = "admin2",
        Permissions = new HashSet<string>(StringComparer.Ordinal) { "apikey.manage" },
    };

    [Fact]
    public async Task List_ReturnsKeysNewestFirst_WithGroupCode_AndNoHash()
    {
        var keys = await Repo.ListAsync(CancellationToken.None);

        keys.Count.ShouldBe(2);
        keys[0].KeyId.ShouldBe("ak_live");
        keys[1].KeyId.ShouldBe("ak_old");
        keys[0].GroupCode.ShouldBe("REPORTING");
        keys[0].RevokedAt.ShouldBeNull();

        // The response type has no field that could carry the hash; this is a guard against a
        // future column being added to the projection.
        typeof(ApiKeySummary).GetProperties()
            .Select(p => p.Name)
            .ShouldNotContain("KeyHash");
    }

    [Fact]
    public async Task Revoke_LiveKey_SetsRevokedAtAndRevokedBy()
    {
        await using (var work = await UnitOfWork.BeginAsync(_fixture.DataSource, CancellationToken.None))
        {
            var result = await ApiKeyRepository.RevokeAsync(LiveKey, AdminCaller, work, CancellationToken.None);

            result.Outcome.ShouldBe(RevokeOutcome.Revoked);
            result.DisplayName.ShouldBe("Live integration");
            result.PriorRevokedAt.ShouldBeNull();

            await work.CommitAsync(CancellationToken.None);
        }

        var revokedBy = await _fixture.ScalarAsync<Guid?>(
            $"SELECT revoked_by FROM federation.api_key WHERE id = '{LiveKey}';");
        revokedBy.ShouldBe(Admin);

        var revokedAt = await _fixture.ScalarAsync<DateTimeOffset?>(
            $"SELECT revoked_at FROM federation.api_key WHERE id = '{LiveKey}';");
        revokedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task Revoke_IsIdempotent()
    {
        await FirstRevokeAsync();

        await using var work = await UnitOfWork.BeginAsync(_fixture.DataSource, CancellationToken.None);
        var second = await ApiKeyRepository.RevokeAsync(LiveKey, AdminCaller, work, CancellationToken.None);

        second.Outcome.ShouldBe(RevokeOutcome.AlreadyRevoked);
        second.PriorRevokedAt.ShouldNotBeNull("the second call reports when it was first revoked");
    }

    [Fact]
    public async Task Revoke_UnknownId_IsNotFound()
    {
        await using var work = await UnitOfWork.BeginAsync(_fixture.DataSource, CancellationToken.None);
        var result = await ApiKeyRepository.RevokeAsync(
            Guid.NewGuid(), AdminCaller, work, CancellationToken.None);

        result.Outcome.ShouldBe(RevokeOutcome.NotFound);
    }

    private async Task FirstRevokeAsync()
    {
        await using var work = await UnitOfWork.BeginAsync(_fixture.DataSource, CancellationToken.None);
        await ApiKeyRepository.RevokeAsync(LiveKey, AdminCaller, work, CancellationToken.None);
        await work.CommitAsync(CancellationToken.None);
    }
}
