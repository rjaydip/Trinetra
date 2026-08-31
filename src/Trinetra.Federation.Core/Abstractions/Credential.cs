namespace Trinetra.Federation.Core.Abstractions;

/// <summary>
/// A resolved secret, held in memory only.
/// </summary>
/// <remarks>
/// Deliberately not a serialisable record: <see cref="ToString"/> is redacted so a secret
/// cannot reach a log line, a traceback or an event payload through ordinary string
/// interpolation — which is how credentials actually leak in practice, not through anyone
/// deciding to log them. Targets carry a <c>CredentialReference</c>; this is what the secret
/// store returns for it.
/// </remarks>
public sealed class Credential
{
    public Credential(
        string? username = null,
        string? password = null,
        string? token = null,
        IReadOnlyDictionary<string, string>? extra = null)
    {
        Username = username;
        Password = password;
        Token = token;
        Extra = extra ?? new Dictionary<string, string>(StringComparer.Ordinal);
    }

    public string? Username { get; }
    public string? Password { get; }
    public string? Token { get; }

    /// <summary>Vendor-specific extras — API keys, certificate thumbprints, tenant ids.</summary>
    public IReadOnlyDictionary<string, string> Extra { get; }

    /// <summary>Redacted by design. Never returns secret material.</summary>
    public override string ToString() => $"Credential(Username={Username}, Password=<redacted>)";
}

/// <summary>
/// Resolves a <c>CredentialReference</c> into live secret material.
/// </summary>
/// <remarks>
/// Implementations back onto Vault or, as a fallback, PostgreSQL with pgcrypto and a
/// KMS-wrapped data key. Every resolution is audit-logged: knowing which worker read which
/// credential and when is a requirement for a system holding access to an entire state's
/// camera estate.
/// </remarks>
public interface ICredentialResolver
{
    Task<Credential> ResolveAsync(string credentialReference, CancellationToken cancellationToken);

    /// <summary>
    /// Resolves a credential on behalf of a named caller, recording <paramref name="context"/> on
    /// the audit row.
    /// </summary>
    /// <remarks>
    /// The parameterless overload attributes the access to the resolving process itself, which is
    /// right for the in-process connector worker. This overload exists for the one HTTP path that
    /// hands secret material back to a caller (the AI worker's
    /// <c>GET /vms/{id}/credential/resolve</c>): the audit row must name <b>that</b> caller and the
    /// target, and — because the response body carries the plaintext — an audit-write failure has
    /// to fail the resolution rather than be swallowed. See <see cref="CredentialAccessContext"/>.
    /// </remarks>
    Task<Credential> ResolveAsync(
        string credentialReference, CredentialAccessContext context, CancellationToken cancellationToken);
}

/// <summary>
/// Who is resolving a credential, and how strict the audit is, for one call to
/// <see cref="ICredentialResolver.ResolveAsync(string, CredentialAccessContext, CancellationToken)"/>.
/// </summary>
/// <param name="AccessedBy">
/// Identity written to <c>credential_access_log.accessed_by</c> — the caller's actor string, not
/// the resolving process.
/// </param>
/// <param name="TargetId">
/// The connector target the credential belongs to, written to <c>credential_access_log.target_id</c>
/// so a resolution can be traced back to a device without joining on the reference string.
/// </param>
/// <param name="AuditFailureIsFatal">
/// When <see langword="true"/>, a failure to write the audit row throws
/// <see cref="CredentialAuditException"/> instead of being logged and ignored. Set on paths that
/// disclose the secret to a caller, where an unaudited disclosure is not acceptable; left
/// <see langword="false"/> for the in-process path, where a failed audit must not stop a worker
/// connecting to cameras.
/// </param>
public sealed record CredentialAccessContext(
    string AccessedBy, Guid? TargetId, bool AuditFailureIsFatal = false);
