using Dapper;
using Npgsql;

namespace Trinetra.Federation.Storage.Repositories;

/// <summary>The non-sensitive facts about the database an instance is connected to.</summary>
public sealed record DeploymentDbFacts(string DatabaseName, string? Host, int? Port, string ServerVersion);

/// <summary>
/// Reads the deployment facts shown on the Scalar landing page (database identity + server
/// version). The connection string is parsed here, in Storage, and only its non-sensitive parts
/// are surfaced — never the username or password.
/// </summary>
public sealed class SystemInfoRepository
{
    private readonly NpgsqlDataSource _dataSource;

    public SystemInfoRepository(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    /// <summary>Database name, configured host/port, and PostgreSQL version. Null when the database is unreachable.</summary>
    public async Task<DeploymentDbFacts?> TryGetAsync(CancellationToken ct)
    {
        var builder = new NpgsqlConnectionStringBuilder(_dataSource.ConnectionString);

        try
        {
            await using var c = await _dataSource.OpenConnectionAsync(ct);
            var row = await c.QuerySingleAsync<Row>(new CommandDefinition(
                "SELECT current_database() AS DatabaseName, version() AS ServerVersion;",
                cancellationToken: ct));

            return new DeploymentDbFacts(
                row.DatabaseName,
                string.IsNullOrEmpty(builder.Host) ? null : builder.Host,
                builder.Port == 0 ? null : builder.Port,
                row.ServerVersion);
        }
        catch (NpgsqlException)
        {
            return null;
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    private sealed record Row(string DatabaseName, string ServerVersion);
}
