using Dapper;
using Npgsql;

namespace Trinetra.Admin.Commands;

/// <summary>
/// Resolves the human codes an operator types into the UUIDs the system stores.
/// </summary>
/// <remarks>
/// Every entity has an immutable UUID and a readable code, per <c>CAMERA-SCHEMA.md</c>. Nobody
/// at a terminal has a UUID to hand, so the CLI speaks entirely in codes and translates here.
/// A failed lookup names what was not found and how to list the valid values, because "FK
/// violation on organization_unit_id" tells an operator nothing actionable.
/// </remarks>
internal static class Lookup
{
    public static Task<Guid> OrganizationAsync(NpgsqlDataSource db, string code, CancellationToken ct) =>
        ResolveAsync(db, "federation.organizations", code, "organization", "org list", ct);

    public static Task<Guid> OrganizationUnitAsync(NpgsqlDataSource db, string code, CancellationToken ct) =>
        ResolveAsync(db, "federation.organization_units", code, "organization unit", "unit list", ct);

    public static Task<Guid> GeographicAreaAsync(NpgsqlDataSource db, string code, CancellationToken ct) =>
        ResolveAsync(db, "federation.geographic_areas", code, "geographic area", "area list", ct);

    public static Task<Guid> SiteAsync(NpgsqlDataSource db, string code, CancellationToken ct) =>
        ResolveAsync(db, "federation.sites", code, "site", "site list", ct);

    public static Task<Guid> RoleAsync(NpgsqlDataSource db, string code, CancellationToken ct) =>
        ResolveAsync(db, "federation.roles", code, "role", "role list", ct);

    public static async Task<Guid?> OptionalSiteAsync(NpgsqlDataSource db, string? code, CancellationToken ct) =>
        string.IsNullOrWhiteSpace(code) ? null : await SiteAsync(db, code, ct);

    public static async Task<Guid?> OptionalUnitAsync(NpgsqlDataSource db, string? code, CancellationToken ct) =>
        string.IsNullOrWhiteSpace(code) ? null : await OrganizationUnitAsync(db, code, ct);

    public static async Task<Guid?> OptionalAreaAsync(NpgsqlDataSource db, string? code, CancellationToken ct) =>
        string.IsNullOrWhiteSpace(code) ? null : await GeographicAreaAsync(db, code, ct);

    private static async Task<Guid> ResolveAsync(
        NpgsqlDataSource db, string table, string code, string what, string listCommand,
        CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);

        var id = await connection.ExecuteScalarAsync<Guid?>(new CommandDefinition(
            $"SELECT id FROM {table} WHERE code = @code;", new { code }, cancellationToken: ct));

        return id ?? throw new CommandFailedException(
            $"No {what} with code '{code}'. List them with: trinetra-admin {listCommand}");
    }
}
