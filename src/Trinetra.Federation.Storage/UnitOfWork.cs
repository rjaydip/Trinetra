using System.Text.Json;
using Dapper;
using Npgsql;

namespace Trinetra.Federation.Storage;

/// <summary>
/// One connection and one transaction shared by a mutation and its audit record.
/// </summary>
/// <remarks>
/// <para>
/// The mutation and its audit row used to be written on <b>separate connections</b>, each
/// committing on its own. A process death, a connection reset, or a pool timeout between the two
/// left the change applied and unrecorded — and the record that goes missing is, by definition,
/// the one written closest to whatever caused the crash. "Who repointed this NVR" then has no
/// answer for precisely the incident being investigated.
/// </para>
/// <para>
/// Both writes now share a transaction, so the change and the evidence of it commit together or
/// not at all.
/// </para>
/// <para>
/// This is also the <b>only</b> way to write an audit row: <c>ConfigAuditWriter</c> is no longer
/// injectable, so there is no path that records a change outside the transaction that made it.
/// Repository write methods require a <see cref="UnitOfWork"/> for the same reason — a mutation
/// that could open its own connection would silently leave the transaction.
/// </para>
/// </remarks>
public sealed class UnitOfWork : IAsyncDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        // Compact: audit rows are numerous and read by machines far more often than by people.
        WriteIndented = false,
    };

    private readonly NpgsqlConnection _connection;
    private readonly NpgsqlTransaction _transaction;
    private bool _committed;

    private UnitOfWork(NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        _connection = connection;
        _transaction = transaction;
    }

    /// <summary>The connection every write in this unit must use.</summary>
    public NpgsqlConnection Connection => _connection;

    /// <summary>The transaction every command in this unit must enlist in.</summary>
    /// <remarks>
    /// Dapper does not pick this up implicitly. A <c>CommandDefinition</c> built without it runs
    /// outside the transaction even on the same connection, which is the failure this type exists
    /// to prevent — so every write passes it explicitly.
    /// </remarks>
    public NpgsqlTransaction Transaction => _transaction;

    public static async Task<UnitOfWork> BeginAsync(
        NpgsqlDataSource dataSource, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(dataSource);

        var connection = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);

        try
        {
            var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            return new UnitOfWork(connection, transaction);
        }
        catch
        {
            // The connection is ours until the UnitOfWork exists to own it.
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Records a configuration change, inside this transaction.</summary>
    /// <remarks>
    /// Snapshots are stored rather than a diff so the record stays readable years later, when the
    /// code that produced the diff format is long gone.
    /// </remarks>
    public async Task AuditAsync(
        CallerContext caller,
        string action,
        string entityType,
        string entityId,
        object? before,
        object? after,
        Guid? organizationUnitId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);

        await _connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO federation.config_audit
                (actor, actor_user_id, actor_api_key_id, action, entity_type, entity_id,
                 organization_unit_id, before_state, after_state, source_address)
            VALUES (@Actor, @UserId, @ApiKeyId, @action, @entityType, @entityId,
                    @organizationUnitId, @before::jsonb, @after::jsonb, @SourceAddress);
            """, new
        {
            caller.Actor, caller.UserId, caller.ApiKeyId, caller.SourceAddress,
            action, entityType, entityId, organizationUnitId,
            before = before is null ? null : JsonSerializer.Serialize(before, SerializerOptions),
            after = after is null ? null : JsonSerializer.Serialize(after, SerializerOptions),
        }, _transaction, cancellationToken: ct)).ConfigureAwait(false);
    }

    public async Task CommitAsync(CancellationToken ct)
    {
        await _transaction.CommitAsync(ct).ConfigureAwait(false);
        _committed = true;
    }

    /// <summary>
    /// Rolls back anything not committed.
    /// </summary>
    /// <remarks>
    /// This is what makes an early return safe. An endpoint that validates, mutates, then decides
    /// to answer 409 simply returns; the <c>await using</c> unwinds and the change is undone. The
    /// alternative — remembering to roll back on every branch — is the kind of rule that holds
    /// until someone adds a branch.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        if (!_committed)
        {
            try
            {
                await _transaction.RollbackAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is InvalidOperationException or NpgsqlException)
            {
                // Already finished, or the connection is gone. Either way the server has ended
                // the transaction and there is nothing left to undo; throwing here would replace
                // the original failure with a misleading one.
            }
        }

        await _transaction.DisposeAsync().ConfigureAwait(false);
        await _connection.DisposeAsync().ConfigureAwait(false);
    }
}
