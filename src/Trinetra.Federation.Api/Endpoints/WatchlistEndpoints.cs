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
          .WithPaginatedResponse<WatchlistEntryResponse>()
          .RequirePermission("alert.read")
          .WithSummary("List watchlist entries")
          .WithDescription(
              "Entries within the caller's organization reach. Filter with `active` "
              + "(`true` / `false` — omit for both). "
              + Paginate.Doc);

        group.MapPost("/", CreateAsync)
          .RequirePermission("watchlist.manage")
          .WithSummary("Add a plate to the watchlist")
          .WithDescription(
              "The plate number is normalized before storage (see `PlateNormalizer` — "
              + "\"MH-12-AB-1234\" / \"mh12ab1234\" both store as \"MH12AB1234\"), so ingest-time "
              + "matching against OCR output does not depend on formatting. One active entry per "
              + "plate per organization.\n\n"
              + "Also backfills: any detection already on file for this plate (in this "
              + $"organization) raises an alert immediately, up to {HistoricalMatchesCap} most "
              + "recent — the plate is not only watched going forward.");

        group.MapDelete("/{id:guid}", DeactivateAsync)
          .RequirePermission("watchlist.manage")
          .WithSummary("Remove a plate from the watchlist")
          .WithDescription(
              "Deactivates the entry rather than hard-deleting it — past alerts it raised keep "
              + "their reason on record. A later entry for the same plate is unaffected.");

        group.MapGet("/alerts", ListAlertsAsync)
          .WithPaginatedResponse<WatchlistAlertResponse>()
          .RequirePermission("alert.read")
          .WithSummary("List raised watchlist alerts")
          .WithDescription(
              "Newest first. Raised automatically when a freshly-ingested detection's plate "
              + "matches an active watchlist entry — never by a client calling this API.\n\n"
              + "Filters: `acknowledged` (`true` / `false` — omit for both), `plate` (exact "
              + "normalised plate), `entryId` (one watchlist entry), `severity`, and "
              + "`from`/`to` on the raised time. "
              + Paginate.Doc);

        group.MapPost("/alerts/{id:guid}/acknowledge", AcknowledgeAsync)
          .RequirePermission("alert.acknowledge")
          .WithSummary("Acknowledge a watchlist alert")
          .WithDescription(
              "Idempotent: acknowledging an already-acknowledged alert is a 204 no-op (no second "
              + "audit row). 404 only when the alert id is unknown.");
    }

    private const int EntriesHardCap = 1000;

    private static async Task<IResult> ListAsync(
        bool? active, int? page, int? pageSize,
        WatchlistRepository repo, HttpContext http, CancellationToken ct)
    {
        var q = new PageQuery(page, pageSize);
        var entries = await repo.ListAsync(
            active,
            new PageWindow(q.Limit(EntriesHardCap, EntriesHardCap), q.Offset(EntriesHardCap)),
            CallerContextFactory.From(http), ct);

        return Paginate.Render(http, q, EntriesHardCap,
            [.. entries.Items.Select(e => new WatchlistEntryResponse(
                e.Id, e.OrganizationUnitId, e.PlateNumberNormalized, e.Reason, e.Severity,
                e.IsActive, e.CreatedAt))],
            entries.Total);
    }

    private static readonly HashSet<string> Severities =
        new(["Low", "Medium", "High", "Critical"], StringComparer.Ordinal);

    private const int MaxReasonLength = 500;

    // Same order of magnitude as the list-endpoint hard caps (EntriesHardCap / AlertsHardCap):
    // a plate with more prior sightings than this is itself worth surfacing as "capped" rather
    // than silently scanning (and alerting on) an unbounded amount of history.
    private const int HistoricalMatchesCap = 1000;

    private static async Task<Results<Created<WatchlistEntryCreatedResponse>, ProblemHttpResult>> CreateAsync(
        [FromBody] CreateWatchlistEntryRequest request, WatchlistRepository repo,
        NpgsqlDataSource db, HttpContext http, CancellationToken ct)
    {
        var caller = CallerContextFactory.From(http);

        // Named 400s ahead of the DB (finding 16-L3): severity has a CHECK constraint, and a
        // plate that normalizes to nothing would otherwise be stored as an empty watchlist entry.
        var plate = PlateNormalizer.Normalize(request.PlateNumber);
        var error =
            string.IsNullOrEmpty(plate)
                ? "plateNumber must contain at least one letter or digit."
            // Guard empty/null before the membership test — Severities.Contains(null) throws
            // with an explicit comparer. (RespectNullableAnnotations already rejects an explicit
            // "severity": null at deserialization; this covers "" and defence in depth.)
            : string.IsNullOrEmpty(request.Severity) || !Severities.Contains(request.Severity)
                ? $"severity must be one of: {string.Join(", ", Severities)}."
            : request.Reason is { Length: > MaxReasonLength }
                ? $"reason is at most {MaxReasonLength} characters."
                : null;

        if (error is not null)
        {
            return TypedResults.Problem(
                title: "Invalid watchlist entry", detail: error,
                statusCode: StatusCodes.Status400BadRequest);
        }

        var entry = new WatchlistEntry
        {
            OrganizationUnitId = request.OrganizationUnitId,
            PlateNumberNormalized = plate,
            Reason = request.Reason,
            Severity = request.Severity,
        };

        await using var work = await UnitOfWork.BeginAsync(db, ct);

        var id = await repo.CreateAsync(entry, caller, work, ct);

        // Backfill: a plate can already have detection history on file before it's watched —
        // surface those matches as alerts now rather than leaving them only discoverable via a
        // manual GET /detections?plate= search.
        var variants = PlateNormalizer.NormalizedVariants(plate);
        var historical = await repo.FindHistoricalMatchesAsync(
            request.OrganizationUnitId, variants, HistoricalMatchesCap, work, ct);

        foreach (var match in historical)
        {
            await repo.RaiseAlertAsync(id, match.EventId, match.OccurredAt, work, ct);
        }

        await work.AuditAsync(caller, "create", "watchlist_entry", id.ToString(),
            before: null,
            after: new
            {
                entry.PlateNumberNormalized, entry.Reason, entry.Severity,
                historicalAlertsRaised = historical.Count,
                historicalMatchesCapped = historical.Count >= HistoricalMatchesCap,
            },
            request.OrganizationUnitId, ct);
        await work.CommitAsync(ct);

        return TypedResults.Created($"/api/v1/watchlist/{id}", new WatchlistEntryCreatedResponse(
            id, historical.Count, historical.Count >= HistoricalMatchesCap));
    }

    private static async Task<Results<NoContent, NotFound>> DeactivateAsync(
        Guid id, WatchlistRepository repo, NpgsqlDataSource db, HttpContext http,
        CancellationToken ct)
    {
        var caller = CallerContextFactory.From(http);

        await using var work = await UnitOfWork.BeginAsync(db, ct);

        if (await repo.DeactivateAsync(id, caller, work, ct) is not { } entry)
        {
            return TypedResults.NotFound();
        }

        // Record which plate was taken off, under which org — "who removed MH12AB1234 and when".
        await work.AuditAsync(caller, "delete", "watchlist_entry", id.ToString(),
            before: new { entry.PlateNumberNormalized, entry.Reason, entry.Severity },
            after: new { isActive = false },
            entry.OrganizationUnitId, ct);
        await work.CommitAsync(ct);

        return TypedResults.NoContent();
    }

    private const int AlertsHardCap = 1000;

    private static async Task<IResult> ListAlertsAsync(
        bool? acknowledged, string? plate, Guid? entryId, string? severity,
        DateTimeOffset? from, DateTimeOffset? to, int? page, int? pageSize,
        WatchlistRepository repo, HttpContext http, CancellationToken ct)
    {
        var q = new PageQuery(page, pageSize);
        var normalized = string.IsNullOrWhiteSpace(plate) ? null : PlateNormalizer.Normalize(plate);

        var rows = await repo.ListAlertsAsync(
            acknowledged, normalized, entryId,
            string.IsNullOrWhiteSpace(severity) ? null : severity, from, to,
            new PageWindow(q.Limit(AlertsHardCap, AlertsHardCap), q.Offset(AlertsHardCap)),
            CallerContextFactory.From(http), ct);

        return Paginate.Render(http, q, AlertsHardCap,
            [.. rows.Items.Select(r => new WatchlistAlertResponse(
                r.Id, r.WatchlistEntryId, r.PlateNumberNormalized, r.Reason, r.Severity,
                r.DetectionEventId, r.DetectionOccurredAt, r.RaisedAt, r.AcknowledgedAt))],
            rows.Total);
    }

    private static async Task<Results<NoContent, NotFound>> AcknowledgeAsync(
        Guid id, WatchlistRepository repo, NpgsqlDataSource db, HttpContext http,
        CancellationToken ct)
    {
        var caller = CallerContextFactory.From(http);

        await using var work = await UnitOfWork.BeginAsync(db, ct);

        if (await repo.AcknowledgeAlertAsync(id, caller, work, ct) is not { } ack)
        {
            return TypedResults.NotFound();
        }

        // A no-op re-acknowledge changed nothing — no audit row for it (invariant 10: audit
        // rows go with mutations). A real transition is recorded, with the alert's org.
        if (!ack.AlreadyAcknowledged)
        {
            await work.AuditAsync(caller, "acknowledge", "watchlist_alert", id.ToString(),
                before: new { acknowledged = false }, after: new { acknowledged = true },
                ack.OrganizationUnitId, ct);
        }

        await work.CommitAsync(ct);

        return TypedResults.NoContent();
    }
}
