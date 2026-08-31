using Shouldly;

namespace Trinetra.IntegrationTests;

/// <summary>
/// The role/permission seed data the API's <c>RequirePermission</c> gates resolve against.
/// A permission that no role holds is an endpoint nobody can reach — v1.2 shipped three of
/// those, and v1.4 is what closes them.
/// </summary>
public sealed class SeedPermissionsTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;

    public SeedPermissionsTests(PostgresFixture fixture) => _fixture = fixture;

    private Task<long> RoleHasAsync(string role, string permission) => _fixture.ScalarAsync<long>(
        $"""
        SELECT count(*)
        FROM federation.role_permissions rp
        JOIN federation.roles r ON r.id = rp.role_id
        WHERE r.code = '{role}' AND rp.permission_code = '{permission}';
        """);

    [Theory]
    [InlineData("vms.read")]
    [InlineData("observation.write")]
    [InlineData("worker.heartbeat")]
    [InlineData("credential.resolve")]
    public async Task DetectionWorker_Holds_ItsFourPermissions(string permission)
    {
        (await RoleHasAsync("DETECTION_WORKER", permission))
            .ShouldBe(1, $"the AI worker's key cannot function without {permission}");
    }

    [Fact]
    public async Task DetectionWorker_Holds_NothingElse()
    {
        var count = await _fixture.ScalarAsync<long>(
            """
            SELECT count(*)
            FROM federation.role_permissions rp
            JOIN federation.roles r ON r.id = rp.role_id
            WHERE r.code = 'DETECTION_WORKER';
            """);

        count.ShouldBe(4, "a machine role is least-privilege — exactly its four permissions "
                        + "(vms.read, observation.write, worker.heartbeat, credential.resolve)");
    }

    [Theory]
    [InlineData("observation.write")]
    [InlineData("worker.heartbeat")]
    [InlineData("watchlist.manage")]
    [InlineData("apikey.manage")]
    [InlineData("credential.resolve")]
    public async Task SuperAdmin_BackfillReached_PermissionsAddedAfterV1(string permission)
    {
        (await RoleHasAsync("SUPER_ADMIN", permission))
            .ShouldBe(1, "the v1.4 CROSS JOIN backfill grants SUPER_ADMIN every later permission");
    }
}
