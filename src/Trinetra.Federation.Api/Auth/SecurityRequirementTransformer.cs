using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Trinetra.Federation.Api.Auth;

/// <summary>
/// Marks each protected operation as requiring either a bearer token or an API key.
/// </summary>
/// <remarks>
/// Declaring the schemes in <see cref="SecuritySchemeTransformer"/> is only half the job: it
/// makes the Authorize button appear, but Scalar attaches the header only to operations that
/// actually reference a scheme. Without this, a user pastes a valid token, sees it accepted, and
/// then gets 401 on every call with nothing to explain it — the token was never sent.
/// </remarks>
internal sealed class SecurityRequirementTransformer(IWebHostEnvironment environment)
    : IOpenApiOperationTransformer
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

        // Separate entries are an OpenAPI OR: Scalar can send either Authorization: Bearer or
        // X-Api-Key, matching the policy scheme selected by the request headers.
        operation.Security =
        [
            new OpenApiSecurityRequirement
            {
                [new OpenApiSecuritySchemeReference("Bearer", context.Document)] = [],
            },
            new OpenApiSecurityRequirement
            {
                [new OpenApiSecuritySchemeReference("ApiKey", context.Document)] = [],
            },
        ];

        // In Development the OAuth2 password flow is also on offer; list it as a third
        // alternative so Scalar attaches the token it fetched to this operation.
        if (environment.IsDevelopment())
        {
            operation.Security.Add(new OpenApiSecurityRequirement
            {
                [new OpenApiSecuritySchemeReference("OAuth2", context.Document)] = [],
            });
        }

        // A 401 response is documented on every protected operation. Otherwise the docs imply
        // authentication cannot fail, which is the one outcome a caller most needs to handle.
        operation.Responses ??= [];
        operation.Responses.TryAdd("401", new OpenApiResponse { Description = "Not authenticated" });
        operation.Responses.TryAdd("403", new OpenApiResponse { Description = "Insufficient permission or out of scope" });

        return Task.CompletedTask;
    }
}
