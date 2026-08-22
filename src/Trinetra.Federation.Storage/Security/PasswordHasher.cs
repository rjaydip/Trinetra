using System.Security.Cryptography;
using System.Text;

namespace Trinetra.Federation.Storage.Security;

/// <summary>A stored password: hash, salt, and the cost it was produced with.</summary>
public readonly record struct PasswordHash(byte[] Hash, byte[] Salt, int Iterations, string Algorithm);

/// <summary>
/// PBKDF2-HMAC-SHA256 password hashing.
/// </summary>
/// <remarks>
/// <para>
/// PBKDF2 rather than Argon2id, despite Argon2 being the stronger modern choice. Two reasons:
/// it is in the BCL, so there is no third-party dependency to licence-review for a government
/// deployment; and it is FIPS-approved, which Argon2 currently is not. Where procurement asks
/// for FIPS-validated cryptography, Argon2 is simply not an available answer.
/// </para>
/// <para>
/// The cost is stored per row rather than fixed in code, so it can be raised over time: an old
/// password still verifies against its own iteration count and is silently rehashed at the next
/// successful login.
/// </para>
/// </remarks>
public static class PasswordHasher
{
    /// <summary>Current cost. Raise over time; existing rows keep verifying at their own cost.</summary>
    public const int DefaultIterations = 600_000;

    public const string AlgorithmName = "PBKDF2-HMAC-SHA256";

    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    /// <summary>Minimum length accepted anywhere a password is set.</summary>
    /// <remarks>
    /// Enforced here rather than at each call site so the seeded administrator, an operator
    /// rotation and an API password change cannot drift apart — the weakest path would set the
    /// real policy.
    /// </remarks>
    public const int MinimumLength = 12;

    public static PasswordHash Hash(string password, int iterations = DefaultIterations)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);

        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, HashBytes);

        return new PasswordHash(hash, salt, iterations, AlgorithmName);
    }

    /// <summary>
    /// Verifies a password against a stored hash.
    /// </summary>
    /// <remarks>
    /// Comparison is constant-time. A naive byte comparison leaks how many leading bytes matched
    /// through timing, which over enough attempts recovers the hash.
    /// </remarks>
    public static bool Verify(string password, PasswordHash stored)
    {
        if (string.IsNullOrEmpty(password))
        {
            return false;
        }

        var candidate = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), stored.Salt, stored.Iterations,
            HashAlgorithmName.SHA256, stored.Hash.Length);

        return CryptographicOperations.FixedTimeEquals(candidate, stored.Hash);
    }

    /// <summary>Whether a stored hash was produced with an outdated cost and should be rehashed.</summary>
    public static bool NeedsRehash(PasswordHash stored) =>
        stored.Iterations < DefaultIterations
        || !string.Equals(stored.Algorithm, AlgorithmName, StringComparison.Ordinal);

    /// <summary>
    /// Validates a candidate password, returning why it was rejected.
    /// </summary>
    /// <remarks>
    /// Deliberately length-based rather than composition-based. Mandatory symbol and digit rules
    /// push people toward predictable substitutions and written-down passwords; length is the
    /// property that actually resists offline attack against a PBKDF2 hash.
    /// </remarks>
    public static bool IsAcceptable(string? password, out string? reason)
    {
        if (string.IsNullOrWhiteSpace(password))
        {
            reason = "Password must not be empty.";
            return false;
        }

        if (password.Length < MinimumLength)
        {
            reason = $"Password must be at least {MinimumLength} characters.";
            return false;
        }

        reason = null;
        return true;
    }
}
