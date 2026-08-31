using Trinetra.Federation.Api;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddTrinetraConfiguration();
builder.Services
    .AddTrinetraOptions(builder.Configuration)
    .AddTrinetraInfrastructure(builder.Configuration)
    .AddTrinetraAuthentication()
    .AddTrinetraHttpServices(builder.Configuration)
    .AddTrinetraOpenApi();

var app = builder.Build();

await app.InitializeTrinetraAsync();
app.UseTrinetraMiddleware();
app.MapTrinetraEndpoints();

app.Run();
