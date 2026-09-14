using Microsoft.AspNetCore.Http.HttpResults;
using Trinetra.Federation.Api.Auth;
using Trinetra.Federation.Api.Contracts;
using Trinetra.Federation.Api.OpenApi;
using Trinetra.Federation.Storage.Repositories;

namespace Trinetra.Federation.Api.Endpoints;

/// <summary>
/// A caller's own video-wall layout — which cameras sit in which tile, in a grid that is
/// <c>columnCount</c> wide and grows/shrinks a row at a time as cameras are added or removed. A
/// personal UI preference, not department-scoped domain data: every route here reads or writes
/// the caller's own row only (CLAUDE.md invariant 11's documented "own primary key" exception),
/// needs no permission beyond being authenticated, and is not audited.
/// </summary>
public static class VideoWallEndpoints
{
    private const int MinColumns = 1;
    private const int MaxColumns = 12;
    private const int MaxTiles = 64;

    public static void MapVideoWallEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/video-wall")
            .WithTags(ApiTags.VideoWall).RequireAuthorization();

        const string why = "reads/writes only the caller's own row — a permission would only ever "
            + "grant or deny someone access to their own layout, which authentication alone "
            + "already settles";

        group.MapGet("/preferences", GetAsync)
          .WithSummary("Read the caller's video-wall layout")
          .WithDescription(
              "The caller's own saved column count and camera order. `404` means the wall is not "
              + "configured — the caller has never saved a layout — and the client is expected to "
              + "show an explicit \"not configured\" state with a way to start configuring, rather "
              + "than inventing a default grid.")
          .AllowAnyAuthenticated(why);

        group.MapPut("/preferences", SaveAsync)
          .WithSummary("Save the caller's video-wall layout")
          .WithDescription(
              "Creates or replaces the caller's layout in full. `cameraIds` is the wall in grid "
              + "order (row-major, wrapping every `columnCount` tiles) — its length is the tile "
              + "count, there are no empty-tile placeholders. `columnCount` must be 1-12 and "
              + $"`cameraIds` may hold at most {MaxTiles} entries. Camera ids are stored as given "
              + "— a camera later deleted or moved out of the caller's access is not pruned from "
              + "a saved layout, only unresolved when read back elsewhere.")
          .AllowAnyAuthenticated(why);

        group.MapDelete("/preferences", DeleteAsync)
          .WithSummary("Clear the caller's video-wall layout")
          .WithDescription(
              "Returns the wall to \"not configured\" — the next `GET` is `404` again.")
          .AllowAnyAuthenticated(why);
    }

    private static async Task<Results<Ok<VideoWallPreferenceResponse>, NotFound>> GetAsync(
        VideoWallPreferenceRepository repo, HttpContext http, CancellationToken ct)
    {
        var userId = RequireUserId(http);

        var row = await repo.GetAsync(userId, ct);
        if (row is null)
        {
            return TypedResults.NotFound();
        }

        return TypedResults.Ok(new VideoWallPreferenceResponse(
            row.ColumnCount, [.. row.CameraIds.Select(id => id.ToString())]));
    }

    private static async Task<Results<Ok<VideoWallPreferenceResponse>, ProblemHttpResult>> SaveAsync(
        VideoWallPreferenceRequest request,
        VideoWallPreferenceRepository repo,
        HttpContext http,
        CancellationToken ct)
    {
        var userId = RequireUserId(http);

        if (request.ColumnCount is < MinColumns or > MaxColumns)
        {
            return Bad("Invalid column count", $"'columnCount' must be between {MinColumns} and {MaxColumns}.");
        }

        if (request.CameraIds.Count == 0)
        {
            return Bad("Empty wall", "'cameraIds' must have at least one entry — clear the wall with DELETE instead.");
        }

        if (request.CameraIds.Count > MaxTiles)
        {
            return Bad("Too many tiles", $"'cameraIds' may hold at most {MaxTiles} entries.");
        }

        var cameraIds = new Guid[request.CameraIds.Count];
        for (var i = 0; i < request.CameraIds.Count; i++)
        {
            if (!Guid.TryParse(request.CameraIds[i], out var parsed))
            {
                return Bad("Invalid camera id", $"'cameraIds[{i}]' must be a valid camera id.");
            }

            cameraIds[i] = parsed;
        }

        await repo.SaveAsync(userId, request.ColumnCount, cameraIds, ct);

        // The frontend's `request<T>` helper treats only 204 as bodyless and otherwise always
        // parses JSON — an empty 200 body would throw on every successful save. Echo back what
        // was just persisted, matching GetAsync's shape.
        return TypedResults.Ok(new VideoWallPreferenceResponse(request.ColumnCount, request.CameraIds));
    }

    private static async Task<NoContent> DeleteAsync(
        VideoWallPreferenceRepository repo, HttpContext http, CancellationToken ct)
    {
        var userId = RequireUserId(http);
        await repo.DeleteAsync(userId, ct);
        return TypedResults.NoContent();
    }

    private static Guid RequireUserId(HttpContext http) =>
        CallerContextFactory.From(http).UserId
            ?? throw new InvalidOperationException("Authenticated caller has no user id.");

    private static ProblemHttpResult Bad(string title, string detail) => TypedResults.Problem(
        title: title, detail: detail, statusCode: StatusCodes.Status400BadRequest);
}
