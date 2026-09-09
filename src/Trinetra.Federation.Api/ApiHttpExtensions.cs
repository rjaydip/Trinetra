using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Trinetra.Federation.Api.Auth;

namespace Trinetra.Federation.Api;

internal static class ApiHttpExtensions
{
    public static IServiceCollection AddTrinetraHttpServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddHealthChecks();
        services.AddProblemDetails();

        // One shared cache: the API-key auth-path rate-limit counters and the brief grant cache
        // (finding 4-H5 / 8-NEW-H). Entries are tiny and TTL-bounded, so no SizeLimit.
        services.AddMemoryCache();
        services.AddExceptionHandler<BadRequestExceptionHandler>();
        services.AddExceptionHandler<ConstraintViolationExceptionHandler>();
        services.AddExceptionHandler<InvalidReferenceExceptionHandler>();
        services.AddExceptionHandler<UnhandledExceptionHandler>();

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy("login", context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 10,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                }));
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(
                context => RateLimitPartition.GetFixedWindowLimiter(
                    context.User.Identity?.Name
                        ?? context.Connection.RemoteIpAddress?.ToString()
                        ?? "anon",
                _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 600,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                    }));
        });

        var authOptions = configuration.GetSection(AuthOptions.SectionName).Get<AuthOptions>();
        services.AddCors(options => options.AddDefaultPolicy(policy =>
        {
            var origins = authOptions?.AllowedOrigins.ToArray() ?? [];
            if (origins.Length > 0)
            {
                policy.WithOrigins(origins)
                      .AllowAnyHeader()
                      .AllowAnyMethod()
                      .AllowCredentials()
                      // A browser fetch() can only read these response headers if they are
                      // explicitly exposed. The list endpoints put pagination metadata here
                      // (the body carries the envelope only when ?page was sent).
                      .WithExposedHeaders(
                          "X-Total-Count", "X-Result-Capped", "X-Page", "X-Page-Size");
            }
        }));

        return services;
    }
}
