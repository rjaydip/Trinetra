using Shouldly;
using Trinetra.Federation.Storage.Repositories;

namespace Trinetra.IntegrationTests;

/// <summary>
/// The deployment facts shown on the Scalar landing page: database identity + server version,
/// read without touching any data or exposing the connection secret.
/// </summary>
public sealed class SystemInfoTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;

    public SystemInfoTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task TryGet_ReportsDatabaseNameHostPortAndServerVersion()
    {
        var facts = await new SystemInfoRepository(_fixture.DataSource).TryGetAsync(CancellationToken.None);

        facts.ShouldNotBeNull();
        facts!.DatabaseName.ShouldNotBeNullOrWhiteSpace();
        facts.Host.ShouldNotBeNullOrWhiteSpace();
        facts.Port.ShouldNotBeNull();
        facts.ServerVersion.ShouldContain("PostgreSQL");
    }

    [Fact]
    public async Task TryGet_ReturnsNullAndDoesNotThrow_WhenTheDatabaseIsUnreachable()
    {
        await using var dead = new Npgsql.NpgsqlDataSourceBuilder(
            "Host=127.0.0.1;Port=1;Username=none;Password=none;Database=none;Timeout=1;Command Timeout=1")
            .Build();

        (await new SystemInfoRepository(dead).TryGetAsync(CancellationToken.None)).ShouldBeNull();
    }
}
