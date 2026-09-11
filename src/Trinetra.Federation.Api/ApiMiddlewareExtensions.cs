using Microsoft.AspNetCore.Authorization;
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
        app.Use(EnforceMustChangePasswordAsync);
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

    /// <summary>
    /// Finding 4-M1 / 5-L5: <c>mustChangePassword</c> was minted onto the token and read back by
    /// <see cref="CallerContextFactory.MustChangePassword"/>, but nothing ever checked it — a
    /// flagged user (post-reset, post-compromise) authenticated normally against every endpoint.
    /// Runs after <c>UseAuthorization</c> so a caller who fails authentication/authorization
    /// outright still gets that more specific reason. <c>RequirePermission</c>'s own check is a
    /// later endpoint filter, not part of <c>UseAuthorization</c> — a caller who is both flagged
    /// and lacking the route's permission gets "password change required" here first, which is
    /// the right order (rotate the password before anything leaks about what the route needs).
    /// Only blocks routes that neither opted out
    /// (<see cref="PermissionEndpoints.AllowWhileMustChangePassword"/> — changing the password
    /// itself, logging out) nor are anonymous to begin with.
    /// </summary>
    private static async Task EnforceMustChangePasswordAsync(HttpContext context, RequestDelegate next)
    {
        var endpoint = context.GetEndpoint();

        if (endpoint?.Metadata.GetMetadata<IAuthorizeData>() is not null
            && endpoint.Metadata.GetMetadata<AllowWhileMustChangePasswordMetadata>() is null
            && CallerContextFactory.MustChangePassword(context))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new
            {
                type = "https://tools.ietf.org/html/rfc7231#section-6.5.3",
                title = "Password change required",
                status = 403,
                detail = "This account must change its password before using the API further. "
                       + "Call POST /api/v1/auth/password.",
            });
            return;
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
