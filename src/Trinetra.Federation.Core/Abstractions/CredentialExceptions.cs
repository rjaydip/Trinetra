namespace Trinetra.Federation.Core.Abstractions;

/// <summary>
/// Thrown when a connector target is configured but no secret has ever been stored under its
/// <c>credential_reference</c>.
/// </summary>
/// <remarks>
/// Distinct from the other resolution failures (wrong key, altered ciphertext, malformed
/// document), which are genuine faults. "Never provisioned" is an ordinary onboarding state — a
/// target created but not yet given a credential — and an HTTP caller should see a 404 for it,
/// mirroring <c>GET /vms/{id}/credential/status</c>, rather than a 500. Derives from
/// <see cref="InvalidOperationException"/> so existing <c>catch</c> sites that do not care about
/// the distinction still handle it.
/// </remarks>
public sealed class CredentialNotProvisionedException : InvalidOperationException
{
    public CredentialNotProvisionedException() { }

    public CredentialNotProvisionedException(string message) : base(message) { }

    public CredentialNotProvisionedException(string message, Exception innerException)
        : base(message, innerException) { }
}

/// <summary>
/// Thrown when a credential was resolved but its access-audit row could not be written, on a
/// path where <see cref="CredentialAccessContext.AuditFailureIsFatal"/> is set.
/// </summary>
/// <remarks>
/// The secret is deliberately <b>not</b> returned in this case: a path that hands plaintext to a
/// caller must not do so without a log row. Distinct type so the caller can map it to a 503
/// rather than a 500 — the request can simply be retried once the audit store is reachable.
/// </remarks>
public sealed class CredentialAuditException : Exception
{
    public CredentialAuditException() { }

    public CredentialAuditException(string message) : base(message) { }

    public CredentialAuditException(string message, Exception innerException)
        : base(message, innerException) { }
}
