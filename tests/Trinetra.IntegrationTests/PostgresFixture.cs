using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Trinetra.IntegrationTests;

/// <summary>
/// A real PostgreSQL + PostGIS instance for the test class, migrated to current schema.
/// </summary>
/// <remarks>
/// Deliberately a real database rather than an in-memory substitute. Everything worth testing
/// here — <c>SKIP LOCKED</c> claim semantics, RANGE partition routing, <c>NULLS DISTINCT</c>
/// dedup behaviour, PL/pgSQL partition maintenance — exists only in PostgreSQL. A fake would
/// assert our assumptions back at us, which is exactly the failure this suite exists to catch.
/// </remarks>
public sealed class PostgresFixture : IAsyncLifetime
{
    /// <summary>
    /// Multi-arch PostGIS build. The official <c>postgis/postgis</c> images publish no arm64
    /// manifest, so they cannot run on Apple Silicon developer machines; this one covers both
    /// amd64 and arm64 so local runs and CI use an identical image.
    /// </summary>
    private const string PostgisImage = "imresamu/postgis:17-3.5";

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder(PostgisImage)
        .WithDatabase("trinetra")
        .WithUsername("trinetra")
        .WithPassword("trinetra")
        .Build();

    public NpgsqlDataSource DataSource { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        DataSource = new NpgsqlDataSourceBuilder(_container.GetConnectionString()).Build();

        // Exactly what a deployment applies, in the same order: every version file, ascending.
        // Building the test database any other way would mean the suite validates a schema
        // nobody actually runs -- and would not notice a new version file that fails to apply.
        await using var connection = await DataSource.OpenConnectionAsync(CancellationToken.None);

        foreach (var file in VersionFiles())
        {
            await using var command = connection.CreateCommand();
            command.CommandText = await File.ReadAllTextAsync(file, CancellationToken.None);
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }

    /// <summary>Every db/versions/*.sql, in the order a deployment applies them.</summary>
    /// <remarks>
    /// Sorted by parsed version, not by string: an ordinal sort of the file names puts
    /// <c>v1.1.sql</c> before <c>v1.sql</c> ('1' &lt; 's'), which would apply an ALTER before the
    /// CREATE it depends on. Located by walking up from the test binary because the working
    /// directory differs between `dotnet test` and an IDE runner.
    /// </remarks>
    private static string[] VersionFiles()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null)
        {
            var versions = Path.Combine(dir.FullName, "db", "versions");

            if (Directory.Exists(versions))
            {
                var files = Directory.GetFiles(versions, "*.sql");

                if (files.Length == 0)
                {
                    throw new InvalidOperationException($"No .sql files in {versions}.");
                }

                Array.Sort(files, static (a, b) =>
                    ParseVersion(a).CompareTo(ParseVersion(b)));

                return files;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("db/versions was not found.");
    }

    /// <summary>
    /// "v1.sql" -&gt; (1, 0), "v1.2.sql" -&gt; (1, 2). A name that does not parse sorts last, so a
    /// stray file is obvious rather than silently reordering the real versions.
    /// </summary>
    private static (int Major, int Minor) ParseVersion(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);

        if (name.Length < 2 || name[0] is not ('v' or 'V'))
        {
            return (int.MaxValue, int.MaxValue);
        }

        var parts = name[1..].Split('.');

        if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major))
        {
            return (int.MaxValue, int.MaxValue);
        }

        var minor = 0;
        if (parts.Length > 1
            && !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out minor))
        {
            return (int.MaxValue, int.MaxValue);
        }

        return (major, minor);
    }

    public async Task DisposeAsync()
    {
        if (DataSource is not null)
        {
            await DataSource.DisposeAsync();
        }

        await _container.DisposeAsync();
    }

    /// <summary>Runs arbitrary SQL. For arranging state and asserting on it directly.</summary>
    public async Task ExecuteAsync(string sql)
    {
        await using var connection = await DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    public async Task<T?> ScalarAsync<T>(string sql)
    {
        await using var connection = await DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        var result = await command.ExecuteScalarAsync();
        return result is null or DBNull ? default : (T)result;
    }

    /// <summary>Well-known ids so tests can reference the seeded hierarchy directly.</summary>
    public static readonly Guid OrgId       = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid PoliceUnit  = Guid.Parse("a1111111-1111-1111-1111-111111111111");
    public static readonly Guid AhmedabadCp = Guid.Parse("a2222222-2222-2222-2222-222222222222");
    public static readonly Guid DistrictId  = Guid.Parse("b1111111-1111-1111-1111-111111111111");
    public static readonly Guid VillageId   = Guid.Parse("b2222222-2222-2222-2222-222222222222");

    /// <summary>
    /// Clears connector state and re-seeds the organization and geography a test needs.
    /// </summary>
    /// <remarks>
    /// Targets are FK-constrained to an organization unit, and scope resolution walks the
    /// hierarchy, so a flat truncate is no longer enough — the tree has to exist for anything
    /// to be insertable or authorizable.
    /// </remarks>
    public async Task ResetTargetsAsync()
    {
        await ExecuteAsync("TRUNCATE federation.connector_target CASCADE;");

        await ExecuteAsync($"""
            INSERT INTO federation.organizations (id, code, name, organization_type)
            VALUES ('{OrgId}', 'POLICE', 'Police Department', 'DEPARTMENT')
            ON CONFLICT (id) DO NOTHING;

            INSERT INTO federation.organization_units
                (id, organization_id, parent_unit_id, code, name, unit_type)
            VALUES ('{PoliceUnit}', '{OrgId}', NULL, 'PD', 'Police HQ', 'DEPARTMENT')
            ON CONFLICT (id) DO NOTHING;

            INSERT INTO federation.organization_units
                (id, organization_id, parent_unit_id, code, name, unit_type)
            VALUES ('{AhmedabadCp}', '{OrgId}', '{PoliceUnit}', 'AHM-CP', 'Ahmedabad CP',
                    'COMMISSIONERATE')
            ON CONFLICT (id) DO NOTHING;

            INSERT INTO federation.geographic_areas (id, parent_area_id, code, name, area_type)
            VALUES ('{DistrictId}', NULL, 'AHM', 'Ahmedabad', 'DISTRICT')
            ON CONFLICT (id) DO NOTHING;

            INSERT INTO federation.geographic_areas (id, parent_area_id, code, name, area_type)
            VALUES ('{VillageId}', '{DistrictId}', 'VILX', 'Village X', 'VILLAGE')
            ON CONFLICT (id) DO NOTHING;
            """);
    }
}
