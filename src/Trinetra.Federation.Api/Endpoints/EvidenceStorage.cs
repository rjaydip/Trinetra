namespace Trinetra.Federation.Api.Endpoints;

/// <summary>
/// The resolved evidence root — computed and created once at startup rather than on every
/// detection ingest (finding 15-L3: a per-request <c>Directory.CreateDirectory</c> call is
/// needless I/O on the hot path, and it silently masked a root that is missing or unwritable
/// until the first snapshot arrived at runtime instead of at boot).
/// </summary>
internal sealed class EvidenceStorage
{
    public string Root { get; }

    /// <summary>Extra roots a detection's worker-supplied <c>snapshot_reference</c> is allowed
    /// to resolve under, beyond <see cref="Root"/> itself — see
    /// <c>trinetra.settings.example.json</c>'s <c>Evidence</c> section for why this is an
    /// explicit allow-list rather than trusting that string outright. Each entry is canonicalized
    /// once at startup, the same way <see cref="Root"/> is.</summary>
    public IReadOnlyList<string> AllowedExternalRoots { get; }

    public EvidenceStorage(IConfiguration configuration)
    {
        Root = Path.GetFullPath(configuration["Evidence:RootPath"] ?? "evidence");
        Directory.CreateDirectory(Root);

        AllowedExternalRoots = configuration.GetSection("Evidence:AllowedExternalRoots")
            .Get<string[]>()?.Select(Path.GetFullPath).ToArray() ?? [];
    }

    /// <summary>Whether <paramref name="candidatePath"/> — already canonicalized by the caller —
    /// lies under <see cref="Root"/> or one of <see cref="AllowedExternalRoots"/>. The one gate
    /// standing between a detection's worker-supplied snapshot reference and an arbitrary local
    /// file read; see <see cref="DetectionEndpoints"/>'s evidence route.</summary>
    public bool IsPathAllowed(string candidatePath) =>
        IsUnderRoot(candidatePath, Root) || AllowedExternalRoots.Any(root => IsUnderRoot(candidatePath, root));

    private static bool IsUnderRoot(string candidatePath, string root) =>
        candidatePath.Equals(root, StringComparison.Ordinal)
        || candidatePath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal);
}
