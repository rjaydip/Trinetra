using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Trinetra.Federation.Api.Auth;

/// <summary>
/// Declares the API's authentication schemes in the OpenAPI document.
/// </summary>
/// <remarks>
/// Without this the generated document describes endpoints but not how to authenticate to them,
/// so Swagger UI offers no Authorize button — and since every route except login and health
/// requires a token, the whole page would return 401 with no way to fix it from the UI.
/// </remarks>
internal sealed class SecuritySchemeTransformer : IOpenApiDocumentTransformer
{
    public Task TransformAsync(
        OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);

        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes = new Dictionary<string, IOpenApiSecurityScheme>
        {
            ["Bearer"] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                BearerFormat = "JWT",
                In = ParameterLocation.Header,
                Description =
                    "Paste the token from POST /api/v1/auth/login. Swagger adds the 'Bearer ' "
                    + "prefix, so enter the token value alone.",
            },
            ["ApiKey"] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.ApiKey,
                Name = ApiKeyAuthenticationHandler.HeaderName,
                In = ParameterLocation.Header,
                Description =
                    "For service integrations. People use a bearer token instead.",
            },
        };

        document.Info.Title = "Trinetra Federation API";
        document.Info.Description =
            "Model 3 — VMS federation and configuration.\n\n"
            + "Every endpoint except /health and /api/v1/auth/login requires authentication. "
            + "Log in first, then use Authorize to set the token.";

        return Task.CompletedTask;
    }
}
