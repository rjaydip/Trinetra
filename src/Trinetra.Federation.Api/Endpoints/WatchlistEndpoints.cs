using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using Trinetra.Federation.Api.Auth;
using Trinetra.Federation.Api.Contracts;
using Trinetra.Federation.Api.OpenApi;
using Trinetra.Federation.Core;
using Trinetra.Federation.Core.Model;
using Trinetra.Federation.Storage;
using Trinetra.Federation.Storage.Repositories;

namespace Trinetra.Federation.Api.Endpoints;

/// <summary>Plate numbers to flag, and the alerts a detection matching one raises.</summary>
public static class WatchlistEndpoints
{
    public static void MapWatchlistEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/watchlist")
            .WithTags(ApiTags.Watchlist).RequireAuthorization();

        group.MapGet("/", ListAsync)
          .RequirePermission("alert.read")
          .WithSummary("List watchlist entries")
          .WithDescription("Every entry within the caller's organization reach, active or not.");

        group.MapPost("/", CreateAsync)
          .RequirePermission("watchlist.manage")
          .WithSummary("Add a plate to the watchlist")
          .WithDescription(
              "The plate number is normalized before storage (see `PlateNormalizer` — "
              + "\"MH-12-AB-1234\" / \"mh12ab1234\" both store as \"MH12AB1234\"), so ingest-time "
              + "matching against OCR output does not depend on formatting. One active entry per "
              + "plate per organization.");

        group.MapDelete("/{id:guid}", DeactivateAsync)
          .RequirePermission("watchlist.manage")
          .WithSummary("Remove a plate from the watchlist")
          .WithDescription(
              "Deactivates the entry rather than hard-deleting it — past alerts it raised keep "
              + "their reason on record. A later entry for the same plate is unaffected.");

        group.MapGet("/alerts", ListAlertsAsync)
          .RequirePermission("alert.read")
          .WithSummary("List raised watchlist alerts")
          .WithDescription(
              "Newest first. Raised automatically when a freshly-ingested detection's plate "
              + "matches an active watchlist entry — never by a client calling this API.");

        group.MapPost("/alerts/{id:guid}/acknowledge", AcknowledgeAsync)
          .RequirePermission("alert.acknowledge")
          .WithSummary("Acknowledge a watchlist alert");
    }

    private static async Task<Ok<IReadOnlyList<WatchlistEntryResponse>>> ListAsync(
        WatchlistRepository repo, HttpContext http, CancellationToken ct)
    {
        var entries = await repo.ListAsync(CallerContextFactory.From(http), ct);

        return TypedResults.Ok<IReadOnlyList<WatchlistEntryResponse>>(
        [
            .. entries.Select(e => new WatchlistEntryResponse(
                e.Id, e.OrganizationUnitId, e.PlateNumberNormalized, e.Reason, e.Severity,
                e.IsActive, e.CreatedAt)),
        ]);
    }

    private static async Task<Created<CreatedResponse>> CreateAsync(
        [FromBody] CreateWatchlistEntryRequest request, WatchlistRepository repo,
        NpgsqlDataSource db, HttpContext http, CancellationToken ct)
    {
        var caller = CallerContextFactory.From(http);

        var entry = new WatchlistEntry
        {
            OrganizationUnitId = request.OrganizationUnitId,
            PlateNumberNormalized = PlateNormalizer.Normalize(request.PlateNumber),
            Reason = request.Reason,
            Severity = request.Severity,
        };

        await using var work = await UnitOfWork.BeginAsync(db, ct);

        var id = await repo.CreateAsync(entry, caller, work, ct);
        await work.AuditAsync(caller, "create", "watchlist_entry", id.ToString(),
            before: null,
            after: new { entry.PlateNumberNormalized, entry.Reason, entry.Severity },
            request.OrganizationUnitId, ct);
        await work.CommitAsync(ct);

        return TypedResults.Created($"/api/v1/watchlist/{id}", new CreatedResponse(id));
    }

    private static async Task<Results<NoContent, NotFound>> DeactivateAsync(
        Guid id, WatchlistRepository repo, NpgsqlDataSource db, HttpContext http,
        CancellationToken ct)
    {
        var caller = CallerContextFactory.From(http);

        await using var work = await UnitOfWork.BeginAsync(db, ct);

        if (!await repo.DeactivateAsync(id, caller, work, ct))
        {
            return TypedResults.NotFound();
        }

        await work.AuditAsync(caller, "delete", "watchlist_entry", id.ToString(),
            before: null, after: null, organizationUnitId: null, ct);
        await work.CommitAsync(ct);

        return TypedResults.NoContent();
    }

    private static async Task<Ok<IReadOnlyList<WatchlistAlertResponse>>> ListAlertsAsync(
        int? limit, WatchlistRepository repo, HttpContext http, CancellationToken ct)
    {
        var rows = await repo.ListAlertsAsync(limit ?? 100, CallerContextFactory.From(http), ct);

        return TypedResults.Ok<IReadOnlyList<WatchlistAlertResponse>>(
        [
            .. rows.Select(r => new WatchlistAlertResponse(
                r.Id, r.WatchlistEntryId, r.PlateNumberNormalized, r.Reason, r.Severity,
                r.DetectionEventId, r.DetectionOccurredAt, r.RaisedAt, r.AcknowledgedAt)),
        ]);
    }

    private static async Task<Results<NoContent, NotFound>> AcknowledgeAsync(
        Guid id, WatchlistRepository repo, NpgsqlDataSource db, HttpContext http,
        CancellationToken ct)
    {
        var caller = CallerContextFactory.From(http);

        await using var work = await UnitOfWork.BeginAsync(db, ct);

        if (!await repo.AcknowledgeAlertAsync(id, caller, work, ct))
        {
            return TypedResults.NotFound();
        }

        await work.AuditAsync(caller, "update", "watchlist_alert", id.ToString(),
            before: null, after: new { acknowledged = true }, organizationUnitId: null, ct);
        await work.CommitAsync(ct);

        return TypedResults.NoContent();
    }
}
