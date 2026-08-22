using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Trinetra.Federation.Api.Auth;

/// <summary>
/// Marks each protected operation as requiring a bearer token.
/// </summary>
/// <remarks>
/// Declaring the schemes in <see cref="SecuritySchemeTransformer"/> is only half the job: it
/// makes the Authorize button appear, but Swagger UI attaches the header only to operations that
/// actually reference a scheme. Without this, a user pastes a valid token, sees it accepted, and
/// then gets 401 on every call with nothing to explain it — the token was never sent.
/// </remarks>
internal sealed class SecurityRequirementTransformer : IOpenApiOperationTransformer
{
    public Task TransformAsync(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(context);

        var metadata = context.Description.ActionDescriptor.EndpointMetadata;

        // Anonymous wins, as it does in the pipeline: /health and login must not be shown as
        // requiring a token, or the docs would contradict the behaviour.
        if (metadata.OfType<IAllowAnonymous>().Any())
        {
            return Task.CompletedTask;
        }

        if (!metadata.OfType<IAuthorizeData>().Any())
        {
            return Task.CompletedTask;
        }

        operation.Security =
        [
            new OpenApiSecurityRequirement
            {
                [new OpenApiSecuritySchemeReference("Bearer", context.Document)] = [],
            },
        ];

        // A 401 response is documented on every protected operation. Otherwise the docs imply
        // authentication cannot fail, which is the one outcome a caller most needs to handle.
        operation.Responses ??= [];
        operation.Responses.TryAdd("401", new OpenApiResponse { Description = "Not authenticated" });
        operation.Responses.TryAdd("403", new OpenApiResponse { Description = "Insufficient permission or out of scope" });

        return Task.CompletedTask;
    }
}
