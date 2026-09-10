using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;
using Trinetra.Federation.Api.Auth;
using Trinetra.Federation.Storage.Repositories;

namespace Trinetra.Federation.Api.OpenApi;

/// <summary>
/// Appends a "This deployment" section to the document description, so the Scalar landing page
/// shows which database the instance is on, its PostgreSQL version, the allowed CORS origins and
/// the retention windows — the questions asked when confirming a deployment.
/// </summary>
/// <remarks>
/// Runs after <see cref="TagDescriptionTransformer"/>, which sets <c>Info.Description</c>. The
/// document is served anonymously at <c>/openapi/v1.json</c>, so this block is public: it carries
/// no key, credential, account or connection secret — only the facts an operator needs to tell
/// two environments apart.
/// </remarks>
internal sealed class DeploymentInfoTransformer(
    SystemInfoRepository systemInfo,
    IOptions<AuthOptions> auth,
    IOptions<RetentionOptions> retention)
    : IOpenApiDocumentTransformer
{
    public async Task TransformAsync(
        OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);

        var facts = await systemInfo.TryGetAsync(cancellationToken);
        var r = retention.Value;
        var origins = auth.Value.AllowedOrigins;

        var db = facts is null
            ? "**unreachable**"
            : $"`{facts.DatabaseName}`" + (facts.Host is null ? "" : $" on `{facts.Host}"
                + (facts.Port is { } p ? $":{p.ToString(CultureInfo.InvariantCulture)}" : "") + "`");

        var version = facts is null ? "—" : ShortVersion(facts.ServerVersion);

        var cors = origins.Count == 0
            ? "_none configured_"
            : string.Join(", ", origins.Select(o => $"`{o}`"));

        var retentionLine = !r.Enabled
            ? "_disabled_"
            : $"events {r.EventDays}d · health {r.HealthDays}d · camera-status {r.CameraStatusDays}d · "
              + $"connection-tests {r.ConnectionTestDays}d · dead-letter {r.DeadLetterDays}d · "
              + $"audit {r.AuditMonths}mo · auth-audit {r.AuthAuditMonths}mo";

        var block = new StringBuilder()
            .Append("\n\n---\n\n## This deployment\n\n")
            .Append("| | |\n|---|---|\n")
            .Append(CultureInfo.InvariantCulture, $"| Database | {db} |\n")
            .Append(CultureInfo.InvariantCulture, $"| PostgreSQL | {version} |\n")
            .Append(CultureInfo.InvariantCulture, $"| CORS origins | {cors} |\n")
            .Append(CultureInfo.InvariantCulture, $"| Retention | {retentionLine} |\n")
            .ToString();

        document.Info ??= new OpenApiInfo();
        document.Info.Description = (document.Info.Description ?? "") + block;
    }

    // "PostgreSQL 17.11 (Debian ...) on aarch64-..." -> "PostgreSQL 17.11"
    private static string ShortVersion(string full)
    {
        var cut = full.IndexOf(" (", StringComparison.Ordinal);
        return cut > 0 ? full[..cut] : full;
    }
}
