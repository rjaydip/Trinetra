using System.Security.Cryptography;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Trinetra.Federation.Core.Abstractions;
using Trinetra.Federation.Core.Events;
using Trinetra.Federation.Storage;
using Trinetra.Federation.Storage.Secrets;

namespace Trinetra.IntegrationTests;

public sealed class SecretsAndEventsTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private readonly SecretEncryption _encryption;

    public SecretsAndEventsTests(PostgresFixture fixture)
    {
        _fixture = fixture;
        _encryption = SecretEncryption.FromBase64Key("test-key-1", SecretEncryption.GenerateKeyBase64());
    }

    private static readonly Guid TargetOne = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid TargetTwo = Guid.Parse("00000000-0000-0000-0000-000000000002");

    public async Task InitializeAsync()
    {
        await _fixture.ResetTargetsAsync();
        await _fixture.ExecuteAsync($"""
        DELETE FROM federation.federation_event;
        DELETE FROM federation.secret;

        -- Cursors are FK-constrained to their target, correctly: a cursor for a target that
        -- does not exist would be orphaned state nothing ever cleans up.
        INSERT INTO federation.connector_target
            (id, code, organization_unit_id, display_name, vendor, endpoint, credential_reference)
        VALUES ('{TargetOne}', 'TGT-NEW', '{PostgresFixture.PoliceUnit}', 'New Target',
                'Onvif', 'http://10.0.0.1', 'vault://x'),
               ('{TargetTwo}', 'TGT-CURSOR', '{PostgresFixture.PoliceUnit}', 'Cursor Target',
                'Onvif', 'http://10.0.0.2', 'vault://y')
        ON CONFLICT (id) DO NOTHING;

        SELECT federation.ensure_event_partitions(CURRENT_DATE, 2);
        """);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private PostgresCredentialResolver NewResolver(SecretEncryption? encryption = null) =>
        new(_fixture.DataSource, encryption ?? _encryption, "worker-test",
            NullLogger<PostgresCredentialResolver>.Instance);

    private async Task StoreSecretAsync(string reference, string username, string password,
        SecretEncryption? encryption = null)
    {
        var enc = encryption ?? _encryption;
        var payload = JsonSerializer.Serialize(new { Username = username, Password = password });
        var sealedSecret = enc.Seal(payload);

        await using var connection = await _fixture.DataSource.OpenConnectionAsync(CancellationToken.None);
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO federation.secret (credential_reference, ciphertext, nonce, tag, key_id)
            VALUES (@Reference, @Ciphertext, @Nonce, @Tag, @KeyId)
            ON CONFLICT (credential_reference) DO UPDATE
            SET ciphertext = EXCLUDED.ciphertext, nonce = EXCLUDED.nonce,
                tag = EXCLUDED.tag, key_id = EXCLUDED.key_id;
            """, new
        {
            Reference = reference,
            sealedSecret.Ciphertext,
            sealedSecret.Nonce,
            sealedSecret.Tag,
            sealedSecret.KeyId,
        }, cancellationToken: CancellationToken.None));
    }

    // ---- Secrets -----------------------------------------------------------

    [Fact]
    public async Task Credential_RoundTripsThroughEncryptedStorage()
    {
        await StoreSecretAsync("vault://vms/1", "admin", "s3cret-p@ss");

        var credential = await NewResolver().ResolveAsync("vault://vms/1", CancellationToken.None);

        credential.Username.ShouldBe("admin");
        credential.Password.ShouldBe("s3cret-p@ss");
    }

    [Fact]
    public async Task StoredSecret_IsNotReadableAsPlaintext()
    {
        // The point of encrypting in the application: a DBA with full table access still
        // cannot read a camera password.
        await StoreSecretAsync("vault://vms/2", "admin", "unmistakable-password");

        var raw = await _fixture.ScalarAsync<byte[]>(
            "SELECT ciphertext FROM federation.secret WHERE credential_reference = 'vault://vms/2';");

        System.Text.Encoding.UTF8.GetString(raw!).ShouldNotContain("unmistakable-password");
    }

    [Fact]
    public async Task Resolution_IsAudited()
    {
        await StoreSecretAsync("vault://vms/3", "admin", "pw");
        await NewResolver().ResolveAsync("vault://vms/3", CancellationToken.None);

        var audited = await _fixture.ScalarAsync<long>("""
            SELECT count(*) FROM federation.credential_access_log
            WHERE credential_reference = 'vault://vms/3' AND succeeded AND accessed_by = 'worker-test';
            """);

        audited.ShouldBe(1);
    }

    [Fact]
    public async Task FailedResolution_IsAlsoAudited()
    {
        // Repeated failures against one reference mean a rotated secret nobody updated, or
        // someone probing. Either way it must be visible.
        await Should.ThrowAsync<InvalidOperationException>(
            NewResolver().ResolveAsync("vault://does-not-exist", CancellationToken.None));

        var audited = await _fixture.ScalarAsync<long>("""
            SELECT count(*) FROM federation.credential_access_log
            WHERE credential_reference = 'vault://does-not-exist' AND NOT succeeded;
            """);

        audited.ShouldBe(1);
    }

    [Fact]
    public async Task WrongKey_FailsLoudlyRatherThanReturningGarbage()
    {
        // GCM is authenticated: a wrong key must fail, never yield plausible bytes that then
        // get sent to a device as a password.
        await StoreSecretAsync("vault://vms/4", "admin", "pw");

        var otherKey = SecretEncryption.FromBase64Key("test-key-1", SecretEncryption.GenerateKeyBase64());

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            NewResolver(otherKey).ResolveAsync("vault://vms/4", CancellationToken.None));

        ex.Message.ShouldContain("failed authenticated decryption");
    }

    [Fact]
    public async Task KeyIdMismatch_NamesTheRotationProblemExplicitly()
    {
        await StoreSecretAsync("vault://vms/5", "admin", "pw");

        var rotated = SecretEncryption.FromBase64Key("test-key-2", SecretEncryption.GenerateKeyBase64());

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            NewResolver(rotated).ResolveAsync("vault://vms/5", CancellationToken.None));

        // Distinguishes an incomplete rotation from a corrupted row, so an operator does not
        // go hunting the wrong problem.
        ex.Message.ShouldContain("test-key-1");
        ex.Message.ShouldContain("test-key-2");
    }

    [Fact]
    public async Task ResolutionWithContext_AuditsTheCallerAndTarget()
    {
        // The worker credential-resolve endpoint must be able to answer "which caller read which
        // credential, for which target" — not just attribute it to the resolving process.
        await StoreSecretAsync("vault://x", "admin", "pw");

        await NewResolver().ResolveAsync(
            "vault://x",
            new CredentialAccessContext("apikey:worker-42", TargetOne, AuditFailureIsFatal: true),
            CancellationToken.None);

        var audited = await _fixture.ScalarAsync<long>($"""
            SELECT count(*) FROM federation.credential_access_log
            WHERE credential_reference = 'vault://x' AND succeeded
              AND accessed_by = 'apikey:worker-42' AND target_id = '{TargetOne}';
            """);

        audited.ShouldBe(1);
    }

    [Fact]
    public async Task ResolutionWithContext_ForUnprovisionedTarget_ThrowsNotProvisioned()
    {
        // The endpoint maps this to 404, not 500: a target created but not yet given a
        // credential is an ordinary onboarding state.
        await Should.ThrowAsync<CredentialNotProvisionedException>(
            NewResolver().ResolveAsync(
                "vault://never-set",
                new CredentialAccessContext("apikey:worker-42", TargetOne, AuditFailureIsFatal: true),
                CancellationToken.None));
    }

    // ---- Event persistence -------------------------------------------------

    private static NormalisedEvent NewEvent(string id, string? sourceEventId, DateTimeOffset when) =>
        new()
        {
            EventId = id,
            SourceVmsId = TargetOne,
            SourceEventId = sourceEventId,
            CameraId = "CAM-001",
            OrganizationUnitId = PostgresFixture.PoliceUnit,
            EventType = EventType.AnprDetection,
            VendorEventType = "tns1:VideoAnalytics/LicensePlate",
            Timestamp = when,
            ObjectReference = "GJ05AB1234",
            Confidence = 0.96,
        };

    [Fact]
    public async Task Events_AreWrittenInBatch()
    {
        var store = new EventStore(_fixture.DataSource);
        var now = DateTimeOffset.UtcNow;

        var written = await store.AppendAsync(
            [.. Enumerable.Range(0, 250).Select(i => NewEvent($"EVT-B{i}", $"SRC-B{i}", now))], CancellationToken.None);

        written.ShouldBe(250);
    }

    [Fact]
    public async Task RedeliveredEvents_AreSuppressed()
    {
        // At-least-once transport plus idempotent sinks is what makes the system
        // effectively-once. Nothing may assume exactly-once delivery.
        var store = new EventStore(_fixture.DataSource);
        var now = DateTimeOffset.UtcNow;

        await store.AppendAsync([NewEvent("EVT-D1", "SRC-DUP", now)], CancellationToken.None);
        var second = await store.AppendAsync([NewEvent("EVT-D2", "SRC-DUP", now)], CancellationToken.None);

        second.ShouldBe(0, "a redelivered event must not create a second row");

        var total = await _fixture.ScalarAsync<long>(
            "SELECT count(*) FROM federation.federation_event WHERE source_event_id = 'SRC-DUP';");
        total.ShouldBe(1);
    }

    [Fact]
    public async Task EventsWithoutVendorId_AreAllStored()
    {
        // Vendors that supply no event id (ONVIF, Dahua) must not have their events collapsed
        // into one row by deduplication.
        var store = new EventStore(_fixture.DataSource);
        var now = DateTimeOffset.UtcNow;

        var written = await store.AppendAsync(
            [NewEvent("EVT-N1", null, now), NewEvent("EVT-N2", null, now)], CancellationToken.None);

        written.ShouldBe(2);
    }

    // ---- Cursors -----------------------------------------------------------

    [Fact]
    public async Task NewTarget_SeedsItsCursorToNow_NotTheEpoch()
    {
        // Seeding to the epoch would make a newly onboarded target request its VMS's entire
        // event history on first contact — a self-inflicted flood and a load spike for the vendor.
        var state = new ConnectorStateStore(_fixture.DataSource);

        var cursor = await state.GetCursorAsync(TargetOne, CancellationToken.None);

        cursor.Position.ShouldBe(DateTimeOffset.UtcNow, tolerance: TimeSpan.FromMinutes(1));
        cursor.MaxLookback.ShouldBe(TimeSpan.FromHours(6));
    }

    [Fact]
    public async Task Cursor_NeverMovesBackwards()
    {
        // A late or out-of-order batch must not rewind progress and cause events to be
        // republished indefinitely.
        var state = new ConnectorStateStore(_fixture.DataSource);
        await state.GetCursorAsync(TargetTwo, CancellationToken.None);

        var ahead = DateTimeOffset.UtcNow.AddHours(1);
        await state.AdvanceCursorAsync(TargetTwo, ahead, "evt-100", CancellationToken.None);
        await state.AdvanceCursorAsync(TargetTwo, DateTimeOffset.UtcNow.AddHours(-1), "evt-1", CancellationToken.None);

        var cursor = await state.GetCursorAsync(TargetTwo, CancellationToken.None);
        cursor.Position.ShouldBe(ahead, tolerance: TimeSpan.FromSeconds(1));
    }
}
