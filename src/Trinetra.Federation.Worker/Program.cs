using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using Npgsql;
using Trinetra.Federation.Adapters;
using Trinetra.Federation.Core.Abstractions;
using Trinetra.Federation.Runtime;
using Trinetra.Federation.Storage;
using Trinetra.Federation.Storage.Secrets;
using Trinetra.Federation.Worker;

// Connector worker host.
//
// Runs under systemd on bare metal. Horizontal scale means running more of these: each claims a
// bounded share of connector targets via the PostgreSQL lease, and the fleet rebalances itself
// when one is added or dies. No orchestrator, no coordinator. See docs/ARCHITECTURE-MODEL-3.md §4.

var builder = Host.CreateApplicationBuilder(args);

// Under systemd this switches logging to the journal's own format (no timestamps or colour --
// journald adds those) and, with Type=notify, sends sd_notify READY=1 once every hosted service
// has started, plus STOPPING=1 on shutdown.
//
// READY is what makes a rolling restart safe: systemd waits for this worker to be genuinely up
// before the deployment tool moves to the next host. Without it "started" means "the process
// exists", and a fleet can be restarted faster than it can claim leases.
builder.Services.AddSystemd();

// Shared settings, linked into every host's output so the API, worker and CLI cannot end up
// with different encryption keys. Rooted at the OUTPUT directory rather than the content root:
// `dotnet run` sets the content root to the project folder, where this linked file does not
// exist. Added after the defaults so it wins over appsettings.json, but before environment
// variables, which still override everything for a real deployment.
builder.Configuration.AddJsonFile(
    new PhysicalFileProvider(AppContext.BaseDirectory),
    "trinetra.settings.json", optional: false, reloadOnChange: false);
builder.Configuration.AddEnvironmentVariables();

builder.Services.AddOptions<WorkerOptions>()
    .Bind(builder.Configuration.GetSection(WorkerOptions.SectionName))
    .ValidateDataAnnotations()
    .Validate(o =>
    {
        o.Validate();
        return true;
    })
    // Fail at startup rather than on first claim: a misconfigured worker that starts happily and
    // then quietly claims nothing is far harder to notice than one that refuses to boot.
    .ValidateOnStart();

builder.Services.AddSingleton<NpgsqlDataSource>(sp =>
{
    // Taken from the shared ConnectionStrings section rather than a worker-specific copy, so
    // every host reaches the same database by the same definition.
    var connectionString = builder.Configuration.GetConnectionString("Federation")
        ?? throw new InvalidOperationException("ConnectionStrings:Federation is not configured.");
    var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
    dataSourceBuilder.UseLoggerFactory(sp.GetRequiredService<ILoggerFactory>());
    return dataSourceBuilder.Build();
});

// Credential encryption. The key never reaches PostgreSQL — see SecretEncryption.
builder.Services.AddSingleton(sp =>
{
    var keyId = builder.Configuration["Secrets:KeyId"]
        ?? throw new InvalidOperationException(
            "Secrets:KeyId is not configured. It records which key sealed each secret and is "
            + "what makes key rotation possible without a flag day.");

    var key = builder.Configuration["Secrets:Key"]
        ?? throw new InvalidOperationException(
            "Secrets:Key is not configured. Generate one with: openssl rand -base64 32 "
            + "and supply it via environment or a secret file — never in appsettings.json.");

    return SecretEncryption.FromBase64Key(keyId, key);
});

builder.Services.AddSingleton<ICredentialResolver>(sp => new PostgresCredentialResolver(
    sp.GetRequiredService<NpgsqlDataSource>(),
    sp.GetRequiredService<SecretEncryption>(),
    sp.GetRequiredService<IOptions<WorkerOptions>>().Value.WorkerId,
    sp.GetRequiredService<ILogger<PostgresCredentialResolver>>()));

builder.Services.AddSingleton<LeaseStore>();
builder.Services.AddSingleton<EventStore>();
builder.Services.AddSingleton<ConnectorStateStore>();

builder.Services.AddFederationAdapters();

// LeaseManager decides what this process owns; ConnectorSupervisor makes that real. Registered
// as a singleton as well as a hosted service so the supervisor can subscribe to its events.
builder.Services.AddSingleton<LeaseManager>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<LeaseManager>());
builder.Services.AddHostedService<ConnectorSupervisor>();

// Day-one correlation: windowed SQL polling over federation_event, behind the ICorrelationEngine
// port. See ARCHITECTURE-MODEL-3.md §8 -- NOT a Kafka consumer, Trinetra.Federation.Bus is
// untouched.
builder.Services.AddOptions<CorrelationRunnerOptions>()
    .Bind(builder.Configuration.GetSection(CorrelationRunnerOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<ICorrelationEngine, SqlCorrelationEngine>();
builder.Services.AddHostedService<CorrelationRunner>();

var host = builder.Build();

// Workers never apply migrations either, for the same reason as the API and more of it: there
// are hundreds of them, and a fleet restart would have hundreds of processes queued on an
// advisory lock at exactly the moment the estate needs them polling. The schema is prepared
// once by ./db/setup.sh before anything is started.
//

await host.RunAsync();
