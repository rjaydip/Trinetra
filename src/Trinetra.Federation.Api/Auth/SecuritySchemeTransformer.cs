using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Trinetra.Federation.Api.Auth;

/// <summary>
/// Declares the API's authentication schemes in the OpenAPI document.
/// </summary>
/// <remarks>
/// Without this the generated document describes endpoints but not how to authenticate to them,
/// so Scalar offers no Authorize button — and since every route except login and health
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
        var schemes = new Dictionary<string, IOpenApiSecurityScheme>
        {
            ["Bearer"] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                BearerFormat = "JWT",
                In = ParameterLocation.Header,
                Description =
                    "**Paste the token by itself. Do not type `Bearer`.**\n\n"
                    + "Scalar adds the `Bearer ` prefix for you. Typing it as well sends "
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

        // Gives Scalar's Authorize dialog plain username / password fields. It POSTs them to
        // /api/v1/auth/token — the same credential check as /login, rate limited the same way,
        // access-token only — and applies the returned token to every request. No client id or
        // secret; both fields start blank.
        schemes["OAuth2"] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.OAuth2,
            Description =
                "Enter your Trinetra username and password; Scalar fetches a token and applies "
                + "it to every request. No client id or secret. For a real integration use an "
                + "API key header instead.",
            Flows = new OpenApiOAuthFlows
            {
                Password = new OpenApiOAuthFlow
                {
                    TokenUrl = new Uri("/api/v1/auth/token", UriKind.Relative),
                    Scopes = new Dictionary<string, string>(StringComparer.Ordinal),
                },
            },
        };

        document.Components.SecuritySchemes = schemes;

        return Task.CompletedTask;
    }
}
