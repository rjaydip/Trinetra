using Microsoft.Extensions.FileProviders;

namespace Trinetra.Federation.Api;

internal static class ApiConfigurationExtensions
{
    public static IConfigurationBuilder AddTrinetraConfiguration(
        this IConfigurationBuilder configuration)
    {
        return configuration
            .AddJsonFile(
                new PhysicalFileProvider(AppContext.BaseDirectory),
                "trinetra.settings.json",
                optional: false,
                reloadOnChange: false)
            .AddEnvironmentVariables();
    }
}
