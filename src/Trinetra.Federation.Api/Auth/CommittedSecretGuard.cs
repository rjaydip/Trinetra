using System.Security.Cryptography;
using System.Text;

namespace Trinetra.Federation.Api.Auth;

/// <summary>
/// Refuses to run in Production with a secret that is in the repository.
/// </summary>
/// <remarks>
/// <para>
/// <c>config/trinetra.settings.json</c> is committed, and it is copied into the publish output
/// alongside the binaries. Environment variables override it — which is exactly how a deployment
/// is meant to supply real secrets — but nothing forces them to be set. Miss one line in
/// <c>/etc/trinetra/api.env</c> and the service starts, reports healthy, and runs on a key that
/// anyone with read access to the repository already has.
/// </para>
/// <para>
/// That is the worst failure this system can have and the hardest to notice, because there is no
/// symptom: every credential encrypts and decrypts correctly, every token validates. So it is
/// checked at startup, in Production only, and the process refuses to run.
/// </para>
/// <para>
/// Hashes rather than the values themselves, so tightening this file never means adding a real
/// secret to source control — and so the list can be extended for a deployment's own retired
/// keys without publishing them.
/// </para>
/// </remarks>
public static class CommittedSecretGuard
{
    // SHA-256 of the values committed in config/trinetra.settings.json.
    private static readonly Dictionary<string, string> Known = new(StringComparer.Ordinal)
    {
        ["Secrets:Key"] = "Secrets__Key",
        ["Auth:Jwt:SigningKey"] = "Auth__Jwt__SigningKey",
        ["Auth:SeedAdmin:Password"] = "Auth__SeedAdmin__Password",
    };

    public static void Validate(IConfiguration configuration, bool isProduction)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        if (!isProduction)
        {
            // Development is meant to work from a clone with no setup. That is the whole reason
            // the values are committed.
            return;
        }

        var committed = CommittedHashes();
        var offending = new List<string>();

        foreach (var (key, variable) in Known)
        {
            var value = configuration[key];

            if (!string.IsNullOrEmpty(value) && committed.Contains(Hash(value)))
            {
                offending.Add($"{key}  (set {variable})");
            }
        }

        if (offending.Count > 0)
        {
            throw new InvalidOperationException(
                "Refusing to start in Production using secrets that are committed to the "
                + "repository:\n\n    "
                + string.Join("\n    ", offending)
                + "\n\n  These are development placeholders. Anyone who can read the repository "
                + "can decrypt every stored camera password and forge a session as any user.\n\n"
                + "  Supply real values in /etc/trinetra/api.env:\n"
                + "      Secrets__Key           openssl rand -base64 32\n"
                + "      Auth__Jwt__SigningKey  openssl rand -base64 48\n\n"
                + "  Note that Secrets__Key can never be changed once credentials are stored, so "
                + "set it before provisioning anything.");
        }
    }

    /// <summary>
    /// Hashes of the committed placeholders.
    /// </summary>
    /// <remarks>
    /// Read from the shipped settings file rather than hard-coded, so this cannot drift: whatever
    /// placeholder the repository currently carries is the one refused, including one changed
    /// later without anyone remembering this check exists.
    /// </remarks>
    private static HashSet<string> CommittedHashes()
    {
        var hashes = new HashSet<string>(StringComparer.Ordinal);
        var path = Path.Combine(AppContext.BaseDirectory, "trinetra.settings.json");

        if (!File.Exists(path))
        {
            return hashes;
        }

        // Loaded in isolation: reading the shipped file directly is the point, since the merged
        // configuration is exactly what an environment variable would have already overridden.
        var shipped = new ConfigurationBuilder()
            .AddJsonFile(path, optional: true)
            .Build();

        foreach (var key in Known.Keys)
        {
            var value = shipped[key];

            if (!string.IsNullOrEmpty(value))
            {
                hashes.Add(Hash(value));
            }
        }

        return hashes;
    }

    private static string Hash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
