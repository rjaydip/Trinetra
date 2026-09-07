using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Trinetra.Federation.Storage.Repositories;

/// <summary>One row to append to the authentication audit trail.</summary>
public sealed record AuthAuditEntry
{
    public required string EventType { get; init; }
    public required string Outcome { get; init; }
    public Guid? UserId { get; init; }
    public Guid? ApiKeyId { get; init; }

    /// <summary>Only set when the principal could not be resolved. Truncated to 256 chars.</summary>
    public string? PresentedUsername { get; init; }

    public string? SourceAddress { get; init; }
    public string? UserAgent { get; init; }
    public string? Jti { get; init; }

    /// <summary>Serialised to compact JSON.</summary>
    public object? Detail { get; init; }
}

/// <summary>One row of the auth audit list. Never mutated.</summary>
public sealed record AuthAuditRow
{
    public DateTimeOffset OccurredAt { get; init; }
    public long Id { get; init; }
    public string EventType { get; init; } = "";
    public string Outcome { get; init; } = "";
    public Guid? UserId { get; init; }
    public Guid? ApiKeyId { get; init; }
    public string? PresentedUsername { get; init; }
    public string? SourceAddress { get; init; }
    public string? UserAgent { get; init; }
    public string? Jti { get; init; }
    public string? Detail { get; init; }
}

/// <summary>A newest-first page request for the auth audit trail.</summary>
public sealed record AuthAuditQuery(
    DateTimeOffset From,
    DateTimeOffset To,
    string? EventType,
    string? Outcome,
    Guid? UserId,
    DateTimeOffset? CursorTime,
    long? CursorId,
    int Limit);

/// <summary>
/// The append-only authentication audit trail (finding 4-H3). Two write paths: one that rides an
/// existing <see cref="UnitOfWork"/> for events that accompany a mutation, and one best-effort
/// path for pure events that never blocks or fails the request.
/// </summary>
public sealed partial class AuthAuditRepository
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<AuthAuditRepository> _logger;

    public AuthAuditRepository(NpgsqlDataSource dataSource, ILogger<AuthAuditRepository> logger)
    {
        _dataSource = dataSource;
        _logger = logger;
    }

    private const string InsertSql = """
        INSERT INTO federation.auth_audit
            (event_type, outcome, user_id, api_key_id, presented_username,
             source_address, user_agent, jti, detail)
        VALUES (@EventType, @Outcome, @UserId, @ApiKeyId, @PresentedUsername,
                @SourceAddress, @UserAgent, @Jti, @Detail::jsonb);
        """;

    private static readonly JsonSerializerOptions CompactJson = new() { WriteIndented = false };

    private static object Params(AuthAuditEntry e) => new
    {
        e.EventType,
        e.Outcome,
        e.UserId,
        e.ApiKeyId,
        PresentedUsername = Cap(e.PresentedUsername, 256),
        e.SourceAddress,
        UserAgent = Cap(e.UserAgent, 512),
        e.Jti,
        Detail = e.Detail is null ? null : JsonSerializer.Serialize(e.Detail, CompactJson),
    };

    private static string? Cap(string? s, int max) =>
        s is null ? null : s.Length <= max ? s : s[..max];

    /// <summary>
    /// Appends an event that accompanies a database mutation, inside that mutation's
    /// transaction. Static and <see cref="UnitOfWork"/>-bound for the same reason as
    /// <c>ApiKeyRepository.RevokeAsync</c>: a write on its own connection would silently leave
    /// the transaction.
    /// </summary>
    public static Task WriteAsync(UnitOfWork work, AuthAuditEntry entry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(entry);
        return work.Connection.ExecuteAsync(new CommandDefinition(
            InsertSql, Params(entry), work.Transaction, cancellationToken: ct));
    }

    /// <summary>
    /// Appends a pure event on its own pooled connection. Best effort: never throws (except on
    /// cancellation), never blocks the caller's result. An auth-audit outage must not become an
    /// auth outage.
    /// </summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Best-effort security telemetry: a failed audit write is logged and "
                      + "swallowed rather than failing the authentication it describes.")]
    public async Task WriteBestEffortAsync(AuthAuditEntry entry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);
        try
        {
            await using var c = await _dataSource.OpenConnectionAsync(ct);
            await c.ExecuteAsync(new CommandDefinition(
                InsertSql, Params(entry), cancellationToken: ct));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogWriteFailed(_logger, entry.EventType, ex);
        }
    }

    /// <summary>A newest-first page of the trail. Keyset on <c>(occurred_at DESC, id DESC)</c>.</summary>
    public async Task<IReadOnlyList<AuthAuditRow>> ListAsync(
        AuthAuditQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        await using var c = await _dataSource.OpenConnectionAsync(ct);

        var rows = await c.QueryAsync<AuthAuditRow>(new CommandDefinition("""
            SELECT occurred_at AS OccurredAt, id AS Id, event_type AS EventType,
                   outcome AS Outcome, user_id AS UserId, api_key_id AS ApiKeyId,
                   presented_username AS PresentedUsername, source_address AS SourceAddress,
                   user_agent AS UserAgent, jti AS Jti, detail::text AS Detail
            FROM federation.auth_audit
            WHERE occurred_at >= @From AND occurred_at < @To
              AND (@EventType IS NULL OR event_type = @EventType)
              AND (@Outcome   IS NULL OR outcome    = @Outcome)
              AND (@UserId    IS NULL OR user_id    = @UserId)
              AND (@CursorTime IS NULL OR (occurred_at, id) < (@CursorTime, @CursorId))
            ORDER BY occurred_at DESC, id DESC
            LIMIT @Limit;
            """, new
        {
            query.From, query.To, query.EventType, query.Outcome, query.UserId,
            query.CursorTime, query.CursorId, query.Limit,
        }, cancellationToken: ct));

        return [.. rows];
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Failed to write an auth-audit row for {EventType}. The event is lost; "
                + "authentication itself was not affected.")]
    private static partial void LogWriteFailed(ILogger logger, string eventType, Exception ex);
}
