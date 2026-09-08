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
              + "worker-local path) is stored verbatim as an opaque reference. Returns 202: "
              + "ingest is fire-and-forget from the worker's perspective, matching its "
              + "at-least-once, best-effort submit.");

        group.MapGet("/", SearchAsync)
          .RequirePermission("observation.read")
          .WithSummary("Search recent detections")
          .WithDescription(
              "Detections within a time window, optionally filtered by plate number or VMS "
              + "target — the \"search metadata\" step of the demo path. `from`/`to` default to "
              + "the last 24 hours.");
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

        var snapshotReference = await ResolveSnapshotReferenceAsync(request, configuration, ct);

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
    /// Base64 evidence, decoded and written under the evidence root, is the near-term direction
    /// (the worker's next step per the user); a bare path string is what it sends today.
    /// </summary>
    private static async Task<string?> ResolveSnapshotReferenceAsync(
        DetectionEventRequest request, IConfiguration configuration, CancellationToken ct)
    {
        var base64 = request.Evidence?.GetValueOrDefault(SnapshotBase64Key);
        if (string.IsNullOrEmpty(base64))
        {
            return request.Evidence?.GetValueOrDefault(SnapshotPathKey);
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

        await File.WriteAllBytesAsync(fullPath, Convert.FromBase64String(base64), ct);

        return Path.Combine(Path.GetFileName(root), fileName);
    }

    private static async Task<Ok<IReadOnlyList<DetectionResponse>>> SearchAsync(
        string? plateNumber, Guid? targetId, DateTimeOffset? from, DateTimeOffset? to, int? limit,
        DetectionRepository detections, HttpContext http, CancellationToken ct)
    {
        var caller = CallerContextFactory.From(http);
        var end = to ?? DateTimeOffset.UtcNow;
        var start = from ?? end.AddHours(-24);

        var plateNormalized = string.IsNullOrEmpty(plateNumber)
            ? null
            : PlateNormalizer.Normalize(plateNumber);

        var rows = await detections.SearchAsync(
            plateNormalized, targetId, start, end, limit ?? 100, caller, ct);

        return TypedResults.Ok<IReadOnlyList<DetectionResponse>>(
        [
            .. rows.Select(r => new DetectionResponse(
                r.EventId, $"{r.TargetId}:{r.NativeCameraId}", r.CameraId, r.EventType,
                r.OccurredAt, r.Confidence, r.VehicleType, r.PlateNumberRaw, r.SnapshotReference)),
        ]);
    }
}
