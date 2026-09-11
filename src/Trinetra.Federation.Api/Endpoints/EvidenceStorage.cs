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

    public EvidenceStorage(IConfiguration configuration)
    {
        Root = Path.GetFullPath(configuration["Evidence:RootPath"] ?? "evidence");
        Directory.CreateDirectory(Root);
    }
}
