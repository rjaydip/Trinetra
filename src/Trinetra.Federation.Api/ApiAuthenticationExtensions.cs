using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Trinetra.Federation.Api.Auth;

namespace Trinetra.Federation.Api;

internal static class ApiAuthenticationExtensions
{
    public static IServiceCollection AddTrinetraAuthentication(
        this IServiceCollection services)
    {
        services.AddAuthentication(options =>
            {
                options.DefaultScheme = "Trinetra";
                options.DefaultChallengeScheme = "Trinetra";
            })
            .AddPolicyScheme("Trinetra", "JWT or API key", options =>
            {
                options.ForwardDefaultSelector = context =>
                    context.Request.Headers.ContainsKey(ApiKeyAuthenticationHandler.HeaderName)
                        ? ApiKeyAuthenticationHandler.SchemeName
                        : JwtBearerDefaults.AuthenticationScheme;
            })
            .AddJwtBearer()
            .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(
                ApiKeyAuthenticationHandler.SchemeName, _ => { });

        services.ConfigureOptions<ConfigureJwtBearer>();
        services.AddAuthorization();
        return services;
    }
}
