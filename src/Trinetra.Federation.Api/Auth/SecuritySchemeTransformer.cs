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
/// <remarks>
/// Title and description belong to <see cref="OpenApi.TagDescriptionTransformer"/>, which runs
/// after this one. Setting <c>Info</c> in both meant whichever transformer ran last silently won,
/// and the losing text simply vanished from the page.
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
                    "**Paste the token by itself. Do not type `Bearer`.**\n\n"
                    + "Swagger adds the `Bearer ` prefix for you. Typing it as well sends "
                    + "`Authorization: Bearer Bearer <token>`, which fails validation, and every "
                    + "call comes back 401 with a token that is perfectly valid.\n\n"
                    + "Get the value from the `token` field of `POST /api/v1/auth/login`.",
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

        return Task.CompletedTask;
    }
}
