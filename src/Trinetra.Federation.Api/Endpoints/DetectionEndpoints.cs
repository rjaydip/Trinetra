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

/// <summary>Detection ingest — the sink Model 2's standalone AI worker POSTs to.</summary>
/// <remarks>
/// The worker (<c>ai-worker/</c>, Python) discovers cameras from <c>GET /vms/{id}/cameras</c>,
/// runs vehicle/plate/OCR inference itself, and submits results here. This API never runs
/// inference and never touches video — Model 3's own boundary applies to Model 2 as well.
/// </remarks>
public static class DetectionEndpoints
{
    private const string SnapshotBase64Key = "snapshotBase64";
    private const string SnapshotPathKey = "snapshotPath";
    private const string VehicleTypeKey = "vehicleType";
    private const string PlateNumberKey = "plateNumber";

    /// <summary>
    /// Hard cap on the ingest request body, applied at the route. Comfortably above a 4 MB
    /// base64 snapshot (~5.5 MB encoded) plus the JSON envelope, and far below Kestrel's 30 MB
    /// default — a detection POST has no reason to be larger (finding 15-H2).
    /// </summary>
    private const long MaxIngestBodyBytes = 8L * 1024 * 1024;

    private static readonly System.Buffers.SearchValues<char> SafeIdChars =
        System.Buffers.SearchValues.Create(
            "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_");

    /// <summary>
    /// The detection <c>id</c> is caller-supplied and becomes both the evidence filename and the
    /// idempotency key. Constrain it to a safe token so a crafted value such as
    /// <c>../../etc/cron.d/x</c> cannot escape the evidence root. The worker sends
    /// <c>evt-{uuid:n}</c>, which this admits.
    /// </summary>
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
              "The ingest sink for Model 2's standalone AI worker. Idempotent on `id` — a "
              + "retried POST for an already-seen detection is accepted again but changes "
              + "nothing and never raises a second watchlist alert.\n\n"
              + "`cameraId` is `\"{targetId}:{nativeCameraId}\"`, exactly as the worker's own "
              + "camera discovery builds it from `GET /vms/{id}/cameras`. An id that does not "
              + "resolve against the camera inventory is rejected — a detection cannot be scoped "
              + "to an organization without a known camera.\n\n"
              + "`evidence.snapshotBase64`, if present, is decoded and written under the "
              + "configured evidence root; otherwise `evidence.snapshotPath` (today's "
              + "worker-local path) is stored verbatim as an opaque reference. The snapshot is "
              + "capped at `Evidence:MaxSnapshotBytes` (default 4 MB) and the whole request at "
              + "8 MB — an oversized or malformed `snapshotBase64` is a 400. Returns 202: ingest "
              + "is fire-and-forget from the worker's perspective, matching its at-least-once, "
              + "best-effort submit.");

        group.MapGet("/", SearchAsync)
          .RequirePermission("observation.read")
          .WithSummary("Search recent detections")
          .WithDescription(
              "Detections within a time window, optionally filtered by plate number or VMS "
              + "target — the \"search metadata\" step of the demo path. `from`/`to` default to "
              + "the last 24 hours and may span at most 31 days (400 otherwise). `limit` is "
              + "capped at 500 for an open search, or 5000 when filtered to one `plateNumber` — "
              + "a month of one plate is a bounded investigation result.");
    }

    private static async Task<Results<Accepted, ProblemHttpResult>> IngestAsync(
        [FromBody] DetectionEventRequest request, DetectionRepository detections,
        WatchlistRepository watchlist, NpgsqlDataSource db, IConfiguration configuration,
        HttpContext http, CancellationToken ct)
    {
        var caller = CallerContextFactory.From(http);

        if (!IsSafeDetectionId(request.Id))
        {
            return TypedResults.Problem(
                title: "Invalid detection id",
                detail: "id must be 1-128 characters of ASCII letters, digits, '-' or '_'.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var resolved = await detections.ResolveCameraAsync(request.CameraId, ct);
        if (resolved is null)
        {
            return TypedResults.Problem(
                title: "Unknown camera",
                detail: $"'{request.CameraId}' does not match any camera this VMS reported. "
                      + "A detection cannot be scoped without a known camera.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var (targetId, nativeCameraId, cameraId, organizationUnitId, geographicAreaId) = resolved.Value;

        var plateRaw = request.Attributes?.GetValueOrDefault(PlateNumberKey);
        var plateNormalized = string.IsNullOrEmpty(plateRaw)
            ? null
            : PlateNormalizer.Normalize(plateRaw);

        var (snapshotReference, evidenceError) =
            await ResolveSnapshotReferenceAsync(request, configuration, ct);
        if (evidenceError is not null)
        {
            return TypedResults.Problem(
                title: "Invalid evidence",
                detail: evidenceError,
                statusCode: StatusCodes.Status400BadRequest);
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

        await using var work = await UnitOfWork.BeginAsync(db, ct);

        var inserted = await detections.IngestAsync(
            evt, targetId, nativeCameraId, cameraId, organizationUnitId, geographicAreaId,
            plateNormalized, caller, work, ct);

        if (inserted)
        {
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
        }

        await work.CommitAsync(ct);

        return TypedResults.Accepted((string?)null);
    }

    /// <summary>
    /// Resolves the snapshot reference stored on the detection row: a bare path when the worker
    /// sends one, or the relative path of a base64 snapshot decoded under the evidence root.
    /// </summary>
    /// <remarks>
    /// The second element is non-null when the caller-supplied <c>snapshotBase64</c> is rejected
    /// — oversized or not valid base64 — which the endpoint turns into a 400 rather than letting
    /// a <see cref="FormatException"/> or an unbounded decode reach the write (finding 15-H2).
    /// </remarks>
    private static async Task<(string? Reference, string? Error)> ResolveSnapshotReferenceAsync(
        DetectionEventRequest request, IConfiguration configuration, CancellationToken ct)
    {
        var base64 = request.Evidence?.GetValueOrDefault(SnapshotBase64Key);
        if (string.IsNullOrEmpty(base64))
        {
            return (request.Evidence?.GetValueOrDefault(SnapshotPathKey), null);
        }

        var maxBytes = configuration.GetValue(
            "Evidence:MaxSnapshotBytes", DetectionEvidence.DefaultMaxSnapshotBytes);

        if (!DetectionEvidence.TryDecode(base64, maxBytes, out var snapshot, out var error))
        {
            return (null, error);
        }

        var root = Path.GetFullPath(configuration["Evidence:RootPath"] ?? "evidence");
        Directory.CreateDirectory(root);

        var fileName = $"{request.Id}.jpg";
        var fullPath = Path.GetFullPath(Path.Combine(root, fileName));

        // Defence in depth behind IsSafeDetectionId: the write must never land outside the
        // configured root even if the id check is ever weakened.
        if (!fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Evidence path escaped the configured root.");
        }

        await File.WriteAllBytesAsync(fullPath, snapshot, ct);

        return (Path.Combine(Path.GetFileName(root), fileName), null);
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

        return TypedResults.Ok<IReadOnlyList<DetectionResponse>>(
        [
            .. rows.Select(r => new DetectionResponse(
                r.EventId, $"{r.TargetId}:{r.NativeCameraId}", r.CameraId, r.EventType,
                r.OccurredAt, r.Confidence, r.VehicleType, r.PlateNumberRaw, r.SnapshotReference)),
        ]);
    }
}
