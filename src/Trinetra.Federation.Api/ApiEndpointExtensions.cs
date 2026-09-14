using Scalar.AspNetCore;
using Trinetra.Federation.Api.Auth;
using Trinetra.Federation.Api.Endpoints;

namespace Trinetra.Federation.Api;

internal static class ApiEndpointExtensions
{
    public static WebApplication MapTrinetraEndpoints(this WebApplication app)
    {
        // The OpenAPI document and the Scalar reference are served in every environment, and the
        // root path redirects to it — the API's front door is its own documentation. Every route
        // is still authenticated. Scalar's Authorize dialog offers an OAuth2 password flow:
        // plain username + password fields (both blank, no client id/secret) that POST to
        // /api/v1/auth/token — the same credential check as /login.
        app.MapOpenApi().AllowAnonymous();
        app.MapScalarApiReference("/scalar", options =>
        {
            options
                .WithTitle("Trinetra Federation API")
                .AddPreferredSecuritySchemes("OAuth2", "Bearer", "ApiKey")
                .EnablePersistentAuthentication()
                .WithCustomCss(ScalarCustomCss);

            // No prefilled username, password or client id — the dialog starts empty.
            options.AddPasswordFlow("OAuth2", flow => flow.ClientId = string.Empty);
        });

        app.MapGet("/", () => Results.Redirect("/scalar", permanent: false))
            .AllowAnonymous()
            .ExcludeFromDescription();

        app.MapHealthChecks("/health").AllowAnonymous();
        app.MapAuthEndpoints(enableOAuthPasswordFlow: true);
        app.MapHierarchyEndpoints();
        app.MapUserEndpoints();
        app.MapAccessGroupEndpoints();
        app.MapRoleEndpoints();
        app.MapVmsEndpoints();
        app.MapCameraEndpoints();
        app.MapCameraConnectionTestEndpoints();
        app.MapCameraHealthEndpoints();
        app.MapCameraReconciliationEndpoints();
        app.MapGisEndpoints();
        app.MapCredentialEndpoints();
        app.MapCameraCredentialEndpoints();
        app.MapSavedCredentialEndpoints();
        app.MapCameraCredentialTestEndpoints();
        app.MapConnectionTestEndpoints();
        app.MapEventEndpoints();
        app.MapCorrelationEndpoints();
        app.MapDetectionEndpoints();
        app.MapWatchlistEndpoints();
        app.MapWorkerHealthEndpoints();
        app.MapApiKeyEndpoints();
        app.MapAuthAuditEndpoints();
        app.MapVideoWallEndpoints();
        app.MapStreamSessionEndpoints();

        app.ValidatePermissionCoverage(
            app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Trinetra.Permissions"));

        return app;
    }

    // Scalar renders responses in a CodeMirror editor with line-wrapping on, so wide JSON rows
    // wrap and can't be scrolled. Turn wrapping off and let the CodeMirror scroller scroll.
    // Selectors are unscoped on purpose — the Scalar page is the only thing at this origin, and
    // CodeMirror's own stylesheet is what we're overriding.
    private const string ScalarCustomCss = """
        .cm-content,
        .cm-line,
        .cm-lineWrapping {
            white-space: pre !important;
            overflow-wrap: normal !important;
            word-break: normal !important;
        }
        .cm-editor,
        .cm-scroller {
            overflow-x: auto !important;
        }
        .scalar-codeblock-pre {
            overflow-x: auto !important;
            white-space: pre !important;
        }
        """;
}
