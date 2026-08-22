using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Npgsql;
using Trinetra.Federation.Adapters;
using Trinetra.Federation.Api.Auth;
using Trinetra.Federation.Api.Endpoints;
using Trinetra.Federation.Core.Abstractions;
using Trinetra.Federation.Runtime;
using Trinetra.Federation.Storage;
using Trinetra.Federation.Storage.Repositories;
using Trinetra.Federation.Storage.Secrets;

// Trinetra federation API.
//
// Serves the frontend: organization and geographic hierarchy, users and access groups, VMS
// configuration, connector health, and event queries. Every route is authenticated and scoped —
// see RBAC-LOGICAL-FLOW.md and docs/ARCHITECTURE-MODEL-3.md §9.

var builder = WebApplication.CreateBuilder(args);

// Shared settings, linked into every host's output so the API, worker and CLI cannot end up
// with different encryption keys. Rooted at the OUTPUT directory rather than the content root:
// `dotnet run` sets the content root to the project folder, where this linked file does not
// exist. Added after the defaults so it wins over appsettings.json, but before environment
// variables, which still override everything for a real deployment.
builder.Configuration.AddJsonFile(
    new PhysicalFileProvider(AppContext.BaseDirectory),
    "trinetra.settings.json", optional: false, reloadOnChange: false);
builder.Configuration.AddEnvironmentVariables();

// sd_notify + journald log formatting. No-op when not running under systemd, so `dotnet run`
// on a laptop is unaffected. READY=1 is only sent once startup has completed -- which for this
// host means after the schema check and the admin seed, so systemd's idea of "started" matches
// the point at which the API can actually serve.
builder.Services.AddSystemd();

// Without these two, a request body missing a required field binds it to null and the failure
// surfaces deep in the handler as a NullReferenceException -- a 500, which reads as a server
// fault and gets escalated as an outage instead of being fixed by the caller. With them, System
// .Text.Json rejects the body during binding and the client gets a 400 naming the field.
builder.Services.ConfigureHttpJsonOptions(options =>
{
    // A non-nullable property or constructor parameter genuinely means non-nullable.
    options.SerializerOptions.RespectNullableAnnotations = true;

    // An OMITTED field is an error, not a silent default. RespectNullableAnnotations alone only
    // catches an explicit null: a field left out entirely never reaches the null check, so a
    // typo'd field name bound to null and blew up inside the handler as a 500.
    //
    // This makes every positional parameter without a C# default mandatory, which is why the
    // optional ones in Contracts.cs all carry an explicit '= null'. That is the contract stated
    // in one place: no default means required.
    options.SerializerOptions.RespectRequiredConstructorParameters = true;
});

builder.Services.AddOptions<NetworkOptions>()
    .Bind(builder.Configuration.GetSection(NetworkOptions.SectionName))
    .ValidateOnStart();

builder.Services.AddOptions<AuthOptions>()
    .Bind(builder.Configuration.GetSection(AuthOptions.SectionName))
    .ValidateOnStart();

// Validate() throws on a period that would destroy data. Retention drops partitions, so a bad
// value is not a degraded service — it is silent, permanent loss the first night it runs.
builder.Services.AddOptions<RetentionOptions>()
    .Bind(builder.Configuration.GetSection(RetentionOptions.SectionName))
    .Validate(o => { o.Validate(); return true; })
    .ValidateOnStart();

builder.Services.AddSingleton<NpgsqlDataSource>(sp =>
{
    var connectionString = builder.Configuration.GetConnectionString("Federation")
        ?? throw new InvalidOperationException("ConnectionStrings:Federation is not configured.");
    var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
    dataSourceBuilder.UseLoggerFactory(sp.GetRequiredService<ILoggerFactory>());
    return dataSourceBuilder.Build();
});

// Credential encryption. The key never reaches PostgreSQL — see SecretEncryption.
builder.Services.AddSingleton(_ =>
{
    var keyId = builder.Configuration["Secrets:KeyId"]
        ?? throw new InvalidOperationException("Secrets:KeyId is not configured.");
    var key = builder.Configuration["Secrets:Key"]
        ?? throw new InvalidOperationException(
            "Secrets:Key is not configured. Generate one with 'openssl rand -base64 32' and "
            + "supply it via Secrets__Key — never in appsettings.json.");
    return SecretEncryption.FromBase64Key(keyId, key);
});

builder.Services.AddSingleton<JwtTokenService>();
// Scoped, not singleton: it consumes scoped repositories. Registering it as a singleton
// captures a scoped service for the lifetime of the process, which Development-mode scope
// validation rejects outright — and which would leak a connection in production.
builder.Services.AddScoped<AdminSeeder>();

builder.Services.AddScoped<OrganizationRepository>();
builder.Services.AddScoped<GeographyRepository>();
builder.Services.AddScoped<UserRepository>();
builder.Services.AddScoped<ConnectorTargetRepository>();
builder.Services.AddScoped<FederationQueryRepository>();
builder.Services.AddScoped<ConnectionTestRepository>();
builder.Services.AddScoped<EventQueryRepository>();
builder.Services.AddScoped<ApiKeyRepository>();
// Singleton: the maintenance loop is a singleton BackgroundService and this
// repository holds nothing but the data source.
builder.Services.AddSingleton<MaintenanceRepository>();
builder.Services.AddScoped<SecretWriter>();
builder.Services.AddScoped<AccessGroupRepository>();

// The connection tester drives real adapters, so the API needs the adapter stack too.
builder.Services.AddFederationAdapters();
builder.Services.AddSingleton<ICredentialResolver>(sp => new PostgresCredentialResolver(
    sp.GetRequiredService<NpgsqlDataSource>(),
    sp.GetRequiredService<SecretEncryption>(),
    $"api@{Environment.MachineName}",
    sp.GetRequiredService<ILogger<PostgresCredentialResolver>>()));
builder.Services.AddSingleton<ConnectionTester>();

builder.Services.AddHostedService<MaintenanceService>();

// ---- Authentication --------------------------------------------------------
// Two schemes, selected by what the caller presented. There is no unauthenticated path other
// than login and health: "temporarily open" services have a way of reaching production.

builder.Services
    .AddAuthentication(options =>
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

// Validation parameters come from the container, not a closure — see ConfigureJwtBearer.
builder.Services.ConfigureOptions<ConfigureJwtBearer>();

builder.Services.AddAuthorization();

// ---- Cross-cutting ---------------------------------------------------------

builder.Services.AddHealthChecks();
builder.Services.AddProblemDetails();

// Registered before UseExceptionHandler runs. Without it a body that fails to bind
// returns 500 rather than the 400 the framework already decided it should be.
builder.Services.AddExceptionHandler<BadRequestExceptionHandler>();
builder.Services.AddExceptionHandler<ConstraintViolationExceptionHandler>();
builder.Services.AddOpenApi(options =>
    // Declares the bearer and API-key schemes, so Swagger UI shows an Authorize button.
    // Without it the page renders but every request comes back 401 with no way to fix it.
{
    options.AddDocumentTransformer<SecuritySchemeTransformer>();
    options.AddOperationTransformer<SecurityRequirementTransformer>();
});

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    // Login is anonymous by definition, so it partitions on source address. That is only
    // meaningful once forwarded headers are handled — see UseForwardedHeaders below, and
    // NetworkOptions for why an unvalidated proxy configuration silently disables this.
    options.AddPolicy("login", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0,
        }));

    // Partitions per authenticated caller, which requires the limiter to run AFTER
    // authentication. Running it before leaves User.Identity null on every request, so every
    // partition collapses onto the source address — and behind a proxy that is one address for
    // the whole deployment, meaning a single polling dashboard can deny service to everyone.
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(
        context => RateLimitPartition.GetFixedWindowLimiter(
            context.User.Identity?.Name
                ?? context.Connection.RemoteIpAddress?.ToString()
                ?? "anon",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 600, Window = TimeSpan.FromMinutes(1), QueueLimit = 0,
            }));
});

var authOptions = builder.Configuration.GetSection(AuthOptions.SectionName).Get<AuthOptions>();

builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
{
    var origins = authOptions?.AllowedOrigins.ToArray() ?? [];

    // Never AllowAnyOrigin: requests carry credentials, and a wildcard would let any site a
    // logged-in operator visits act as them.
    if (origins.Length > 0)
    {
        policy.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod().AllowCredentials();
    }
}));

var app = builder.Build();

// ---- Startup ---------------------------------------------------------------

// Constructed eagerly so a missing or malformed signing key stops the process here, with a
// message naming the setting. Left lazy, the JWT scheme is not built until the first request,
// so the API appears to start normally and then returns 500 on EVERY endpoint -- including
// /health, which is anonymous and gives no hint that authentication configuration is the cause.
_ = app.Services.GetRequiredService<JwtTokenService>();

// Bound to ApplicationStopping so Ctrl+C during startup work aborts rather than being swallowed.
var startupToken = app.Lifetime.ApplicationStopping;


// Seeding needs a scope of its own: there is no request in flight at startup, so one is
// created explicitly and disposed once the bootstrap account exists.
using (var startupScope = app.Services.CreateScope())
{
    await startupScope.ServiceProvider.GetRequiredService<AdminSeeder>().SeedAsync(startupToken);
}

// Forwarded headers first, before anything reads the source address. Validated at startup
// rather than trusted: enabling this without a known-proxy list lets any caller claim any
// address, which defeats rate limiting while leaving it apparently working.
var network = app.Services.GetRequiredService<IOptions<NetworkOptions>>().Value;
network.Validate(app.Environment.IsDevelopment());

// Before anything is served: a Production deployment that left a placeholder in place is running
// on a key that is in the repository, and there is no runtime symptom of it.
CommittedSecretGuard.Validate(app.Configuration, app.Environment.IsProduction());

if (network.TrustForwardedHeaders)
{
    app.UseForwardedHeaders(network.Build());
}

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseCors();

app.UseAuthentication();
app.UseAuthorization();

// AFTER authentication, so the limiter can partition per user rather than per address. The
// cost of admitting an unauthenticated request this far is one JWT validation, which is bounded;
// the cost of the alternative is the whole deployment sharing one bucket.
app.UseRateLimiter();

// Turns a repository-layer permission failure into a 403 rather than a 500. The check belongs
// in the data layer, but its result has to reach the caller as an authorization outcome.
app.Use(async (context, next) =>
{
    try
    {
        await next(context);
    }
    catch (ForbiddenException ex)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsJsonAsync(new
        {
            type = "https://tools.ietf.org/html/rfc7231#section-6.5.3",
            title = "Forbidden",
            status = 403,
            detail = $"This action requires the '{ex.Permission}' permission.",
        });
    }
});

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi().AllowAnonymous();

    // Browsable docs at /swagger, reading the document produced above.
    // Development only: the document enumerates every route and its shape, which is a useful
    // starting point for anyone probing a production deployment.
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/openapi/v1.json", "Trinetra Federation API");
        options.RoutePrefix = "swagger";
        options.DocumentTitle = "Trinetra Federation API";

        // Keeps the token across page reloads, so a login is not needed after every refresh.
        options.EnablePersistAuthorization();
    });
}

app.MapHealthChecks("/health").AllowAnonymous();

app.MapAuthEndpoints();
app.MapHierarchyEndpoints();
app.MapUserEndpoints();
app.MapAccessGroupEndpoints();
app.MapVmsEndpoints();
app.MapCredentialEndpoints();
app.MapConnectionTestEndpoints();
app.MapEventEndpoints();

// Reports any authorized route that declares no permission. A route with RequireAuthorization()
// and nothing else looks guarded in review and is reachable by every logged-in user.
app.ValidatePermissionCoverage(
    app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Trinetra.Permissions"));

app.Run();
