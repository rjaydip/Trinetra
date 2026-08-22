using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Trinetra.Federation.Api.OpenApi;

/// <summary>
/// Publishes the group descriptions from <see cref="ApiTags"/> into the document.
/// </summary>
/// <remarks>
/// <c>MapGroup(...).WithTags(...)</c> names a section but says nothing about it, so Swagger UI
/// renders eight bare headings and a reader has to infer what each group is for from the route
/// names beneath it. Declaring the tags at document level attaches a paragraph to each heading
/// and fixes their order — tags that only appear on operations are otherwise emitted in whatever
/// order the routes happened to be registered in.
/// </remarks>
internal sealed class TagDescriptionTransformer : IOpenApiDocumentTransformer
{
    public Task TransformAsync(
        OpenApiDocument document,
        OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);

        document.Info ??= new OpenApiInfo();
        document.Info.Title = "Trinetra Federation API";
        document.Info.Description =
            "The read and configuration surface of Model 3 (VMS Federation).\n\n"
            + "Every route is authenticated, scoped by organization and geography, and audited. "
            + "Each operation states the permission it requires; a caller without it gets 403, "
            + "and a resource outside their scope reads as 404 rather than 403 so that ids "
            + "cannot be probed.\n\n"
            + "**To try anything here:** call `POST /api/v1/auth/login`, copy the `token` value, "
            + "click **Authorize**, and paste the token **without** typing `Bearer` — Swagger "
            + "adds that prefix itself, and typing it too sends `Bearer Bearer <token>`, which "
            + "returns 401 on every call.\n\n"
            + "This API never carries video. Streams and snapshots appear as references only.";

        document.Tags ??= new HashSet<OpenApiTag>();
        document.Tags.Clear();

        foreach (var (name, description) in ApiTags.Ordered)
        {
            document.Tags.Add(new OpenApiTag { Name = name, Description = description });
        }

        return Task.CompletedTask;
    }
}
