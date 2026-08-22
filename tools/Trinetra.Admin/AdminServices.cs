using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Trinetra.Federation.Adapters;
using Trinetra.Federation.Adapters.Http;
using Trinetra.Federation.Core.Abstractions;
using Trinetra.Federation.Storage.Repositories;
using Trinetra.Federation.Storage.Secrets;

namespace Trinetra.Admin;

/// <summary>
/// Wiring for the CLI. Deliberately hand-built rather than a DI container: this is a
/// short-lived process with a handful of services, and an explicit graph is easier to follow.
/// </summary>
internal static class AdminServices
{
    private static SecretEncryption? _encryption;

    public static IConfiguration BuildConfiguration() =>
        new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            // The same shared file the API and worker read, so a credential written by the CLI
            // is readable by a worker and vice versa.
            .AddJsonFile("trinetra.settings.json", optional: false)
            // Environment last so a host can override without editing files — and so the
            // encryption key can be supplied without ever landing on disk.
            .AddEnvironmentVariables()
            .Build();

    public static NpgsqlDataSource CreateDataSource(IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Federation")
            ?? configuration["Worker:ConnectionString"]
            ?? throw new CommandFailedException(
                "No database connection configured. Set ConnectionStrings__Federation.");

        return new NpgsqlDataSourceBuilder(connectionString).Build();
    }

    public static SecretEncryption CreateEncryption(IConfiguration configuration)
    {
        var keyId = configuration["Secrets:KeyId"]
            ?? throw new CommandFailedException(
                "Secrets:KeyId is not set. It records which key sealed each secret, which is what "
                + "makes key rotation possible without a flag day.");

        var key = configuration["Secrets:Key"]
            ?? throw new CommandFailedException(
                "Secrets:Key is not set. Generate one with: trinetra-admin key generate");

        return _encryption = SecretEncryption.FromBase64Key(keyId, key);
    }

    /// <summary>
    /// Builds a resolver reusing the already-configured encryption.
    /// </summary>
    /// <remarks>
    /// Called from the stream probe, which needs the same credential the adapter used. Reusing
    /// the configured key avoids a second read of the environment and keeps a single point where
    /// key material enters the process.
    /// </remarks>
    public static ICredentialResolver CreateCredentialResolver(NpgsqlDataSource dataSource) =>
        new PostgresCredentialResolver(
            dataSource,
            _encryption ?? throw new InvalidOperationException(
                "CreateEncryption must run before a credential resolver is built."),
            $"admin-cli@{Environment.MachineName}",
            NullLogger<PostgresCredentialResolver>.Instance);

    // Repositories, shared with the API so both surfaces agree on what valid configuration is.
    public static OrganizationRepository Organizations(NpgsqlDataSource db) => new(db);

    public static GeographyRepository Geography(NpgsqlDataSource db) => new(db);

    public static UserRepository Users(NpgsqlDataSource db) => new(db);

    public static AccessGroupRepository Groups(NpgsqlDataSource db) => new(db);

    public static ConnectorTargetRepository Targets(NpgsqlDataSource db) => new(db);

    public static SecretWriter Secrets(NpgsqlDataSource db) => new(
        db,
        _encryption ?? throw new InvalidOperationException(
            "CreateEncryption must run before a secret writer is built."));

    public static IAdapterFactory CreateAdapterFactory(
        NpgsqlDataSource dataSource, ILoggerFactory loggerFactory) =>
        new AdapterRegistry(
            new TargetHttpClientProvider(
                new DigestAuthHandler(),
                new DigestAuthHandler(),
                loggerFactory.CreateLogger<TargetHttpClientProvider>()),
            CreateCredentialResolver(dataSource),
            loggerFactory);

    /// <summary>
    /// Logging for the CLI. Quiet by default so command output stays readable; <c>--verbose</c>
    /// surfaces the adapter's own diagnostics, which is what you want when a device misbehaves.
    /// </summary>
    public static ILoggerFactory CreateLoggerFactory(bool verbose) =>
        LoggerFactory.Create(builder => builder
            .AddSimpleConsole(o => o.SingleLine = true)
            .SetMinimumLevel(verbose ? LogLevel.Debug : LogLevel.Warning));
}
