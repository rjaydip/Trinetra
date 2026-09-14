using Dapper;
using Npgsql;

namespace Trinetra.Federation.Storage.Repositories;

/// <summary>A user's saved video-wall layout.</summary>
public sealed record VideoWallPreferenceRow
{
    public int ColumnCount { get; init; }
    public Guid[] CameraIds { get; init; } = [];
}

/// <summary>
/// Each user's own video-wall tile layout — a personal UI preference, not department-scoped or
/// RBAC-sensitive domain data. Keyed directly on the caller's user id rather than a
/// <see cref="CallerContext"/>: there is no cross-principal access path here, only ever "my own
/// row" (CLAUDE.md invariant 11's documented exception), so there is nothing to scope.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Performance", "CA1822:Mark members as static",
    Justification = "Kept an instance method for consistency with the rest of Storage.",
    Scope = "type")]
public sealed class VideoWallPreferenceRepository
{
    private readonly NpgsqlDataSource _dataSource;

    public VideoWallPreferenceRepository(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    /// <summary>The caller's saved layout, or null if they have never saved one (not configured).</summary>
    public async Task<VideoWallPreferenceRow?> GetAsync(Guid userId, CancellationToken ct)
    {
        await using var c = await _dataSource.OpenConnectionAsync(ct);

        return await c.QuerySingleOrDefaultAsync<VideoWallPreferenceRow>(new CommandDefinition("""
            SELECT column_count AS "ColumnCount", camera_ids AS "CameraIds"
            FROM federation.video_wall_preference
            WHERE user_id = @userId;
            """, new { userId }, cancellationToken: ct));
    }

    /// <summary>
    /// Creates or replaces the caller's layout. <paramref name="cameraIds"/> carries the wall in
    /// grid order (left-to-right, wrapping every <paramref name="columnCount"/> tiles) — its
    /// length *is* the tile count, there is no separate empty-tile slot to track.
    /// </summary>
    public async Task SaveAsync(
        Guid userId, int columnCount, IReadOnlyList<Guid> cameraIds, CancellationToken ct)
    {
        await using var c = await _dataSource.OpenConnectionAsync(ct);

        await c.ExecuteAsync(new CommandDefinition("""
            INSERT INTO federation.video_wall_preference
                (user_id, tile_count, column_count, camera_ids, updated_at)
            VALUES (@userId, @tileCount, @columnCount, @cameraIds, now())
            ON CONFLICT (user_id) DO UPDATE
                SET tile_count = EXCLUDED.tile_count,
                    column_count = EXCLUDED.column_count,
                    camera_ids = EXCLUDED.camera_ids,
                    updated_at = now();
            """, new
        {
            userId,
            tileCount = cameraIds.Count,
            columnCount,
            cameraIds = cameraIds.ToArray(),
        }, cancellationToken: ct));
    }

    /// <summary>Deletes the caller's saved layout — the explicit "not configured" state.</summary>
    public async Task DeleteAsync(Guid userId, CancellationToken ct)
    {
        await using var c = await _dataSource.OpenConnectionAsync(ct);

        await c.ExecuteAsync(new CommandDefinition("""
            DELETE FROM federation.video_wall_preference WHERE user_id = @userId;
            """, new { userId }, cancellationToken: ct));
    }
}
