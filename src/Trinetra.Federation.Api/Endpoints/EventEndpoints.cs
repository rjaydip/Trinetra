using Trinetra.Federation.Storage.Repositories;
using Microsoft.AspNetCore.Http.HttpResults;
using System.Globalization;
using System.Text;
using Trinetra.Federation.Api.Auth;
using Trinetra.Federation.Api.Contracts;
using Trinetra.Federation.Api.OpenApi;

namespace Trinetra.Federation.Api.Endpoints;

/// <summary>
/// Event queries over the hot PostgreSQL window.
/// </summary>
/// <remarks>
/// <para>
/// <c>federation_event</c> grows by 100-400M rows/day at the design point, so every constraint
/// below is structural rather than advisory. An unbounded query here takes the database down,
/// and with it every worker writing events.
/// </para>
/// <para>
/// <b>Boundary:</b> this serves the hot window only. Free-text and wide historical search need
/// OpenSearch; these endpoints get re-pointed at it rather than extended. Keeping them narrow is
/// what makes that swap cheap.
/// </para>
/// </remarks>
public static class EventEndpoints
{
    /// <summary>Widest window a single query may span.</summary>
    private static readonly TimeSpan MaxWindow = TimeSpan.FromDays(7);

    private const int DefaultPageSize = 100;
    private const int MaxPageSize = 1000;

    public static void MapEventEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/events", QueryAsync)
           .RequireAuthorization()
           .WithTags(ApiTags.Events).RequirePermission("event.read")
           .WithSummary("Query normalized events over the hot window")
           .WithDescription(
               "Events from every federated VMS in one shape, whatever vendor produced them, "
               + "newest first and restricted to the caller's scope. `eventType` is the "
               + "normalized type; `vendorEventType` is what the device actually called it, kept "
               + "for tracing a normalization back to its source.\n\n"
               + "**`from` and `to` are required and may span at most 7 days.** They are never "
               + "defaulted: the table is partitioned by time and takes 100-400M rows a day at "
               + "the design point, so a missing range silently becoming 'everything' is the one "
               + "query that takes the database down — and with it every worker writing events. "
               + "Narrow further with `cameraId`, `eventType` or `objectReference`.\n\n"
               + "Paging is by the opaque `cursor` from the previous page, never by offset — a "
               + "deep offset on a partitioned table scans every row it skips. A response without "
               + "a cursor is the end of the results.\n\n"
               + "`limit` defaults to 100 and is clamped to 1000.\n\n"
               + "**Boundary:** this is the hot PostgreSQL window only. Free-text and wide "
               + "historical search belong to OpenSearch and are not served here.");
    }

    private static async Task<Results<Ok<EventPage>, ProblemHttpResult>> QueryAsync(
        DateTimeOffset? from,
        DateTimeOffset? to,
        string? cameraId,
        string? eventType,
        string? objectReference,
        string? cursor,
        int? limit,
        EventQueryRepository events,
        HttpContext http,
        CancellationToken ct)
    {
        var caller = CallerContextFactory.From(http);
        caller.Require("event.read");

        // Required, never defaulted. A missing range silently becoming "everything" is exactly
        // the query that scans a billion rows.
        if (from is null || to is null)
        {
            return TypedResults.Problem(
                title: "Time range required",
                detail: "Both 'from' and 'to' must be supplied. The event table is partitioned by "
                      + "time, and an unbounded query cannot be served.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (to <= from)
        {
            return TypedResults.Problem(
                title: "Invalid time range", detail: "'to' must be after 'from'.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (to - from > MaxWindow)
        {
            return TypedResults.Problem(
                title: "Time range too wide",
                detail: $"The window may span at most {MaxWindow.TotalDays:F0} days. "
                      + "Page through with the returned cursor, or narrow the range.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var pageSize = Math.Clamp(limit ?? DefaultPageSize, 1, MaxPageSize);

        // Keyset pagination on (occurred_at DESC, event_id). Never OFFSET: a deep offset on a
        // partitioned table scans every row it skips, so page 500 costs 500 pages of work.
        DateTimeOffset? cursorTime = null;
        string? cursorId = null;

        if (!string.IsNullOrEmpty(cursor) && !TryDecodeCursor(cursor, out cursorTime, out cursorId))
        {
            return TypedResults.Problem(
                title: "Invalid cursor",
                detail: "The cursor is malformed. Restart the query without one.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var rows = await events.QueryAsync(
            new EventQuery(from!.Value, to!.Value, pageSize, cameraId, eventType,
                           objectReference, cursorTime, cursorId),
            caller, ct);

        var page = rows.Select(r => new EventSummary(
            r.EventId, r.SourceVmsId, r.CameraId, r.EventType, r.VendorEventType,
            r.OccurredAt, r.Severity, r.ObjectReference, r.Confidence)).ToList();

        // A cursor only when the page was full: a short page is the end of the results, and
        // handing back a cursor there makes clients poll forever for nothing.
        var next = rows.Count == pageSize
            ? EncodeCursor(rows[^1].OccurredAt, rows[^1].EventId)
            : null;

        return TypedResults.Ok(new EventPage(page, next));
    }

    /// <summary>
    /// Encodes the keyset position opaquely.
    /// </summary>
    /// <remarks>
    /// Opaque so clients cannot construct one by hand and come to depend on its shape — which
    /// would freeze the sort order and block the eventual move to OpenSearch.
    /// </remarks>
    private static string EncodeCursor(DateTimeOffset occurredAt, string eventId) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(
            $"{occurredAt.UtcDateTime:O}|{eventId}"));


    private static bool TryDecodeCursor(
        string cursor, out DateTimeOffset? occurredAt, out string? eventId)
    {
        occurredAt = null;
        eventId = null;

        try
        {
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(cursor)).Split('|', 2);

            if (parts.Length != 2
                || !DateTimeOffset.TryParse(parts[0], CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                        out var parsed))
            {
                return false;
            }

            occurredAt = parsed;
            eventId = parts[1];
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

}
