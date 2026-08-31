using Microsoft.Extensions.Options;
using Trinetra.Federation.Api.Auth;

namespace Trinetra.Federation.Api;

internal static class ApiStartupExtensions
{
    public static async Task<WebApplication> InitializeTrinetraAsync(this WebApplication app)
    {
        _ = app.Services.GetRequiredService<JwtTokenService>();

        using var scope = app.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<AdminSeeder>()
            .SeedAsync(app.Lifetime.ApplicationStopping);

        var network = app.Services.GetRequiredService<IOptions<NetworkOptions>>().Value;
        network.Validate(app.Environment.IsDevelopment());
        CommittedSecretGuard.Validate(app.Configuration, app.Environment.IsProduction());

        return app;
    }
}
