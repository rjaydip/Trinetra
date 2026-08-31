using System.Security.Cryptography;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using Trinetra.Federation.Core.Abstractions;

namespace Trinetra.Federation.Storage.Secrets;

/// <summary>
/// Resolves <c>credential_reference</c> pointers into live secrets, from encrypted storage.
/// </summary>
/// <remarks>
/// <para>
/// The architecture calls for Vault with PostgreSQL as the fallback. This is that fallback, and
/// it is deliberately shaped so Vault can replace it later without touching a single adapter:
/// everything upstream depends only on <see cref="ICredentialResolver"/>.
/// </para>
/// <para>
/// <b>Every resolution is audited</b>, success or failure. For a platform holding access to an
/// entire state's camera estate, "which worker read which credential, and when" is a
/// requirement rather than a nicety. Failures are audited too — repeated failures against one
/// reference mean either a rotated secret nobody updated, or someone probing.
/// </para>
/// <para>
/// Resolved credentials are <b>not cached</b>. A cache would keep plaintext passwords resident
/// in worker memory indefinitely and would silently serve a revoked credential after rotation.
/// Resolution happens once per connection, not per request, so the cost is negligible.
/// </para>
/// </remarks>
public sealed partial class PostgresCredentialResolver : ICredentialResolver
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly SecretEncryption _encryption;
    private readonly string _accessorId;
    private readonly ILogger<PostgresCredentialResolver> _logger;

    public PostgresCredentialResolver(
        NpgsqlDataSource dataSource,
        SecretEncryption encryption,
        string accessorId,
        ILogger<PostgresCredentialResolver> logger)
    {
        _dataSource = dataSource;
        _encryption = encryption;
        _accessorId = accessorId;
        _logger = logger;
    }

    public Task<Credential> ResolveAsync(
        string credentialReference, CancellationToken cancellationToken) =>
        ResolveAsync(
            credentialReference,
            new CredentialAccessContext(_accessorId, TargetId: null, AuditFailureIsFatal: false),
            cancellationToken);

    public async Task<Credential> ResolveAsync(
        string credentialReference, CredentialAccessContext context, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialReference);
        ArgumentNullException.ThrowIfNull(context);

        try
        {
            var credential = await LoadAsync(credentialReference, cancellationToken)
                .ConfigureAwait(false);

            await AuditAsync(credentialReference, context, succeeded: true, reason: null, cancellationToken)
                .ConfigureAwait(false);

            return credential;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not CredentialAuditException)
        {
            // Audited before rethrowing, so a failure to resolve is never invisible. A
            // CredentialAuditException is the audit write itself failing — it is already loud and
            // must not be turned into a second (also failing) "failed" row.
            await AuditAsync(credentialReference, context, succeeded: false, ex.Message, CancellationToken.None)
                .ConfigureAwait(false);
            throw;
        }
    }

    private async Task<Credential> LoadAsync(
        string credentialReference, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT ciphertext, nonce, tag, key_id
            FROM federation.secret
            WHERE credential_reference = @Reference;
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        var row = await connection.QuerySingleOrDefaultAsync<SecretRow>(new CommandDefinition(
            sql, new { Reference = credentialReference }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        if (row is null)
        {
            throw new CredentialNotProvisionedException(
                $"No secret is stored for credential reference '{credentialReference}'. "
                + "The target is configured but its credential was never provisioned.");
        }

        if (!string.Equals(row.KeyId, _encryption.KeyId, StringComparison.Ordinal))
        {
            // Named explicitly, because the generic GCM failure below would otherwise send an
            // operator hunting a corrupted row when the real cause is an incomplete key rotation.
            throw new InvalidOperationException(
                $"Secret '{credentialReference}' is sealed under key '{row.KeyId}' but this "
                + $"process holds key '{_encryption.KeyId}'. Re-encrypt the secret, or configure "
                + "the process with the key that sealed it.");
        }

        string plaintext;
        try
        {
            plaintext = _encryption.Open(new SealedSecret(row.Ciphertext, row.Nonce, row.Tag, row.KeyId));
        }
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException(
                $"Secret '{credentialReference}' failed authenticated decryption. The key is "
                + "wrong or the stored ciphertext was altered.", ex);
        }

        return Deserialise(plaintext, credentialReference);
    }

    /// <summary>
    /// Secrets are stored as a small JSON document rather than a bare password.
    /// </summary>
    /// <remarks>
    /// Vendors need different shapes: ONVIF and ISAPI want username and password, Milestone
    /// wants an OAuth client, Genetec wants a username with an appended application id. One
    /// envelope covers all of them without a schema change per vendor.
    /// </remarks>
    private static Credential Deserialise(string plaintext, string reference)
    {
        StoredCredential? stored;
        try
        {
            stored = JsonSerializer.Deserialize<StoredCredential>(plaintext);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Secret '{reference}' decrypted successfully but is not a valid credential "
                + "document.", ex);
        }

        if (stored is null)
        {
            throw new InvalidOperationException($"Secret '{reference}' decrypted to null.");
        }

        return new Credential(stored.Username, stored.Password, stored.Token, stored.Extra);
    }

    private async Task AuditAsync(
        string credentialReference, CredentialAccessContext context,
        bool succeeded, string? reason, CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO federation.credential_access_log
                (credential_reference, accessed_by, target_id, succeeded, failure_reason)
            VALUES (@Reference, @AccessedBy, @TargetId, @Succeeded, @Reason);
            """;

        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);

            await connection.ExecuteAsync(new CommandDefinition(sql, new
            {
                Reference = credentialReference,
                AccessedBy = context.AccessedBy,
                TargetId = context.TargetId,
                Succeeded = succeeded,
                // Truncated: an exception message can be long, and the audit row records that a
                // failure happened, not a full diagnostic.
                Reason = reason?.Length > 500 ? reason[..500] : reason,
            }, cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Always loud: an audit gap is a compliance finding.
            LogAuditWriteFailed(_logger, credentialReference, ex);

            // On a disclosure path the request must fail rather than return the secret without a
            // log row. On the in-process path a failed audit must NOT block a worker connecting
            // to cameras, so it is logged and swallowed as before.
            if (context.AuditFailureIsFatal)
            {
                throw new CredentialAuditException(
                    $"Resolved credential '{credentialReference}' but could not write its access "
                    + "audit row; the secret was withheld.", ex);
            }
        }
    }

    private sealed class SecretRow
    {
        public byte[] Ciphertext { get; init; } = [];
        public byte[] Nonce { get; init; } = [];
        public byte[] Tag { get; init; } = [];
        public string KeyId { get; init; } = "";
    }

    private sealed record StoredCredential(
        string? Username, string? Password, string? Token, Dictionary<string, string>? Extra);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Failed to write credential access audit for {CredentialReference}. "
                + "The credential was still resolved; this is an audit gap.")]
    private static partial void LogAuditWriteFailed(
        ILogger logger, string credentialReference, Exception exception);
}
