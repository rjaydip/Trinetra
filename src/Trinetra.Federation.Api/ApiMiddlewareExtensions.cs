using Microsoft.Extensions.Options;
using Trinetra.Federation.Api.Auth;
using Trinetra.Federation.Storage;

namespace Trinetra.Federation.Api;

internal static class ApiMiddlewareExtensions
{
    public static WebApplication UseTrinetraMiddleware(this WebApplication app)
    {
        var network = app.Services.GetRequiredService<IOptions<NetworkOptions>>().Value;
        if (network.TrustForwardedHeaders)
        {
            app.UseForwardedHeaders(network.Build());
        }

        app.UseExceptionHandler();
        app.UseStatusCodePages();
        app.UseCors();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseRateLimiter();
        app.Use(HandleForbiddenAsync);

        return app;
    }

    private static async Task HandleForbiddenAsync(HttpContext context, RequestDelegate next)
    {
        try
        {
            await next(context);
        }
        catch (ForbiddenException exception)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new
            {
                type = "https://tools.ietf.org/html/rfc7231#section-6.5.3",
                title = "Forbidden",
                status = 403,
                detail = $"This action requires the '{exception.Permission}' permission.",
            });
        }
    }
}
