using Dapper;
using Npgsql;

namespace Trinetra.Federation.Storage.Repositories;

/// <summary>One entry in the saved-credential library — metadata only, never secret material.</summary>
/// <remarks>
/// Init-property record, not a positional one — matches <c>WatchlistAlertRow</c>'s shape. Dapper
/// maps a positional record through its constructor, which requires an exact parameter-type
/// match; a `timestamptz` column comes back as `DateTime`, not `DateTimeOffset`, so a positional
/// `DateTimeOffset CreatedAt` parameter has no matching constructor and Dapper throws. The
/// parameterless-constructor-plus-property-setter path this shape uses instead converts the value
/// as it assigns it.
/// </remarks>
public sealed record SavedCredentialRow
{
    public Guid Id { get; init; }
    public string Name { get; init; } = "";
    public string? Description { get; init; }
    public string CredentialReference { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public int UsageCount { get; init; }
}

/// <summary>One camera pointed at a saved credential's reference.</summary>
public sealed record SavedCredentialUsedByRow
{
    public Guid Id { get; init; }
    public string CameraCode { get; init; } = "";
    public string Name { get; init; } = "";
}

/// <summary>
/// Metadata for the saved-credential library (v1.23/v1.24) — name, description, and which
/// <c>credential_reference</c> a row points at. The secret itself is written through
/// <see cref="SecretWriter"/> into the existing <c>secret</c> table, exactly as any other
/// credential in this schema; this repository never touches secret material.
/// </summary>
/// <remarks>
/// Not organization/geography scoped: a saved credential is a reusable convenience for whoever is
/// registering cameras, not itself a piece of scoped domain data, so every row is visible to any
/// caller who can list it. Access to what it points at is still gated the ordinary way — a caller
/// still needs <c>camera.credential.resolve</c> (held only by machine roles) to ever see the
/// resolved secret, exactly as for a credential entered directly on a camera. "Usage" (which
/// cameras point at a reference) is answered by querying <c>cameras.credential_reference</c>
/// directly — there is no join table (v1.24's remarks) — and <see cref="GetUsedByAsync"/> scopes
/// that camera list through the same organization/geography predicate
/// <c>CameraRepository</c> itself uses, so this never discloses a camera's existence to a caller
/// who could not already reach it via <c>GET /cameras/{id}</c>.
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Performance", "CA1822:Mark members as static",
    Justification = "Write methods take their connection from the UnitOfWork by design.",
    Scope = "type")]
public sealed class SavedCredentialRepository
{
    private readonly NpgsqlDataSource _dataSource;

    public SavedCredentialRepository(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    /// <summary><see cref="SavedCredentialRow.UsageCount"/> is a true total, not scoped to the
    /// caller — matches <c>RoleResponse</c>'s own list-route behavior.</summary>
    public async Task<IReadOnlyList<SavedCredentialRow>> ListAsync(CancellationToken ct)
    {
        await using var c = await _dataSource.OpenConnectionAsync(ct);
        var rows = await c.QueryAsync<SavedCredentialRow>(new CommandDefinition("""
            SELECT sc.id, sc.name, sc.description,
                   sc.credential_reference AS CredentialReference,
                   sc.created_at AS CreatedAt, sc.updated_at AS UpdatedAt,
                   (SELECT count(*) FROM federation.cameras cam
                    WHERE cam.credential_reference = sc.credential_reference
                      AND cam.deleted_at IS NULL)::int AS UsageCount
            FROM federation.saved_credential sc
            ORDER BY sc.name;
            """, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<SavedCredentialRow?> GetAsync(Guid id, CancellationToken ct)
    {
        await using var c = await _dataSource.OpenConnectionAsync(ct);
        return await c.QuerySingleOrDefaultAsync<SavedCredentialRow>(new CommandDefinition("""
            SELECT sc.id, sc.name, sc.description,
                   sc.credential_reference AS CredentialReference,
                   sc.created_at AS CreatedAt, sc.updated_at AS UpdatedAt,
                   (SELECT count(*) FROM federation.cameras cam
                    WHERE cam.credential_reference = sc.credential_reference
                      AND cam.deleted_at IS NULL)::int AS UsageCount
            FROM federation.saved_credential sc
            WHERE sc.id = @id;
            """, new { id }, cancellationToken: ct));
    }

    /// <summary>Cameras pointed at <paramref name="credentialReference"/>, scoped to what
    /// <paramref name="caller"/> can reach — the same dual-dimension predicate
    /// <c>CameraRepository</c> applies to <c>GET /cameras</c>, so this never names a camera the
    /// caller could not already see there.</summary>
    public async Task<IReadOnlyList<SavedCredentialUsedByRow>> GetUsedByAsync(
        string credentialReference, CallerContext caller, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require("camera.read");

        var reachClause = (caller.IsUnscopedFor("camera.read") && caller.IsUnscopedForGeography("camera.read"))
            ? ""
            : """
              AND (@UnscopedOrg OR c.organization_unit_id IN (
                  SELECT organization_unit_id FROM federation.authorized_org_units(
                      p_user_id => @UserId, p_api_key_id => @ApiKeyId, p_permission => 'camera.read')))
                AND (@UnscopedGeo OR c.geographic_area_id IN (
                  SELECT geographic_area_id FROM federation.authorized_geographic_areas(
                      p_user_id => @UserId, p_api_key_id => @ApiKeyId, p_permission => 'camera.read')))
              """;

        await using var c = await _dataSource.OpenConnectionAsync(ct);
        var rows = await c.QueryAsync<SavedCredentialUsedByRow>(new CommandDefinition($"""
            SELECT c.id, c.camera_code AS CameraCode, c.name
            FROM federation.cameras c
            WHERE c.credential_reference = @credentialReference AND c.deleted_at IS NULL
              {reachClause}
            ORDER BY c.camera_code
            LIMIT 200;
            """, new
        {
            credentialReference,
            caller.UserId, caller.ApiKeyId,
            UnscopedOrg = caller.IsUnscopedFor("camera.read"),
            UnscopedGeo = caller.IsUnscopedForGeography("camera.read"),
        }, cancellationToken: ct));
        return rows.ToList();
    }

    /// <summary>
    /// Inserts the library row. The caller has already sealed the secret under
    /// <paramref name="credentialReference"/> (via <see cref="SecretWriter"/>, in the same
    /// <see cref="UnitOfWork"/>) before this runs — the table's foreign key on
    /// <c>credential_reference</c> means a <c>secret</c> row must already exist, so ordering
    /// (seal first, then record the library entry) is not optional.
    /// </summary>
    public async Task<SavedCredentialRow> CreateAsync(
        Guid id, string name, string? description, string credentialReference, string createdBy,
        UnitOfWork work, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialReference);
        ArgumentException.ThrowIfNullOrWhiteSpace(createdBy);

        return await work.Connection.QuerySingleAsync<SavedCredentialRow>(new CommandDefinition("""
            INSERT INTO federation.saved_credential (id, name, description, credential_reference, created_by)
            VALUES (@id, @name, @description, @credentialReference, @createdBy)
            RETURNING id, name, description, credential_reference AS CredentialReference,
                      created_at AS CreatedAt, updated_at AS UpdatedAt, 0 AS UsageCount;
            """, new { id, name, description, credentialReference, createdBy },
            work.Transaction, cancellationToken: ct));
    }

    /// <summary>
    /// Renames/re-describes an existing entry. Never touches <c>credential_reference</c> — a
    /// rotation reseals the <b>same</b> reference through <see cref="SecretWriter"/> instead of
    /// changing which one this row points at (that is the entire point of a shared reference:
    /// every camera pointed at it keeps working, unaware anything changed). Returns null if the
    /// row does not exist, so the endpoint can 404 rather than silently no-op.
    /// </summary>
    public async Task<SavedCredentialRow?> UpdateAsync(
        Guid id, string? name, string? description, string updatedBy,
        UnitOfWork work, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(updatedBy);

        return await work.Connection.QuerySingleOrDefaultAsync<SavedCredentialRow>(new CommandDefinition("""
            UPDATE federation.saved_credential
            SET name = COALESCE(@name, name),
                description = CASE WHEN @descriptionSet THEN @description ELSE description END,
                updated_at = now(), updated_by = @updatedBy
            WHERE id = @id
            RETURNING id, name, description, credential_reference AS CredentialReference,
                      created_at AS CreatedAt, updated_at AS UpdatedAt,
                      (SELECT count(*) FROM federation.cameras cam
                       WHERE cam.credential_reference = federation.saved_credential.credential_reference
                         AND cam.deleted_at IS NULL)::int AS UsageCount;
            """, new { id, name, description, descriptionSet = description is not null, updatedBy },
            work.Transaction, cancellationToken: ct));
    }

    /// <summary>
    /// Removes the library entry only — the underlying <c>secret</c> row is left sealed and any
    /// camera still pointed at its <c>credential_reference</c> keeps resolving it exactly as
    /// before. Deleting the secret out from under cameras that depend on it would silently break
    /// their streams; this only removes the entry from future pickers. Returns false if the row
    /// did not exist.
    /// </summary>
    public async Task<bool> DeleteAsync(Guid id, UnitOfWork work, CancellationToken ct)
    {
        var affected = await work.Connection.ExecuteAsync(new CommandDefinition("""
            DELETE FROM federation.saved_credential WHERE id = @id;
            """, new { id }, work.Transaction, cancellationToken: ct));
        return affected > 0;
    }
}
