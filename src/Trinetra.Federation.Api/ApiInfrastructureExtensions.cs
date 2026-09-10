using Npgsql;
using Trinetra.Federation.Adapters;
using Trinetra.Federation.Api.Auth;
using Trinetra.Federation.Core.Abstractions;
using Trinetra.Federation.Runtime;
using Trinetra.Federation.Storage;
using Trinetra.Federation.Storage.Repositories;
using Trinetra.Federation.Storage.Secrets;

namespace Trinetra.Federation.Api;

internal static class ApiInfrastructureExtensions
{
    public static IServiceCollection AddTrinetraInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddSingleton<NpgsqlDataSource>(sp =>
        {
            var connectionString = configuration.GetConnectionString("Federation")
                ?? throw new InvalidOperationException(
                    "ConnectionStrings:Federation is not configured.");
            var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
            dataSourceBuilder.UseLoggerFactory(sp.GetRequiredService<ILoggerFactory>());
            return dataSourceBuilder.Build();
        });

        services.AddSingleton(_ =>
        {
            var keyId = configuration["Secrets:KeyId"]
                ?? throw new InvalidOperationException("Secrets:KeyId is not configured.");
            var key = configuration["Secrets:Key"]
                ?? throw new InvalidOperationException(
                    "Secrets:Key is not configured. Generate one with 'openssl rand -base64 32' "
                    + "and supply it via Secrets__Key — never in appsettings.json.");
            return SecretEncryption.FromBase64Key(keyId, key);
        });

        services.AddSingleton<JwtTokenService>();
        services.AddScoped<AdminSeeder>();
        services.AddScoped<OrganizationRepository>();
        services.AddScoped<GeographyRepository>();
        services.AddScoped<UserRepository>();
        services.AddScoped<ConnectorTargetRepository>();
        services.AddScoped<CameraRepository>();
        services.AddScoped<GisQueryRepository>();
        services.AddScoped<CameraHealthRepository>();
        services.AddScoped<CameraMaintenanceRepository>();
        services.AddScoped<ReconciliationRepository>();
        services.AddScoped<FederationQueryRepository>();
        services.AddScoped<ConnectionTestRepository>();
        services.AddScoped<EventQueryRepository>();
        services.AddScoped<ApiKeyRepository>();
        services.AddScoped<RefreshTokenRepository>();
        services.AddScoped<AuthAuditRepository>();
        services.AddSingleton<MaintenanceRepository>();
        services.AddScoped<SecretWriter>();
        services.AddScoped<AccessGroupRepository>();
        services.AddScoped<RoleRepository>();
        services.AddScoped<DetectionRepository>();
        services.AddScoped<WatchlistRepository>();
        services.AddScoped<AiWorkerHealthRepository>();
        services.AddScoped<SystemInfoRepository>();

        services.AddFederationAdapters();
        services.AddSingleton<ICredentialResolver>(sp => new PostgresCredentialResolver(
            sp.GetRequiredService<NpgsqlDataSource>(),
            sp.GetRequiredService<SecretEncryption>(),
            $"api@{Environment.MachineName}",
            sp.GetRequiredService<ILogger<PostgresCredentialResolver>>()));
        services.AddSingleton<ConnectionTester>();
        services.AddHostedService<MaintenanceService>();

        return services;
    }
}
