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

/// <summary>Detection ingest — the sink the standalone AI worker POSTs to.</summary>
/// <remarks>
/// The worker (<c>ai-worker/</c>, Python) discovers cameras from <c>GET /vms/{id}/cameras</c>,
/// runs vehicle/plate/OCR inference itself, and submits results here. This API never runs
/// inference and never touches video — the same boundary the rest of this platform keeps.
/// </remarks>
public static class DetectionEndpoints
{
    private const string SnapshotBase64Key = "snapshotBase64";
    private const string SnapshotPathKey = "snapshotPath";
    /// <summary>When present and equal to <c>"gzip"</c>, <c>snapshotBase64</c> decodes to a
    /// gzip-compressed JPEG rather than a raw one (v1.31) — the worker's own choice to keep
    /// evidence at full resolution/quality and bound size losslessly instead. Any other value,
    /// or the key's absence, means the decoded bytes are the image as-is.</summary>
    private const string SnapshotEncodingKey = "snapshotEncoding";
    private const string GzipEncoding = "gzip";
    private const string VehicleTypeKey = "vehicleType";
    private const string PlateNumberKey = "plateNumber";

    /// <summary>
    /// Hard cap on the ingest request body, applied at the route. Comfortably above a 4 MB
    /// base64 snapshot (~5.5 MB encoded) plus the JSON envelope, and far below Kestrel's 30 MB
    /// default — a detection POST has no reason to be larger (finding 15-H2).
    /// </summary>
    private const long MaxIngestBodyBytes = 8L * 1024 * 1024;

    /// <summary>Bulk items never carry <c>snapshotBase64</c> (v1.30 doc note — evidence stays on
    /// disk under <c>evidence_dir</c>, only metadata batches), so this is sized for up to 500
    /// small JSON items rather than for images: comfortably above worst case, still far below
    /// Kestrel's 30 MB default.</summary>
    private const long MaxBulkIngestBodyBytes = 4L * 1024 * 1024;

    /// <summary>Same ceiling as <c>POST /cameras/bulk-import</c> — 500 rows is the shape this
    /// codebase already uses for a synchronous, per-row-savepoint bulk write.</summary>
    private const int MaxBulkItems = 500;

    // The values ai-worker/pipeline.py produces. Kept as a closed set for the same reason camera
    // type is (finding 15-L2): a free-text event_type that search and dashboards then can't
    // reason about is worse than a 400 when a new worker version needs a new entry here.
    private static readonly HashSet<string> DetectionEventTypes =
        new(["ANPR_DETECTED", "VEHICLE_DETECTED"], StringComparer.Ordinal);

    private static readonly System.Buffers.SearchValues<char> SafeIdChars =
        System.Buffers.SearchValues.Create(
            "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_");

    /// <summary>
    /// The detection <c>id</c> is caller-supplied and becomes both the evidence filename and the
    /// idempotency key. Constrain it to a safe token so a crafted value such as
    /// <c>../../etc/cron.d/x</c> cannot escape the evidence root. The worker sends
    /// <c>evt-{uuid:n}</c>, which this admits.
    /// </summary>
    /// <summary>
    /// Finding 15-L2: <c>eventType</c> must be a known value (a free-text type that search and
    /// dashboards can't reason about is worse than a 400), and <c>confidence</c> must be a real
    /// probability — confidence is never presented as more certain than it is. The DB CHECK is
    /// the backstop; this gives a named 400 instead of an opaque constraint violation.
    /// </summary>
    internal static ProblemHttpResult? ValidateSubmission(string? eventType, double confidence)
    {
        // Contains(null) throws with an explicit comparer — a missing/null eventType is just the
        // invalid-value 400.
        if (eventType is null || !DetectionEventTypes.Contains(eventType))
        {
            return TypedResults.Problem(
                title: "Unknown event type",
                detail: $"eventType must be one of: {string.Join(", ", DetectionEventTypes.Order())}.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        // Written as the negation of the valid range so NaN (which is neither < 0 nor > 1) is
        // also rejected.
        if (!(confidence >= 0 && confidence <= 1))
        {
            return TypedResults.Problem(
                title: "Confidence out of range",
                detail: "confidence must be a number between 0 and 1 inclusive.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        return null;
    }

    private static bool IsSafeDetectionId(string? id) =>
        !string.IsNullOrEmpty(id)
        && id.Length <= 128
        && !id.AsSpan().ContainsAnyExcept(SafeIdChars);

    public static void MapDetectionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/detections")
            .WithTags(ApiTags.Detections).RequireAuthorization();

        group.MapPost("/", IngestAsync)
          .RequirePermission("observation.write")
          .WithMetadata(new RequestSizeLimitAttribute(MaxIngestBodyBytes))
          .WithSummary("Submit a vehicle/plate/OCR detection")
          .WithDescription(
              "The ingest sink for the standalone AI worker. Idempotent on `id` — a "
              + "retried POST for an already-seen detection is accepted again but changes "
              + "nothing and never raises a second watchlist alert. A `POST` reusing an `id` "
              + "with **different** content is a `409` — ids must be unique per detection, not "
              + "reused across distinct submissions.\n\n"
              + "`cameraId` is either `\"{targetId}:{nativeCameraId}\"` (a VMS-federated camera, "
              + "exactly as the worker's own camera discovery builds it from "
              + "`GET /vms/{id}/cameras`) or a bare registry camera UUID (a standalone camera "
              + "claimed via `POST /worker-health/cameras/claim`, v1.27/v1.28) — either shape a "
              + "worker's claimed camera list hands back verbatim. An id that does not resolve "
              + "against the camera inventory **the caller can reach** is rejected as "
              + "`400 Unknown camera` — a detection cannot be scoped to an organization without a "
              + "known camera, and a camera outside the caller's own scope is indistinguishable "
              + "from one that does not exist at all.\n\n"
              + "`eventType` must be `ANPR_DETECTED` or `VEHICLE_DETECTED`; `confidence` must be "
              + "between 0 and 1 — both are 400 otherwise.\n\n"
              + "`evidence.snapshotBase64`, if present, is decoded and written under the "
              + "configured evidence root; otherwise `evidence.snapshotPath` (today's "
              + "worker-local path) is stored verbatim as an opaque reference. The snapshot is "
              + "capped at `Evidence:MaxSnapshotBytes` (default 4 MB) and the whole request at "
              + "8 MB — an oversized or malformed `snapshotBase64` is a 400. Returns 202: ingest "
              + "is fire-and-forget from the worker's perspective, matching its at-least-once, "
              + "best-effort submit.");

        group.MapPost("/bulk", BulkIngestAsync)
          .RequirePermission("observation.write")
          .WithMetadata(new RequestSizeLimitAttribute(MaxBulkIngestBodyBytes))
          .WithSummary("Submit a batch of vehicle/plate/OCR detections")
          .WithDescription(
              "The bulk counterpart to `POST /` (v1.30) — the worker's routine-detection flush "
              + "path, so it isn't one HTTP round trip per detection. **A watchlist-matching "
              + "plate must never wait for a batch window: submit it through `POST /` "
              + "immediately** — this route is for detections a worker has already decided are "
              + "not watchlist-relevant.\n\n"
              + "`items` is 1..500, same per-item shape as `POST /`'s body. Each item is "
              + "validated, resolved and inserted independently — one bad item (unknown camera, "
              + "invalid event type, a reused `id` with different content) does not fail the "
              + "others, matching `POST /cameras/bulk-import`'s per-row-savepoint shape. Always "
              + "200: check each entry's own `status` (`inserted`, `duplicate`, `conflict`, "
              + "`error`) rather than the HTTP status code. A submitter retrying from local "
              + "storage after a prior failure should drop `inserted`/`duplicate` items and keep "
              + "resubmitting `error` items; a `conflict` item reused an id and must not be "
              + "retried verbatim — it needs a fresh id.");

        group.MapGet("/", SearchAsync)
          .RequirePermission("observation.read")
          .WithSummary("Search recent detections")
          .WithDescription(
              "Detections within a time window, optionally filtered by plate number or VMS "
              + "target — the \"search metadata\" step of the demo path. `from`/`to` default to "
              + "the last 24 hours and may span at most 31 days (400 otherwise). `limit` is "
              + "capped at 500 for an open search, or 5000 when filtered to one `plateNumber` — "
              + "a month of one plate is a bounded investigation result. Each result's `tags` "
              + "carries any free-form operator tags already attached (see `POST "
              + "/{eventId}/tags`).");

        group.MapPost("/{eventId}/tags", AddTagAsync)
          .RequirePermission("observation.write")
          .WithSummary("Attach a free-form operator tag to a detection")
          .WithDescription(
              "A human-entered label — \"reviewed\", \"false positive\", \"priority-follow-up\", "
              + "anything the operator wants — kept separate from `eventType`'s closed machine "
              + "classification (`ANPR_DETECTED`/`VEHICLE_DETECTED`). `occurredAt` must match the "
              + "detection's own `timestamp` (its primary key is `(occurredAt, eventId)` "
              + "together); every search result already carries it. Adding a tag the detection "
              + "already has (case-insensitively) is a no-op success, not a conflict. Out-of-scope "
              + "or unknown `eventId`/`occurredAt` is 404.");

        group.MapDelete("/{eventId}/tags/{tag}", RemoveTagAsync)
          .RequirePermission("observation.write")
          .WithSummary("Remove an operator tag from a detection")
          .WithDescription("Matches case-insensitively. Removing a tag that isn't present, or an "
              + "out-of-scope/unknown detection, is 404.");

        group.MapGet("/{eventId}/evidence", GetEvidenceAsync)
          .RequirePermission("observation.read")
          .WithSummary("Fetch a detection's evidence snapshot image")
          .WithDescription(
              "The annotated frame the AI worker saved for this detection — the plate boxed and "
              + "labelled for `ANPR_DETECTED`, or a plain vehicle crop for `VEHICLE_DETECTED`. "
              + "Returns the raw image bytes (`image/jpeg`). `occurredAt` is required — "
              + "`detection_event`'s primary key is `(occurredAt, eventId)` together, same as "
              + "`POST /{eventId}/tags` — every search result already carries it. 404 if the "
              + "detection carries no snapshot, is out of the caller's scope, or is unknown; the "
              + "three are indistinguishable on purpose.");
    }

    /// <summary>Outcome of processing one submitted detection against an already-open
    /// <see cref="UnitOfWork"/> — shared by the single-item and bulk ingest routes so the two
    /// never drift on validation, camera resolution, evidence handling or watchlist matching.</summary>
    private enum ItemOutcome { Inserted, Duplicate, Conflict, Error }

    private static async Task<(ItemOutcome Outcome, string Title, string? Error)> IngestOneAsync(
        DetectionEventRequest request, DetectionRepository detections, WatchlistRepository watchlist,
        IConfiguration configuration, CallerContext caller, UnitOfWork work,
        CancellationToken ct)
    {
        if (!IsSafeDetectionId(request.Id))
        {
            return (ItemOutcome.Error, "Invalid detection id",
                "id must be 1-128 characters of ASCII letters, digits, '-' or '_'.");
        }

        if (ValidateSubmission(request.EventType, request.Confidence) is { } bad)
        {
            return (ItemOutcome.Error, bad.ProblemDetails.Title ?? "Invalid submission", bad.ProblemDetails.Detail);
        }

        var resolved = await detections.ResolveCameraAsync(request.CameraId, caller, ct);
        if (resolved is null)
        {
            return (ItemOutcome.Error, "Unknown camera",
                $"'{request.CameraId}' does not match any camera this caller can reach. "
                + "A detection cannot be scoped without a known camera.");
        }

        var (targetId, nativeCameraId, cameraId, organizationUnitId, geographicAreaId) = resolved.Value;

        var plateRaw = request.Attributes?.GetValueOrDefault(PlateNumberKey);
        var plateNormalized = string.IsNullOrEmpty(plateRaw)
            ? null
            : PlateNormalizer.Normalize(plateRaw);

        var (snapshotReference, imageData, contentEncoding, evidenceError) =
            ResolveSnapshotReference(request, configuration);
        if (evidenceError is not null)
        {
            return (ItemOutcome.Error, "Invalid evidence", evidenceError);
        }

        var evt = new DetectionEvent
        {
            Id = request.Id,
            CameraId = request.CameraId,
            EventType = request.EventType,
            Timestamp = request.Timestamp,
            Confidence = request.Confidence,
            VehicleType = request.Attributes?.GetValueOrDefault(VehicleTypeKey),
            PlateNumberRaw = plateRaw,
            SnapshotReference = snapshotReference,
        };

        var outcome = await detections.IngestAsync(
            evt, targetId, nativeCameraId, cameraId, organizationUnitId, geographicAreaId,
            plateNormalized, caller, work, ct);

        // Finding 15-L1: the id is caller-supplied, so a same-id submission with different
        // content is a real collision, not a retry — surface it rather than silently keeping
        // whichever payload arrived first.
        if (outcome is DetectionIngestOutcome.DuplicateConflict)
        {
            return (ItemOutcome.Conflict, "Detection id already used",
                $"'{request.Id}' was already stored with different content. Detection ids must "
                + "be unique per submission — retry with a fresh id.");
        }

        if (outcome is DetectionIngestOutcome.Inserted)
        {
            if (imageData is not null)
            {
                // Same transaction as the detection insert above — a snapshot and the detection
                // it's evidence for must never be able to diverge (one committed without the
                // other).
                await detections.SaveSnapshotAsync(
                    evt.Id, evt.Timestamp, imageData, "image/jpeg", contentEncoding, work, ct);
            }

            await work.AuditAsync(caller, "ingest", "detection_event", evt.Id,
                before: null,
                after: new { evt.CameraId, evt.EventType, evt.VehicleType, PlateNumber = plateRaw },
                organizationUnitId, ct);

            if (!string.IsNullOrEmpty(plateRaw))
            {
                var variants = PlateNormalizer.NormalizedVariants(plateRaw);
                var match = await watchlist.FindActiveMatchAsync(
                    organizationUnitId, variants, work, ct);

                if (match is not null)
                {
                    await watchlist.RaiseAlertAsync(match.Id, evt.Id, evt.Timestamp, work, ct);
                }
            }

            return (ItemOutcome.Inserted, "", null);
        }

        return (ItemOutcome.Duplicate, "", null);
    }

    private static async Task<Results<Accepted, ProblemHttpResult>> IngestAsync(
        [FromBody] DetectionEventRequest request, DetectionRepository detections,
        WatchlistRepository watchlist, NpgsqlDataSource db, IConfiguration configuration,
        HttpContext http, CancellationToken ct)
    {
        var caller = CallerContextFactory.From(http);

        await using var work = await UnitOfWork.BeginAsync(db, ct);

        var (outcome, title, error) = await IngestOneAsync(
            request, detections, watchlist, configuration, caller, work, ct);

        if (outcome is ItemOutcome.Error)
        {
            return TypedResults.Problem(
                title: title, detail: error, statusCode: StatusCodes.Status400BadRequest);
        }

        if (outcome is ItemOutcome.Conflict)
        {
            return TypedResults.Problem(
                title: title, detail: error, statusCode: StatusCodes.Status409Conflict);
        }

        await work.CommitAsync(ct);

        return TypedResults.Accepted((string?)null);
    }

    private static async Task<Results<Ok<DetectionBulkResponse>, ProblemHttpResult>> BulkIngestAsync(
        [FromBody] DetectionBulkRequest request, DetectionRepository detections,
        WatchlistRepository watchlist, NpgsqlDataSource db, IConfiguration configuration,
        HttpContext http, CancellationToken ct)
    {
        var caller = CallerContextFactory.From(http);

        if (request.Items is null || request.Items.Count == 0 || request.Items.Count > MaxBulkItems)
        {
            return TypedResults.Problem(
                title: "Invalid batch size",
                detail: $"items must contain between 1 and {MaxBulkItems} rows.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var results = new List<DetectionBulkItemResult>(request.Items.Count);
        int inserted = 0, duplicate = 0, failed = 0;

        // Same shape as CameraEndpoints.BulkImportAsync: one connection/transaction for the
        // whole batch, one SAVEPOINT per item — a bad item rolls back to its own savepoint
        // without touching the others or paying for a fresh transaction per item.
        await using var work = await UnitOfWork.BeginAsync(db, ct);

        for (var i = 0; i < request.Items.Count; i++)
        {
            var item = request.Items[i];
            var savepoint = $"bulk_detection_{i}";
            await work.Transaction.SaveAsync(savepoint, ct);

            var (outcome, _, error) = await IngestOneAsync(
                item, detections, watchlist, configuration, caller, work, ct);

            switch (outcome)
            {
                case ItemOutcome.Inserted:
                    await work.Transaction.ReleaseAsync(savepoint, ct);
                    inserted++;
                    results.Add(new DetectionBulkItemResult(i, item.Id, "inserted", null));
                    break;
                case ItemOutcome.Duplicate:
                    await work.Transaction.ReleaseAsync(savepoint, ct);
                    duplicate++;
                    results.Add(new DetectionBulkItemResult(i, item.Id, "duplicate", null));
                    break;
                case ItemOutcome.Conflict:
                    await work.Transaction.RollbackAsync(savepoint, ct);
                    failed++;
                    results.Add(new DetectionBulkItemResult(i, item.Id, "conflict", error));
                    break;
                default:
                    await work.Transaction.RollbackAsync(savepoint, ct);
                    failed++;
                    results.Add(new DetectionBulkItemResult(i, item.Id, "error", error));
                    break;
            }
        }

        await work.CommitAsync(ct);

        return TypedResults.Ok(new DetectionBulkResponse(inserted, duplicate, failed, results));
    }

    /// <summary>
    /// Resolves what a detection's evidence actually is: a caller-supplied <c>snapshotBase64</c>
    /// (v1.30 — decoded here and stored in <c>detection_snapshot</c> by the caller, inside the
    /// same transaction as the detection insert, never written to disk at all), optionally
    /// gzip-compressed per <c>snapshotEncoding</c> (v1.31 — the bytes are stored exactly as
    /// received, compressed or not; this never decompresses them, only records which they are),
    /// or a bare <c>snapshotPath</c> — the worker's own local-disk reference, stored verbatim
    /// exactly as before v1.30, for a worker/API pair that don't share a filesystem.
    /// </summary>
    /// <remarks>
    /// The error element is non-null when the caller-supplied <c>snapshotBase64</c> is rejected
    /// — oversized or not valid base64 — which the endpoint turns into a 400 rather than letting
    /// a <see cref="FormatException"/> or an unbounded decode reach the write (finding 15-H2).
    /// </remarks>
    private static (string? Reference, byte[]? ImageData, string? ContentEncoding, string? Error)
        ResolveSnapshotReference(DetectionEventRequest request, IConfiguration configuration)
    {
        var base64 = request.Evidence?.GetValueOrDefault(SnapshotBase64Key);
        if (string.IsNullOrEmpty(base64))
        {
            return (request.Evidence?.GetValueOrDefault(SnapshotPathKey), null, null, null);
        }

        var maxBytes = configuration.GetValue(
            "Evidence:MaxSnapshotBytes", DetectionEvidence.DefaultMaxSnapshotBytes);

        if (!DetectionEvidence.TryDecode(base64, maxBytes, out var snapshot, out var error))
        {
            return (null, null, null, error);
        }

        var contentEncoding = request.Evidence?.GetValueOrDefault(SnapshotEncodingKey) == GzipEncoding
            ? GzipEncoding
            : null;

        return (DetectionRepository.DatabaseSnapshotMarker, snapshot, contentEncoding, null);
    }

    /// <summary>The widest window a single search may span — matches the events feed (14-M1).</summary>
    private static readonly TimeSpan MaxSearchWindow = TimeSpan.FromDays(31);

    /// <summary>Hard ceiling on <c>limit</c> for an open search (no plate filter).</summary>
    private const int MaxSearchRows = 500;

    /// <summary>
    /// Ceiling when the search is filtered to one plate — a single plate over the max window is
    /// a bounded, legitimate investigation result, so the open-search cap would truncate it.
    /// </summary>
    private const int MaxSearchRowsByPlate = 5000;

    private static async Task<Results<Ok<IReadOnlyList<DetectionResponse>>, ProblemHttpResult>> SearchAsync(
        string? plateNumber, Guid? targetId, DateTimeOffset? from, DateTimeOffset? to, int? limit,
        DetectionRepository detections, HttpContext http, CancellationToken ct)
    {
        var caller = CallerContextFactory.From(http);
        var end = to ?? DateTimeOffset.UtcNow;
        var start = from ?? end.AddHours(-24);

        if (start >= end)
        {
            return TypedResults.Problem(
                title: "Invalid time range", detail: "'to' must be after 'from'.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        // Without a cap a caller could ask for the entire retained history of a busy corridor in
        // one query — an unbounded partition scan (finding 15-M2). The events feed has the same
        // rule; narrow the range or make several requests.
        if (end - start > MaxSearchWindow)
        {
            return TypedResults.Problem(
                title: "Time range too wide",
                detail: $"A detection search may span at most {MaxSearchWindow.TotalDays:F0} days. "
                      + "Narrow `from`/`to`.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var plateNormalized = string.IsNullOrEmpty(plateNumber)
            ? null
            : PlateNormalizer.Normalize(plateNumber);

        var cap = plateNormalized is null ? MaxSearchRows : MaxSearchRowsByPlate;
        var rows = await detections.SearchAsync(
            plateNormalized, targetId, start, end,
            Math.Clamp(limit ?? 100, 1, cap), caller, ct);

        var tagsByEvent = await detections.GetTagsForEventsAsync([.. rows.Select(r => r.EventId)], ct);

        return TypedResults.Ok<IReadOnlyList<DetectionResponse>>(
        [
            .. rows.Select(r => new DetectionResponse(
                r.EventId,
                r.TargetId is { } targetId ? $"{targetId}:{r.NativeCameraId}" : r.CameraId?.ToString() ?? "",
                r.CameraId, r.CameraName, r.EventType,
                r.OccurredAt, r.Confidence, r.VehicleType, r.PlateNumberRaw, r.SnapshotReference,
                [.. tagsByEvent[r.EventId]])),
        ]);
    }

    /// <summary>1-40 characters, trimmed — the same shape v1.26's own CHECK constraint enforces;
    /// checked here too for a named 400 instead of an opaque constraint violation (same posture
    /// as <see cref="ValidateSubmission"/>).</summary>
    private static bool IsValidTag(string? tag) =>
        tag is not null && tag.Trim().Length is >= 1 and <= 40;

    private static async Task<Results<Ok, ProblemHttpResult, NotFound>> AddTagAsync(
        string eventId, [FromBody] DetectionTagRequest request,
        DetectionRepository detections, NpgsqlDataSource db, HttpContext http, CancellationToken ct)
    {
        var caller = CallerContextFactory.From(http);

        if (!IsValidTag(request.Tag))
        {
            return TypedResults.Problem(
                title: "Invalid tag",
                detail: "tag must be 1-40 characters.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var tag = request.Tag.Trim();

        await using var work = await UnitOfWork.BeginAsync(db, ct);

        var outcome = await detections.AddTagAsync(eventId, request.OccurredAt, tag, caller, work, ct);
        if (outcome is DetectionTagOutcome.DetectionNotFound)
        {
            return TypedResults.NotFound();
        }

        if (outcome is DetectionTagOutcome.Added)
        {
            await work.AuditAsync(caller, "tag", "detection_event", eventId,
                before: null, after: new { Tag = tag }, organizationUnitId: null, ct);
        }

        await work.CommitAsync(ct);
        return TypedResults.Ok();
    }

    private static async Task<Results<Ok, NotFound>> RemoveTagAsync(
        string eventId, string tag,
        DetectionRepository detections, NpgsqlDataSource db, HttpContext http, CancellationToken ct)
    {
        var caller = CallerContextFactory.From(http);

        await using var work = await UnitOfWork.BeginAsync(db, ct);

        var removed = await detections.RemoveTagAsync(eventId, tag, caller, work, ct);
        if (!removed)
        {
            return TypedResults.NotFound();
        }

        await work.AuditAsync(caller, "untag", "detection_event", eventId,
            before: new { Tag = tag }, after: null, organizationUnitId: null, ct);
        await work.CommitAsync(ct);
        return TypedResults.Ok();
    }

    private static readonly Dictionary<string, string> EvidenceContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".png"] = "image/png",
    };

    private static async Task<Results<PhysicalFileHttpResult, FileContentHttpResult, NotFound>> GetEvidenceAsync(
        string eventId, DateTimeOffset occurredAt, DetectionRepository detections,
        EvidenceStorage evidence, HttpContext http, CancellationToken ct)
    {
        var caller = CallerContextFactory.From(http);

        var reference = await detections.GetSnapshotReferenceAsync(eventId, occurredAt, caller, ct);
        if (string.IsNullOrEmpty(reference))
        {
            return TypedResults.NotFound();
        }

        // v1.30: the common case now — the image lives in detection_snapshot, not on any
        // filesystem at all. GetSnapshotAsync re-applies the same org/geo scope check
        // GetSnapshotReferenceAsync just did; cheap, and it keeps this route's authorization
        // story in one place per storage path rather than trusting the marker alone.
        if (reference == DetectionRepository.DatabaseSnapshotMarker)
        {
            var snapshot = await detections.GetSnapshotAsync(eventId, occurredAt, caller, ct);
            if (snapshot is null)
            {
                return TypedResults.NotFound();
            }

            // image_data is stored exactly as the worker sent it (v1.31: often gzip-compressed,
            // to keep evidence at full resolution/quality rather than resized or quality-reduced
            // — see detection_snapshot's own doc comment). Content-Encoding is the standard HTTP
            // way to say so: the browser decompresses transparently, so nothing here or on the
            // frontend needs its own gzip/gunzip code. No response-compression middleware is
            // configured in this API, so there's no risk of a second, redundant compression pass
            // on top of this one.
            if (snapshot.Value.ContentEncoding is { } contentEncoding)
            {
                http.Response.Headers.ContentEncoding = contentEncoding;
            }

            return TypedResults.File(snapshot.Value.ImageData, snapshot.Value.ContentType);
        }

        // `reference` is either an absolute worker-local path stored verbatim (the common case
        // — a worker that only ever sent snapshotPath, never the bytes) or relative — this API's
        // own convention for a snapshotBase64 it decoded and wrote itself
        // (ResolveSnapshotReferenceAsync), stored as "{evidenceRoot's own folder name}/
        // {filename}". Either way the folder segment is redundant with whichever root the file
        // actually lives under, so only the filename is combined with each candidate root in
        // turn — Root first, then each configured AllowedExternalRoots entry (see its own doc
        // comment for why an absolute worker-supplied path is never trusted without this
        // containment check).
        string? candidatePath = null;
        if (Path.IsPathFullyQualified(reference))
        {
            var resolved = Path.GetFullPath(reference);
            if (evidence.IsPathAllowed(resolved) && File.Exists(resolved))
            {
                candidatePath = resolved;
            }
        }
        else
        {
            var fileName = Path.GetFileName(reference);
            foreach (var root in (string[]) [evidence.Root, .. evidence.AllowedExternalRoots])
            {
                var resolved = Path.GetFullPath(Path.Combine(root, fileName));
                if (evidence.IsPathAllowed(resolved) && File.Exists(resolved))
                {
                    candidatePath = resolved;
                    break;
                }
            }
        }

        if (candidatePath is null)
        {
            return TypedResults.NotFound();
        }

        var contentType = EvidenceContentTypes.GetValueOrDefault(
            Path.GetExtension(candidatePath), "application/octet-stream");
        return TypedResults.PhysicalFile(candidatePath, contentType);
    }
}
