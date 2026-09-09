using System.Text.Json;
using Dapper;
using Npgsql;
using Trinetra.Federation.Storage.Secrets;

namespace Trinetra.Federation.Storage.Repositories;

/// <summary>
/// Writes device credentials. There is deliberately no read path here.
/// </summary>
/// <remarks>
/// <para>
/// Reading is <see cref="PostgresCredentialResolver"/>'s job and is reachable only from a
/// connector worker resolving a credential to connect. Splitting write from read means no API
/// surface can accidentally expose one: there is simply no method on this type that returns
/// secret material.
/// </para>
/// <para>
/// The audit records <b>that</b> a credential changed, never what it changed to.
/// </para>
/// </remarks>
// CA1822 fires on the write methods now that they take their connection from the UnitOfWork
// rather than the data source. That is the point of the change, not a defect: they stay instance
// methods so one entity's operations are called the same way regardless of which need the pool.
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Performance", "CA1822:Mark members as static",
    Justification = "Write methods take their connection from the UnitOfWork by design.",
    Scope = "type")]
public sealed class SecretWriter
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly SecretEncryption _encryption;

    public SecretWriter(NpgsqlDataSource dataSource, SecretEncryption encryption)
    {
        _dataSource = dataSource;
        _encryption = encryption;
    }

    /// <summary>Stores a credential under a reference, replacing any existing value.</summary>
    public async Task WriteAsync(
        string credentialReference,
        string? username,
        string? password,
        string? token,
        string? description,
        CallerContext caller, UnitOfWork work, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialReference);
        ArgumentNullException.ThrowIfNull(caller);

        // Separately gated from ordinary configuration writing: this is the highest-privilege
        // action in the system, and most clients that legitimately edit a target have no
        // business setting the password it connects with.
        caller.Require("credential.write");

        // Stored as a small document rather than a bare password: ONVIF and ISAPI want a
        // username and password, Milestone an OAuth client, Genetec a username with an appended
        // application id. One envelope covers all of them without a schema change per vendor.
        var payload = JsonSerializer.Serialize(new { Username = username, Password = password, Token = token });
        var sealedSecret = _encryption.Seal(payload);

        var c = work.Connection;

        await c.ExecuteAsync(new CommandDefinition("""
            INSERT INTO federation.secret
                (credential_reference, ciphertext, nonce, tag, key_id, description)
            VALUES (@credentialReference, @Ciphertext, @Nonce, @Tag, @KeyId, @description)
            ON CONFLICT (credential_reference) DO UPDATE
            SET ciphertext = EXCLUDED.ciphertext, nonce = EXCLUDED.nonce, tag = EXCLUDED.tag,
                key_id = EXCLUDED.key_id,
                description = COALESCE(EXCLUDED.description, federation.secret.description),
                updated_at = now(), rotated_at = now();
            """, new
        {
            credentialReference, description,
            sealedSecret.Ciphertext, sealedSecret.Nonce, sealedSecret.Tag, sealedSecret.KeyId,
        }, work.Transaction, cancellationToken: ct));

        // The highest-privilege action lands in the MAIN config-audit trail, through the same
        // UnitOfWork path (and JSON shape) as every other mutation — not a hand-rolled INSERT.
        // Only non-secret facts: a before/after carrying the value would put every camera
        // password in the audit log in plaintext.
        await work.AuditAsync(caller, "credential_set", "secret", credentialReference,
            before: null,
            after: new
            {
                username,
                hasPassword = !string.IsNullOrEmpty(password),
                hasToken = !string.IsNullOrEmpty(token),
            },
            organizationUnitId: null, ct);
    }

    /// <summary>Whether a credential exists. Reports presence only, never content.</summary>
    public async Task<bool> ExistsAsync(string credentialReference, CancellationToken ct)
    {
        await using var c = await _dataSource.OpenConnectionAsync(ct);
        return await c.ExecuteScalarAsync<bool>(new CommandDefinition("""
            SELECT EXISTS (SELECT 1 FROM federation.secret
                           WHERE credential_reference = @credentialReference);
            """, new { credentialReference }, cancellationToken: ct));
    }
}
