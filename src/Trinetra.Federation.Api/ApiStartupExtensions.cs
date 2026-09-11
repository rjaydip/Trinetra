using Microsoft.Extensions.Options;
using Trinetra.Federation.Api.Auth;
using Trinetra.Federation.Api.Endpoints;

namespace Trinetra.Federation.Api;

internal static class ApiStartupExtensions
{
    public static async Task<WebApplication> InitializeTrinetraAsync(this WebApplication app)
    {
        _ = app.Services.GetRequiredService<JwtTokenService>();

        // Resolves and creates the evidence root now, so a missing/unwritable path fails at
        // boot rather than silently on the first detection ingest (finding 15-L3).
        _ = app.Services.GetRequiredService<EvidenceStorage>();

        using var scope = app.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<AdminSeeder>()
            .SeedAsync(app.Lifetime.ApplicationStopping);

        var network = app.Services.GetRequiredService<IOptions<NetworkOptions>>().Value;
        network.Validate(app.Environment.IsDevelopment());
        CommittedSecretGuard.Validate(app.Configuration, app.Environment.IsProduction());

        return app;
    }
}
