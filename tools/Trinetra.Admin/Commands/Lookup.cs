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

    public static Task<Guid> RoleAsync(NpgsqlDataSource db, string code, CancellationToken ct) =>
        ResolveAsync(db, "federation.roles", code, "role", "role list", ct);

    public static async Task<Guid?> OptionalUnitAsync(NpgsqlDataSource db, string? code, CancellationToken ct) =>
        string.IsNullOrWhiteSpace(code) ? null : await OrganizationUnitAsync(db, code, ct);

    public static async Task<Guid?> OptionalAreaAsync(NpgsqlDataSource db, string? code, CancellationToken ct) =>
        string.IsNullOrWhiteSpace(code) ? null : await GeographicAreaAsync(db, code, ct);

    private static async Task<Guid> ResolveAsync(
        NpgsqlDataSource db, string table, string code, string what, string listCommand,
        CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);

        // geographic_areas.code is unique per parent, not globally (v1.11) — a code can recur
        // under different parents, so take up to two and reject an ambiguous match.
        var ids = (await connection.QueryAsync<Guid>(new CommandDefinition(
            $"SELECT id FROM {table} WHERE code = @code LIMIT 2;",
            new { code }, cancellationToken: ct))).ToList();

        return ids.Count switch
        {
            0 => throw new CommandFailedException(
                $"No {what} with code '{code}'. List them with: trinetra-admin {listCommand}"),
            1 => ids[0],
            _ => throw new CommandFailedException(
                $"'{code}' matches more than one {what} (codes are unique only within a parent). "
                + $"List them with: trinetra-admin {listCommand}"),
        };
    }
}
