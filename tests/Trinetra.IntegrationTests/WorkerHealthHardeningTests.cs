using Shouldly;
using Trinetra.Federation.Storage;
using Trinetra.Federation.Storage.Repositories;

namespace Trinetra.IntegrationTests;

/// <summary>
/// PR: worker-health hardening (API review Finding 17). A heartbeat is bound to the reporting
/// API key (17-M1), <c>last_heartbeat_at</c> is server-set regardless of the client clock
/// (17-M2), the list needs <c>worker.read</c> and the retire route needs <c>worker.manage</c>
/// (17-M3), and a decommissioned worker can be cleared (17-L1).
/// </summary>
public sealed class WorkerHealthHardeningTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly PostgresFixture _fixture;

    private static readonly Guid KeyA = Guid.Parse("c1a10000-0000-0000-0000-0000000000a1");
    private static readonly Guid KeyB = Guid.Parse("c1a10000-0000-0000-0000-0000000000b2");
    private static readonly Guid GroupId = Guid.Parse("c1a10000-0000-0000-0000-0000000000f0");

    public WorkerHealthHardeningTests(PostgresFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        await _fixture.ExecuteAsync($"""
            DELETE FROM federation.ai_worker_health;
            DELETE FROM federation.api_key WHERE id IN ('{KeyA}', '{KeyB}');
            DELETE FROM federation.access_groups WHERE id = '{GroupId}';

            INSERT INTO federation.access_groups (id, code, name, role_id, status)
            SELECT '{GroupId}', 'WH_TEST', 'Worker-health test', r.id, 'ACTIVE'
            FROM federation.roles r WHERE r.code = 'DETECTION_WORKER';

            INSERT INTO federation.api_key (id, key_id, key_hash, display_name, group_id)
            VALUES ('{KeyA}', 'ak_wh_a', '\x00', 'Fleet A', '{GroupId}'),
                   ('{KeyB}', 'ak_wh_b', '\x00', 'Fleet B', '{GroupId}');
            """);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private AiWorkerHealthRepository Repo => new(_fixture.DataSource);

    private static CallerContext KeyCaller(Guid apiKeyId, params string[] permissions) => new()
    {
        ApiKeyId = apiKeyId,
        Actor = $"apikey:{apiKeyId}",
        Permissions = new HashSet<string>(permissions, StringComparer.Ordinal),
    };

    private static CallerContext UserCaller(params string[] permissions) => new()
    {
        UserId = Guid.NewGuid(),
        Actor = "user:test",
        Permissions = new HashSet<string>(permissions, StringComparer.Ordinal),
    };

    [Fact]
    public async Task Heartbeat_IsBoundToTheReportingKey_NoCrossKeyOverwrite()
    {
        await Repo.UpsertHeartbeatAsync(
            KeyCaller(KeyA, "worker.heartbeat"), "ai-worker-0-of-2", "host-a", DateTimeOffset.UtcNow, default);
        await Repo.UpsertHeartbeatAsync(
            KeyCaller(KeyB, "worker.heartbeat"), "ai-worker-0-of-2", "host-b", DateTimeOffset.UtcNow, default);

        var count = await _fixture.ScalarAsync<long>(
            "SELECT count(*) FROM federation.ai_worker_health WHERE worker_id = 'ai-worker-0-of-2';");
        count.ShouldBe(2);

        var owners = await _fixture.ScalarAsync<long>($"""
            SELECT count(DISTINCT api_key_id) FROM federation.ai_worker_health
            WHERE api_key_id IN ('{KeyA}', '{KeyB}');
            """);
        owners.ShouldBe(2);
    }

    [Fact]
    public async Task Heartbeat_SameWorkerIdAndHost_UnderTwoKeys_DoesNotOverwrite()
    {
        // Same worker_id AND hostname — the only thing separating the rows is the key. If the
        // identity ever narrowed to (worker_id, hostname), KeyB's heartbeat would move KeyA's
        // last_heartbeat_at. This is the assertion 17-M1 actually needs.
        await Repo.UpsertHeartbeatAsync(
            KeyCaller(KeyA, "worker.heartbeat"), "ai-worker-0-of-1", "shared-host", DateTimeOffset.UtcNow, default);
        var keyASeenBefore = await _fixture.ScalarAsync<string>($"""
            SELECT last_heartbeat_at::text FROM federation.ai_worker_health
            WHERE api_key_id = '{KeyA}' AND worker_id = 'ai-worker-0-of-1';
            """);

        await Task.Delay(50);
        await Repo.UpsertHeartbeatAsync(
            KeyCaller(KeyB, "worker.heartbeat"), "ai-worker-0-of-1", "shared-host", DateTimeOffset.UtcNow, default);

        var rows = await _fixture.ScalarAsync<long>(
            "SELECT count(*) FROM federation.ai_worker_health WHERE worker_id = 'ai-worker-0-of-1';");
        var keyASeenAfter = await _fixture.ScalarAsync<string>($"""
            SELECT last_heartbeat_at::text FROM federation.ai_worker_health
            WHERE api_key_id = '{KeyA}' AND worker_id = 'ai-worker-0-of-1';
            """);

        rows.ShouldBe(2);
        keyASeenAfter.ShouldBe(keyASeenBefore);
    }

    [Fact]
    public async Task Heartbeat_LastSeen_IsServerTime_NotTheClientClock()
    {
        var future = DateTimeOffset.UtcNow.AddYears(5);

        await Repo.UpsertHeartbeatAsync(
            KeyCaller(KeyA, "worker.heartbeat"), "ai-worker-0-of-1", "host-a", future, default);

        // last_heartbeat_at is the server clock (within seconds of now); reported_at keeps the
        // worker's absurd future claim untouched, purely for drift visibility.
        var lastSeenAgeSeconds = await _fixture.ScalarAsync<double>(
            "SELECT EXTRACT(EPOCH FROM (now() - last_heartbeat_at))::float8 "
            + "FROM federation.ai_worker_health WHERE worker_id = 'ai-worker-0-of-1';");
        var reportedAheadSeconds = await _fixture.ScalarAsync<double>(
            "SELECT EXTRACT(EPOCH FROM (reported_at - now()))::float8 "
            + "FROM federation.ai_worker_health WHERE worker_id = 'ai-worker-0-of-1';");

        lastSeenAgeSeconds.ShouldBeInRange(-5, 60);
        reportedAheadSeconds.ShouldBeGreaterThan(4 * 365 * 24 * 3600.0);
    }

    [Fact]
    public async Task Heartbeat_Upsert_KeepsFirstSeen_MovesLastSeen()
    {
        var caller = KeyCaller(KeyA, "worker.heartbeat");
        await Repo.UpsertHeartbeatAsync(caller, "ai-worker-0-of-1", "host-a", DateTimeOffset.UtcNow, default);
        var firstSeen = await _fixture.ScalarAsync<string>(
            "SELECT first_seen_at::text FROM federation.ai_worker_health WHERE worker_id = 'ai-worker-0-of-1';");

        await Task.Delay(50);
        await Repo.UpsertHeartbeatAsync(caller, "ai-worker-0-of-1", "host-a", DateTimeOffset.UtcNow, default);

        var afterFirstSeen = await _fixture.ScalarAsync<string>(
            "SELECT first_seen_at::text FROM federation.ai_worker_health WHERE worker_id = 'ai-worker-0-of-1';");
        var lastSeenMovedAhead = await _fixture.ScalarAsync<bool>(
            "SELECT last_heartbeat_at > first_seen_at "
            + "FROM federation.ai_worker_health WHERE worker_id = 'ai-worker-0-of-1';");
        var rows = await _fixture.ScalarAsync<long>(
            "SELECT count(*) FROM federation.ai_worker_health WHERE worker_id = 'ai-worker-0-of-1';");

        afterFirstSeen.ShouldBe(firstSeen);
        lastSeenMovedAhead.ShouldBeTrue();
        rows.ShouldBe(1);
    }

    [Fact]
    public async Task Heartbeat_FromAUserPrincipal_IsRejected()
    {
        var ex = await Should.ThrowAsync<InvalidOperationException>(() =>
            Repo.UpsertHeartbeatAsync(
                UserCaller("worker.heartbeat"), "ai-worker-0-of-1", "host-a", DateTimeOffset.UtcNow, default));

        ex.Message.ShouldContain("API-key");
    }

    [Fact]
    public async Task List_RequiresWorkerRead_NotWorkerHeartbeat()
    {
        await Should.ThrowAsync<ForbiddenException>(() =>
            Repo.ListAsync(KeyCaller(KeyA, "worker.heartbeat"), PageWindow.UpTo(100), default));

        var ok = await Repo.ListAsync(KeyCaller(KeyA, "worker.read"), PageWindow.UpTo(100), default);
        ok.ShouldNotBeNull();
    }

    [Fact]
    public async Task List_CarriesTheOwningKeyName()
    {
        await Repo.UpsertHeartbeatAsync(
            KeyCaller(KeyA, "worker.heartbeat"), "ai-worker-0-of-1", "host-a", DateTimeOffset.UtcNow, default);

        var page = await Repo.ListAsync(UserCaller("worker.read"), PageWindow.UpTo(100), default);

        var row = page.Items.ShouldHaveSingleItem();
        row.ApiKeyId.ShouldBe(KeyA);
        row.ApiKeyName.ShouldBe("Fleet A");
    }

    [Fact]
    public async Task Retire_RemovesTheRecord_RequiresWorkerManage()
    {
        await Repo.UpsertHeartbeatAsync(
            KeyCaller(KeyA, "worker.heartbeat"), "ai-worker-3-of-4", "host-a", DateTimeOffset.UtcNow, default);
        var id = await _fixture.ScalarAsync<Guid>(
            "SELECT id FROM federation.ai_worker_health WHERE worker_id = 'ai-worker-3-of-4';");

        await using (var denied = await UnitOfWork.BeginAsync(_fixture.DataSource, CancellationToken.None))
        {
            await Should.ThrowAsync<ForbiddenException>(() =>
                Repo.RetireAsync(id, UserCaller("worker.read"), denied, CancellationToken.None));
        }

        await using var work = await UnitOfWork.BeginAsync(_fixture.DataSource, CancellationToken.None);
        var removed = await Repo.RetireAsync(id, UserCaller("worker.manage"), work, CancellationToken.None);
        await work.CommitAsync(CancellationToken.None);

        removed.ShouldNotBeNull();
        removed!.WorkerId.ShouldBe("ai-worker-3-of-4");

        var count = await _fixture.ScalarAsync<long>(
            $"SELECT count(*) FROM federation.ai_worker_health WHERE id = '{id}';");
        count.ShouldBe(0);
    }

    [Fact]
    public async Task Retire_UnknownId_ReturnsNull()
    {
        await using var work = await UnitOfWork.BeginAsync(_fixture.DataSource, CancellationToken.None);
        var removed = await Repo.RetireAsync(Guid.NewGuid(), UserCaller("worker.manage"), work, CancellationToken.None);
        removed.ShouldBeNull();
    }

    [Fact]
    public async Task StateAdmin_LostHeartbeat_GainedRead()
    {
        var perms = await _fixture.ScalarAsync<long>("""
            SELECT count(*) FROM federation.role_permissions rp
            JOIN federation.roles r ON r.id = rp.role_id
            WHERE r.code = 'STATE_ADMIN' AND rp.permission_code = 'worker.heartbeat';
            """);
        perms.ShouldBe(0);

        var read = await _fixture.ScalarAsync<long>("""
            SELECT count(*) FROM federation.role_permissions rp
            JOIN federation.roles r ON r.id = rp.role_id
            WHERE r.code = 'STATE_ADMIN' AND rp.permission_code = 'worker.read';
            """);
        read.ShouldBe(1);

        var workerStillHasHeartbeat = await _fixture.ScalarAsync<long>("""
            SELECT count(*) FROM federation.role_permissions rp
            JOIN federation.roles r ON r.id = rp.role_id
            WHERE r.code = 'DETECTION_WORKER' AND rp.permission_code = 'worker.heartbeat';
            """);
        workerStillHasHeartbeat.ShouldBe(1);

        // DETECTION_WORKER is the ONLY non-SUPER_ADMIN role holding worker.heartbeat — catches a
        // future role picking it up by copy-paste, the defect class 17-M3 is about.
        var nonSuperHolders = await _fixture.ScalarAsync<long>("""
            SELECT count(DISTINCT r.code) FROM federation.role_permissions rp
            JOIN federation.roles r ON r.id = rp.role_id
            WHERE rp.permission_code = 'worker.heartbeat' AND r.code <> 'SUPER_ADMIN';
            """);
        nonSuperHolders.ShouldBe(1);
    }
}
