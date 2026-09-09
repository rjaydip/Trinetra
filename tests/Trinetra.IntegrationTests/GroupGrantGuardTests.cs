using Shouldly;
using Trinetra.Federation.Api.Auth;
using Trinetra.Federation.Storage;
using Trinetra.Federation.Storage.Repositories;

namespace Trinetra.IntegrationTests;

/// <summary>
/// <see cref="GroupGrantGuard"/> — the chokepoint that <see cref="ApiKeyEndpoints"/> and
/// <c>UserEndpoints.AddToGroupAsync</c> both run before conferring an access group's grant.
/// </summary>
/// <remarks>
/// Focus here is finding <b>8-H3</b>: a <c>DRAFT</c> or <c>INACTIVE</c> group grants nothing
/// today, so binding a key or a membership to one silently plants a grant that springs to full
/// life — with no fresh review — the moment someone activates the group. The guard rejects a
/// non-<c>ACTIVE</c> group with a 409, ahead of the unscoped short-circuit, so even a
/// platform-admin cannot do it by accident.
/// </remarks>
public sealed class GroupGrantGuardTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly PostgresFixture _fixture;

    private static readonly Guid DraftGroup    = Guid.Parse("f8000000-0000-4000-8000-00000000e001");
    private static readonly Guid InactiveGroup = Guid.Parse("f8000000-0000-4000-8000-00000000e002");
    private static readonly Guid ActiveGroup   = Guid.Parse("f8000000-0000-4000-8000-00000000e003");
    private static readonly Guid MissingGroup  = Guid.Parse("f8000000-0000-4000-8000-00000000eeee");

    public GroupGrantGuardTests(PostgresFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        await _fixture.ResetTargetsAsync();
        await _fixture.ExecuteAsync($"""
            DELETE FROM federation.access_groups WHERE code LIKE 'GGG-%';

            INSERT INTO federation.access_groups (id, code, name, role_id, status)
            SELECT v.id, v.code, v.code, r.id, v.status
            FROM federation.roles r, (VALUES
                ('{DraftGroup}'::uuid,    'GGG-DRAFT',    'DRAFT'),
                ('{InactiveGroup}'::uuid, 'GGG-INACTIVE', 'INACTIVE'),
                ('{ActiveGroup}'::uuid,   'GGG-ACTIVE',   'ACTIVE')
            ) AS v(id, code, status)
            WHERE r.code = 'VIEWER';
            """);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private AccessGroupRepository Groups => new(_fixture.DataSource);

    // Unscoped for apikey.manage on both dimensions — the account with no ceiling to escalate
    // past. The non-ACTIVE check must still stop it.
    private static CallerContext PlatformAdmin() => new()
    {
        UserId = Guid.Parse("d8000000-0000-4000-8000-00000000f001"),
        Actor = "platform-admin",
        Permissions = new HashSet<string>(StringComparer.Ordinal) { "apikey.manage" },
        UnscopedPermissions = new HashSet<string>(StringComparer.Ordinal) { "apikey.manage" },
        UnscopedGeography = new HashSet<string>(StringComparer.Ordinal) { "apikey.manage" },
    };

    [Fact]
    public async Task Check_DraftGroup_IsRefusedWith409()
    {
        var problem = await GroupGrantGuard.CheckAsync(
            Groups, PlatformAdmin(), DraftGroup, "apikey.manage", CancellationToken.None);

        problem.ShouldNotBeNull();
        problem!.StatusCode.ShouldBe(409);
    }

    [Fact]
    public async Task Check_InactiveGroup_IsRefusedWith409()
    {
        var problem = await GroupGrantGuard.CheckAsync(
            Groups, PlatformAdmin(), InactiveGroup, "apikey.manage", CancellationToken.None);

        problem.ShouldNotBeNull();
        problem!.StatusCode.ShouldBe(409);
    }

    [Fact]
    public async Task Check_ActiveGroup_ForAnUnscopedCaller_IsAllowed()
    {
        var problem = await GroupGrantGuard.CheckAsync(
            Groups, PlatformAdmin(), ActiveGroup, "apikey.manage", CancellationToken.None);

        problem.ShouldBeNull();
    }

    [Fact]
    public async Task Check_UnknownGroup_Is404()
    {
        var problem = await GroupGrantGuard.CheckAsync(
            Groups, PlatformAdmin(), MissingGroup, "apikey.manage", CancellationToken.None);

        problem.ShouldNotBeNull();
        problem!.StatusCode.ShouldBe(404);
    }
}
