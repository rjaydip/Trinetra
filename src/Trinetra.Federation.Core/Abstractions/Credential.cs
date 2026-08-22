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
}
