using Shouldly;
using Trinetra.Federation.Storage.Repositories;

namespace Trinetra.IntegrationTests;

/// <summary>PR7c — <see cref="ApiKeyRepository.TouchAsync"/> writes <c>last_used_at</c> at most
/// once per key per five minutes (finding 8-NEW-H).</summary>
public sealed class ApiKeyCoarseTouchTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly PostgresFixture _fixture;

    private static readonly Guid GroupId = Guid.Parse("f3333333-3333-3333-3333-333333333333");
    private static readonly Guid KeyId   = Guid.Parse("c3333333-0000-4000-8000-000000000abc");

    public ApiKeyCoarseTouchTests(PostgresFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        await _fixture.ExecuteAsync($"""
            DELETE FROM federation.api_key WHERE id = '{KeyId}';
            DELETE FROM federation.access_groups WHERE id = '{GroupId}';

            INSERT INTO federation.access_groups (id, code, name, role_id, status)
            SELECT '{GroupId}', 'CTOUCH', 'Coarse touch test', r.id, 'ACTIVE'
            FROM federation.roles r WHERE r.code = 'VIEWER';

            INSERT INTO federation.api_key (id, key_id, key_hash, display_name, group_id)
            VALUES ('{KeyId}', 'ak_ctouch', 'ctouch-hash', 'Coarse touch', '{GroupId}');
            """);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private ApiKeyRepository Repo => new(_fixture.DataSource);

    private Task<string?> LastUsed() => _fixture.ScalarAsync<string>(
        $"SELECT last_used_at::text FROM federation.api_key WHERE id = '{KeyId}'");

    [Fact]
    public async Task Touch_FirstCall_Writes_ReturnsTrue()
    {
        (await LastUsed()).ShouldBeNull();
        (await Repo.TouchAsync(KeyId, CancellationToken.None)).ShouldBeTrue();
        (await LastUsed()).ShouldNotBeNull();
    }

    [Fact]
    public async Task Touch_SecondCallImmediately_NoWrite_ReturnsFalse()
    {
        await Repo.TouchAsync(KeyId, CancellationToken.None);
        var stamp = await LastUsed();

        (await Repo.TouchAsync(KeyId, CancellationToken.None)).ShouldBeFalse();
        (await LastUsed()).ShouldBe(stamp);
    }

    [Fact]
    public async Task Touch_AfterWindowLapses_WritesAgain()
    {
        await _fixture.ExecuteAsync(
            $"UPDATE federation.api_key SET last_used_at = now() - interval '6 minutes' "
            + $"WHERE id = '{KeyId}'");

        (await Repo.TouchAsync(KeyId, CancellationToken.None)).ShouldBeTrue();
    }

    [Fact]
    public async Task Touch_UnknownId_ReturnsFalse_NoThrow() =>
        (await Repo.TouchAsync(Guid.NewGuid(), CancellationToken.None)).ShouldBeFalse();
}
