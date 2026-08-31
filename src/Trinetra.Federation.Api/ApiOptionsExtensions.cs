using Microsoft.AspNetCore.Http.Json;
using Trinetra.Federation.Api.Auth;

namespace Trinetra.Federation.Api;

internal static class ApiOptionsExtensions
{
    public static IServiceCollection AddTrinetraOptions(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddSystemd();
        services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.RespectNullableAnnotations = true;
            options.SerializerOptions.RespectRequiredConstructorParameters = true;
        });

        services.AddOptions<NetworkOptions>()
            .Bind(configuration.GetSection(NetworkOptions.SectionName))
            .ValidateOnStart();
        services.AddOptions<AuthOptions>()
            .Bind(configuration.GetSection(AuthOptions.SectionName))
            .ValidateOnStart();
        services.AddOptions<RetentionOptions>()
            .Bind(configuration.GetSection(RetentionOptions.SectionName))
            .Validate(options => { options.Validate(); return true; })
            .ValidateOnStart();

        return services;
    }
}
