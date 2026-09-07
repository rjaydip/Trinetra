using Scalar.AspNetCore;
using Trinetra.Federation.Api.Auth;
using Trinetra.Federation.Api.Endpoints;

namespace Trinetra.Federation.Api;

internal static class ApiEndpointExtensions
{
    public static WebApplication MapTrinetraEndpoints(this WebApplication app)
    {
        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi().AllowAnonymous();
            app.MapScalarApiReference("/scalar", options => options
                .WithTitle("Trinetra Federation API")
                .AddPreferredSecuritySchemes("OAuth2", "Bearer", "ApiKey")
                .EnablePersistentAuthentication()
                .WithCustomCss(ScalarCustomCss));
        }

        app.MapHealthChecks("/health").AllowAnonymous();
        app.MapAuthEndpoints(app.Environment.IsDevelopment());
        app.MapHierarchyEndpoints();
        app.MapUserEndpoints();
        app.MapAccessGroupEndpoints();
        app.MapVmsEndpoints();
        app.MapCameraEndpoints();
        app.MapCameraHealthEndpoints();
        app.MapCameraReconciliationEndpoints();
        app.MapGisEndpoints();
        app.MapCredentialEndpoints();
        app.MapConnectionTestEndpoints();
        app.MapEventEndpoints();
        app.MapDetectionEndpoints();
        app.MapWatchlistEndpoints();
        app.MapWorkerHealthEndpoints();
        app.MapApiKeyEndpoints();
        app.MapAuthAuditEndpoints();

        app.ValidatePermissionCoverage(
            app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Trinetra.Permissions"));

        return app;
    }

    // Scalar renders responses in a CodeMirror editor with line-wrapping on, so wide JSON rows
    // wrap and can't be scrolled. Turn wrapping off and let the CodeMirror scroller scroll.
    // Selectors are unscoped on purpose — this is the dev-only docs page, and CodeMirror's own
    // stylesheet is what we're overriding. Development-only, like the page itself.
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
