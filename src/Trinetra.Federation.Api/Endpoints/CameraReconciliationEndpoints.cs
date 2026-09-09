using System.Text;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using Trinetra.Federation.Api.Auth;
using Trinetra.Federation.Api.Contracts;
using Trinetra.Federation.Api.OpenApi;
using Trinetra.Federation.Core.Model;
using Trinetra.Federation.Storage;
using Trinetra.Federation.Storage.Repositories;

namespace Trinetra.Federation.Api.Endpoints;

/// <summary>
/// Linking registry cameras to the cameras a VMS reports (<c>federated_camera</c>).
/// </summary>
/// <remarks>
/// A VMS-discovered camera stays unlinked until an operator matches it to a registry record.
/// The link is one field — <c>federated_camera.camera_id</c> — and the inventory poll never
/// clears it. Every route here needs <c>camera.reconcile</c>.
/// </remarks>
public static class CameraReconciliationEndpoints
{
    private const int DefaultLimit = 100;
    private const int MaxLimit = 500;

    public static void MapCameraReconciliationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/cameras").WithTags(ApiTags.Cameras).RequireAuthorization();

        group.MapGet("/unreconciled", UnreconciledAsync)
          .RequirePermission("camera.reconcile")
          .WithSummary("The reconciliation backlog")
          .WithDescription(
              "Cameras a VMS reports that the registry has never matched. Filter by `targetId`; "
              + $"`limit` defaults to {DefaultLimit}, clamped to {MaxLimit}. Page with the opaque "
              + "`cursor`.");

        group.MapPost("/{id:guid}/reconcile", ReconcileAsync)
          .RequirePermission("camera.reconcile")
          .WithSummary("Link a registry camera to a VMS-reported camera")
          .WithDescription(
              "Sets `federated_camera.camera_id` for `(targetId, nativeCameraId)`. Optionally "
              + "copies the VMS id and first stream reference onto the registry record when "
              + "those are still blank. Linking the same pair again is a no-op; linking a VMS "
              + "camera that is already tied to a different registry record is 409.");

        group.MapPost("/from-federated", FromFederatedAsync)
          .RequirePermission("camera.reconcile")
          .WithSummary("Register a camera from an unreconciled VMS row and link it")
          .WithDescription(
              "Creates a registry record using the VMS row for what it can supply (owner, area, "
              + "coordinates, name) and the body for what it cannot (`cameraCode`, `cameraType`, "
              + "optics), then links the two — one call. Needs `camera.create` as well. "
              + "`geographicAreaId` must be supplied if the VMS row has no area.");
    }

    private static async Task<Ok<UnreconciledPage>> UnreconciledAsync(
        Guid? targetId, string? cursor, int? limit,
        ReconciliationRepository repo, HttpContext http, CancellationToken ct)
    {
        var caller = CallerContextFactory.From(http);
        caller.Require("camera.reconcile");

        var pageSize = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
        var (cursorTarget, cursorNative) = DecodeCursor(cursor);

        var rows = await repo.UnreconciledAsync(
            targetId, cursorTarget, cursorNative, pageSize + 1, caller, ct);

        string? next = null;
        var items = rows;
        if (rows.Count > pageSize)
        {
            items = [.. rows.Take(pageSize)];
            next = EncodeCursor(items[^1].TargetId, items[^1].NativeCameraId);
        }

        return TypedResults.Ok(new UnreconciledPage(
            [.. items.Select(r => new UnreconciledCameraResponse(
                r.TargetId, r.NativeCameraId, r.Name, r.VendorModel, r.Firmware,
                r.OrganizationUnitId, r.GeographicAreaId, r.Latitude, r.Longitude, r.LastSeen,
                r.StreamReferences ?? []))],
            next));
    }

    private static async Task<Results<Ok<ReconcileResponse>, NotFound, ProblemHttpResult>> ReconcileAsync(
        Guid id, [FromBody] ReconcileRequest request, ReconciliationRepository repo,
        NpgsqlDataSource db, HttpContext http, CancellationToken ct)
    {
        var caller = CallerContextFactory.From(http);

        if (request.TargetId == Guid.Empty || string.IsNullOrWhiteSpace(request.NativeCameraId))
        {
            return TypedResults.Problem(
                title: "Invalid request", detail: "targetId and nativeCameraId are required.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        await using var work = await UnitOfWork.BeginAsync(db, ct);

        var result = await repo.ReconcileAsync(
            id, request.TargetId, request.NativeCameraId.Trim(),
            request.AdoptStreamReference, request.AdoptVmsId, caller, work, ct);

        switch (result.Status)
        {
            case ReconcileStatus.CameraNotFound:
            case ReconcileStatus.FederatedNotFound:
                return TypedResults.NotFound();
            case ReconcileStatus.Conflict:
                return TypedResults.Problem(
                    title: "Already reconciled",
                    detail: "That VMS camera is linked to a different registry record.",
                    statusCode: StatusCodes.Status409Conflict);
        }

        await work.AuditAsync(caller, "reconcile", "camera", id.ToString(),
            before: null,
            after: new { result.TargetId, result.NativeCameraId, result.VmsId },
            result.OrganizationUnitId, ct);
        await work.CommitAsync(ct);

        return TypedResults.Ok(new ReconcileResponse(
            result.CameraId, result.TargetId, result.NativeCameraId, result.VmsId));
    }

    private static async Task<Results<Created<CreatedResponse>, NotFound, ProblemHttpResult>> FromFederatedAsync(
        [FromBody] CreateFromFederatedRequest request,
        ReconciliationRepository reconcile, CameraRepository cameras,
        NpgsqlDataSource db, HttpContext http, CancellationToken ct)
    {
        var caller = CallerContextFactory.From(http);
        caller.Require("camera.create");

        if (request.TargetId == Guid.Empty || string.IsNullOrWhiteSpace(request.NativeCameraId))
        {
            return Bad("targetId and nativeCameraId are required.");
        }

        var native = request.NativeCameraId.Trim();

        var fed = await reconcile.GetUnreconciledAsync(request.TargetId, native, caller, ct);
        if (fed is null)
        {
            return TypedResults.NotFound();
        }

        var geographicAreaId = request.GeographicAreaId ?? fed.GeographicAreaId;
        if (geographicAreaId is null)
        {
            return Bad("geographicAreaId is required: the VMS row has no area.");
        }

        var latitude = request.Latitude ?? fed.Latitude;
        var longitude = request.Longitude ?? fed.Longitude;
        if (latitude is null || longitude is null)
        {
            return Bad("latitude and longitude are required: the VMS row has none.");
        }

        var cameraType = (request.CameraType ?? "").Trim().ToUpperInvariant();
        if (!CameraVocab.Types.Contains(cameraType))
        {
            return Bad($"cameraType must be one of: {string.Join(", ", CameraVocab.Types)}.");
        }

        if (string.IsNullOrWhiteSpace(request.CameraCode))
        {
            return Bad("cameraCode is required.");
        }

        var camera = new Camera
        {
            Id = Guid.Empty,
            Code = request.CameraCode.Trim(),
            Name = string.IsNullOrWhiteSpace(request.Name) ? fed.Name ?? request.CameraCode.Trim() : request.Name.Trim(),
            OrganizationUnitId = request.OrganizationUnitId ?? fed.OrganizationUnitId,
            GeographicAreaId = geographicAreaId.Value,
            CameraType = cameraType,
            Latitude = latitude.Value,
            Longitude = longitude.Value,
            Azimuth = request.Azimuth,
            HorizontalFov = request.HorizontalFov,
            EffectiveRange = request.EffectiveRange,
            Model = fed.VendorModel,
        };

        await using var work = await UnitOfWork.BeginAsync(db, ct);

        Guid newId;
        try
        {
            newId = await cameras.CreateAsync(camera, caller, work, ct);
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return TypedResults.Problem(
                title: "Camera code already in use", statusCode: StatusCodes.Status409Conflict);
        }

        var linked = await reconcile.ReconcileAsync(
            newId, request.TargetId, native,
            request.AdoptStreamReference, request.AdoptVmsId, caller, work, ct);

        if (linked.Status == ReconcileStatus.Conflict)
        {
            return TypedResults.Problem(
                title: "Already reconciled",
                detail: "That VMS camera was linked to another record between read and write.",
                statusCode: StatusCodes.Status409Conflict);
        }

        if (linked.Status is ReconcileStatus.CameraNotFound or ReconcileStatus.FederatedNotFound)
        {
            return TypedResults.NotFound();
        }

        await work.AuditAsync(caller, "create", "camera", newId.ToString(),
            before: null, after: new { camera.Code, fromFederated = new { request.TargetId, native } },
            camera.OrganizationUnitId, ct);
        await work.AuditAsync(caller, "reconcile", "camera", newId.ToString(),
            before: null, after: new { request.TargetId, NativeCameraId = native, linked.VmsId },
            camera.OrganizationUnitId, ct);
        await work.CommitAsync(ct);

        return TypedResults.Created($"/api/v1/cameras/{newId}", new CreatedResponse(newId));
    }

    private static ProblemHttpResult Bad(string detail) => TypedResults.Problem(
        title: "Invalid request", detail: detail, statusCode: StatusCodes.Status400BadRequest);

    private static (Guid?, string?) DecodeCursor(string? cursor)
    {
        if (string.IsNullOrEmpty(cursor))
        {
            return (null, null);
        }

        try
        {
            var raw = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            var sep = raw.IndexOf('|', StringComparison.Ordinal);
            if (sep > 0 && Guid.TryParse(raw[..sep], out var target))
            {
                return (target, raw[(sep + 1)..]);
            }
        }
        catch (FormatException)
        {
            // Fall through to "no cursor" — the query restarts from the beginning.
        }

        return (null, null);
    }

    private static string EncodeCursor(Guid target, string native) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{target}|{native}"));
}
