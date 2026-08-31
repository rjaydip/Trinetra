using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Trinetra.Federation.Api.OpenApi;

/// <summary>Documents the global fallback response applied by the exception pipeline.</summary>
internal sealed class ProblemDetailsDocumentationTransformer : IOpenApiOperationTransformer
{
    public Task TransformAsync(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);

        operation.Responses ??= [];
        operation.Responses.TryAdd(
            "500",
            new OpenApiResponse
            {
                Description = "Unexpected server error returned as ProblemDetails.",
            });

        return Task.CompletedTask;
    }
}
