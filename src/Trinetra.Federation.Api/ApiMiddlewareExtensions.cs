using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Metadata;
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
        app.Use(ApplyRequestSizeLimitAsync);
        app.UseCors();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseRateLimiter();
        app.Use(HandleForbiddenAsync);

        return app;
    }

    /// <summary>
    /// Honours <see cref="IRequestSizeLimitMetadata"/> on the matched endpoint for minimal APIs
    /// (this host has no MVC and so no <c>RequestSizeLimitMiddleware</c>). Runs after routing —
    /// <c>WebApplication</c> adds that first — and before the body is read, so a route that
    /// declares <c>RequestSizeLimitAttribute</c> gets a tighter cap than Kestrel's global 30 MB.
    /// </summary>
    private static async Task ApplyRequestSizeLimitAsync(HttpContext context, RequestDelegate next)
    {
        var limit = context.GetEndpoint()?.Metadata
            .GetMetadata<IRequestSizeLimitMetadata>()?.MaxRequestBodySize;

        if (limit is not null
            && context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } feature)
        {
            feature.MaxRequestBodySize = limit;
        }

        await next(context);
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
