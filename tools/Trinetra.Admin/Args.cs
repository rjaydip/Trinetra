namespace Trinetra.Admin;

/// <summary>
/// Minimal command-line parsing for <c>--flag value</c> and bare positional verbs.
/// </summary>
/// <remarks>
/// Hand-rolled rather than taking a parsing library. This tool has a handful of commands, runs
/// on operator machines and in provisioning scripts, and is exactly the kind of place where a
/// dependency needs justifying rather than assuming.
/// </remarks>
internal sealed class Args
{
    private readonly Dictionary<string, string?> _flags = new(StringComparer.OrdinalIgnoreCase);

    private Args(IReadOnlyList<string> positional) => Positional = positional;

    public IReadOnlyList<string> Positional { get; }

    public static Args Parse(string[] argv)
    {
        var positional = new List<string>();
        var args = new Args(positional);

        for (var i = 0; i < argv.Length; i++)
        {
            var token = argv[i];

            if (!token.StartsWith("--", StringComparison.Ordinal))
            {
                positional.Add(token);
                continue;
            }

            var name = token[2..];

            // A flag followed by another flag is a boolean switch, not a missing value.
            if (i + 1 < argv.Length && !argv[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                args._flags[name] = argv[++i];
            }
            else
            {
                args._flags[name] = null;
            }
        }

        return args;
    }

    public bool Has(string name) => _flags.ContainsKey(name);

    public string? Get(string name) => _flags.GetValueOrDefault(name);

    /// <summary>Reads a required flag, failing with a message an operator can act on.</summary>
    public string Require(string name)
    {
        if (!_flags.TryGetValue(name, out var value) || string.IsNullOrWhiteSpace(value))
        {
            throw new CommandFailedException($"--{name} is required.");
        }

        return value;
    }
}

/// <summary>An expected failure, reported as a message rather than a stack trace.</summary>
internal sealed class CommandFailedException : Exception
{
    public CommandFailedException(string message) : base(message) { }
}
