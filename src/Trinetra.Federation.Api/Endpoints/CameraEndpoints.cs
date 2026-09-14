using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using Trinetra.Federation.Api.Auth;
using Trinetra.Federation.Api.Contracts;
using Trinetra.Federation.Api.OpenApi;
using Trinetra.Federation.Core.Geo;
using Trinetra.Federation.Core.Model;
using Trinetra.Federation.Storage;
using Trinetra.Federation.Storage.Repositories;

namespace Trinetra.Federation.Api.Endpoints;

/// <summary>
/// The authoritative camera registry: create, read, list, edit and retire CCTV assets.
/// </summary>
/// <remarks>
/// <para>
/// <b>These cameras are registered deliberately</b> — one at a time here, in bulk via
/// <c>POST /cameras/bulk-import</c>, or from the API. That is the opposite of
/// <c>GET /vms/{id}/cameras</c>, which is the read-only view of what a VMS currently reports
/// and never a registration path.
/// </para>
/// <para>
/// Every route is scoped on both dimensions — the camera's owning organization unit and its
/// its geographic area, ANDed. A camera the caller cannot reach is <b>404, not 403</b>.
/// </para>
/// </remarks>
public static class CameraEndpoints
{
    private const int DefaultPageSize = 50;
    private const int MaxPageSize = 200;
    private const int MaxBulkRows = 500;

    public static void MapCameraEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/cameras").WithTags(ApiTags.Cameras).RequireAuthorization();

        group.MapPost("/", CreateAsync)
          .RequirePermission("camera.create")
          .WithSummary("Register a camera")
          .WithDescription(
              "Manual onboarding: creates one authoritative registry record. `cameraCode` must "
              + "be unique among live cameras; `organizationUnitId` and `geographicAreaId` must both be "
              + "within the caller's scope, or the request is refused. Coordinates are required "
              + "and validated. Optics (`azimuth`, `horizontalFov`, `effectiveRange`) are "
              + "optional but all three are needed before a coverage sector can be drawn.");

        group.MapGet("/", ListAsync)
          .RequirePermission("camera.read")
          .WithSummary("List the registry, paged and filtered")
          .WithDescription(
              "Every camera within the caller's organization-and-geography scope, ordered by "
              + "`cameraCode`. Filter with `organizationUnitId` (includes descendants), "
              + "`geographicAreaId` (includes descendants), `cameraType`, the three status axes, "
              + "`bbox` (`minLon,minLat,maxLon,maxLat`) and `q` (prefix match on code and name). "
              + "Retired cameras are hidden unless `includeRetired=true`.\n\n"
              + "Paging is by the opaque `cursor` from the previous page, never by offset. A "
              + "response whose `nextCursor` is null is the last page. `limit` defaults to 50, "
              + "clamped to 200.");

        group.MapGet("/reports/ageing-infrastructure", AgeingInfrastructureAsync)
          .RequirePermission("camera.read")
          .WithSummary("Ageing-infrastructure report: cameras bucketed by installation age")
          .WithDescription(
              "Every in-scope, live camera bucketed by years since `installationDate` — "
              + "`under_3`, `3_to_5`, `5_to_10`, `10_plus`, and `unknown` for a camera with no "
              + "recorded date — plus the oldest cameras with a known date (`oldestLimit`, "
              + "default 25, capped at 200), for prioritising replacement. Filter with "
              + "`organizationUnitId`/`geographicAreaId` (both include descendants), same as "
              + "`GET /cameras`.");

        group.MapPost("/bulk-import", BulkImportAsync)
          .RequirePermission("camera.import")
          .WithSummary("Register or update many cameras in one call")
          .WithDescription(
              $"Synchronous, 1..{MaxBulkRows} rows. `mode` is `insert` (a row whose code exists "
              + "fails, others proceed) or `upsert` (an existing code is replaced). Each row is "
              + "isolated (via a per-row savepoint) and gets its own audit entry, so one bad row "
              + "never rolls back the good ones. All rows commit together when the request "
              + "finishes, not independently as it proceeds — a connection lost mid-batch takes "
              + "the whole batch with it, including rows already reflected in an earlier partial "
              + "read of this response. Only a completed `200` response means the batch landed: "
              + "on any error, timeout, or disconnect before then, assume nothing committed and "
              + "retry the whole batch — `upsert` mode is safe to retry as-is; a retried `insert` "
              + "will report already-materialized rows as `duplicate camera_code`, which is safe "
              + "to ignore. Always 200: the body reports every row's outcome.");

        group.MapGet("/{id:guid}", GetAsync)
          .RequirePermission("camera.read")
          .WithSummary("Read one camera")
          .WithDescription("The full registry record. Out of scope or absent → 404.");

        group.MapPut("/{id:guid}", ReplaceAsync)
          .RequirePermission("camera.update")
          .WithSummary("Replace a camera")
          .WithDescription(
              "A full replacement: every field is taken from the body and an omitted optional "
              + "field reverts to null/default. Read the camera first and send it back modified. "
              + "Moving the camera to a different unit or area requires the caller to be scoped "
              + "to both the old and the new placement. `maintenanceStatus` cannot be set to or "
              + "away from `RETIRED` here — use DELETE.");

        group.MapPatch("/{id:guid}", PatchAsync)
          .RequirePermission("camera.update")
          .WithSummary("Update selected fields of a camera")
          .WithDescription(
              "Only the properties present in the body change. A property sent as `null` clears "
              + "that column; a property left out is untouched — the two are distinct. Useful "
              + "for a GIS pin drag (`{ \"latitude\": …, \"longitude\": … }`) or a bulk azimuth "
              + "correction. `id`, `cameraCode` history, timestamps and the retirement fields "
              + "are not patchable; `maintenanceStatus` cannot be set to/from `RETIRED`.");

        group.MapDelete("/{id:guid}", RetireAsync)
          .RequirePermission("camera.delete")
          .WithSummary("Retire a camera")
          .WithDescription(
              "A soft delete: `maintenanceStatus` becomes `RETIRED` and the row is stamped "
              + "`deletedAt`. The record stays so historical observations and reconciliation "
              + "links remain valid, and its `cameraCode` becomes free for reuse. Retiring an "
              + "already-retired camera is a no-op success.");
    }

    // -------------------------------------------------------------------
    // Handlers
    // -------------------------------------------------------------------

    private static async Task<Results<Created<CreatedResponse>, ProblemHttpResult>> CreateAsync(
        [FromBody] CameraWriteRequest request, CameraRepository repo,
        NpgsqlDataSource db, HttpContext http, CancellationToken ct)
    {
        var caller = CallerContextFactory.From(http);

        if (!TryBuild(request, Guid.Empty, out var camera, out var problem))
        {
            return problem!;
        }

        await using var work = await UnitOfWork.BeginAsync(db, ct);

        Guid id;
        try
        {
            id = await repo.CreateAsync(camera!, caller, work, ct);
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return TypedResults.Problem(
                title: "Camera code already in use",
                detail: $"A live camera with code '{request.CameraCode}' already exists.",
                statusCode: StatusCodes.Status409Conflict);
        }

        await work.AuditAsync(caller, "create", "camera", id.ToString(),
            before: null, after: Redact(camera!), request.OrganizationUnitId, ct);
        await work.CommitAsync(ct);

        return TypedResults.Created($"/api/v1/cameras/{id}", new CreatedResponse(id));
    }

    private static async Task<Results<Ok<CameraPage>, ProblemHttpResult>> ListAsync(
        int? limit, string? cursor, bool? includeRetired,
        Guid? organizationUnitId, Guid? geographicAreaId,
        string? cameraType, string? operationalStatus, string? connectivityStatus,
        string? maintenanceStatus, string? q, string? bbox,
        CameraRepository repo, HttpContext http, CancellationToken ct)
    {
        var caller = CallerContextFactory.From(http);

        BoundingBox? box = null;
        if (!string.IsNullOrWhiteSpace(bbox))
        {
            if (!TryParseBbox(bbox, out var parsed, out var bboxProblem))
            {
                return bboxProblem!;
            }

            box = parsed;
        }

        var pageSize = Math.Clamp(limit ?? DefaultPageSize, 1, MaxPageSize);

        if (!TryDecodeCursor(cursor, out var decodedCode, out var decodedId))
        {
            return TypedResults.Problem(
                title: "Invalid cursor",
                detail: "The cursor is malformed. Restart the listing without one.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var rows = await repo.ListAsync(
            new CameraQuery(
                pageSize + 1, decodedCode, decodedId, includeRetired ?? false,
                organizationUnitId, geographicAreaId, cameraType,
                Upper(operationalStatus), Upper(connectivityStatus), Upper(maintenanceStatus),
                q, box),
            caller, ct);

        string? next = null;
        var items = rows;
        if (rows.Count > pageSize)
        {
            items = [.. rows.Take(pageSize)];
            next = EncodeCursor(items[^1].Code, items[^1].Id);
        }

        return TypedResults.Ok(new CameraPage([.. items.Select(ToResponse)], next));
    }

    private const int DefaultOldestLimit = 25;
    private const int MaxOldestLimit = 200;

    private static readonly (string Bucket, string Label)[] AgeingBucketOrder =
    [
        ("under_3", "Under 3 years"),
        ("3_to_5", "3–5 years"),
        ("5_to_10", "5–10 years"),
        ("10_plus", "10+ years"),
        ("unknown", "Unknown installation date"),
    ];

    private static async Task<Ok<AgeingInfrastructureResponse>> AgeingInfrastructureAsync(
        Guid? organizationUnitId, Guid? geographicAreaId, int? oldestLimit,
        CameraRepository repo, HttpContext http, CancellationToken ct)
    {
        var caller = CallerContextFactory.From(http);

        var row = await repo.GetAgeingInfrastructureAsync(
            caller, organizationUnitId, geographicAreaId,
            Math.Clamp(oldestLimit ?? DefaultOldestLimit, 1, MaxOldestLimit), ct);

        var counts = new Dictionary<string, int>
        {
            ["under_3"] = row.UnderThreeYears,
            ["3_to_5"] = row.ThreeToFiveYears,
            ["5_to_10"] = row.FiveToTenYears,
            ["10_plus"] = row.TenPlusYears,
            ["unknown"] = row.UnknownInstallationDate,
        };

        var buckets = AgeingBucketOrder
            .Select(b => new AgeingInfrastructureBucketResponse(b.Bucket, b.Label, counts[b.Bucket]))
            .ToList();
        var total = counts.Values.Sum();

        return TypedResults.Ok(new AgeingInfrastructureResponse(
            total, buckets,
            [.. row.OldestCameras.Select(c => new AgeingCameraSummaryResponse(
                c.Id, c.CameraCode, c.Name, DateOnly.FromDateTime(c.InstallationDate),
                c.AgeYears, c.MaintenanceStatus))]));
    }

    internal static async Task<Results<Ok<BulkImportResult>, ProblemHttpResult>> BulkImportAsync(
        [FromBody] BulkImportRequest request, CameraRepository repo,
        NpgsqlDataSource db, HttpContext http, CancellationToken ct)
    {
        var caller = CallerContextFactory.From(http);

        var upsert = string.Equals(request.Mode, "upsert", StringComparison.OrdinalIgnoreCase);
        if (!upsert && !string.Equals(request.Mode, "insert", StringComparison.OrdinalIgnoreCase))
        {
            return TypedResults.Problem(
                title: "Invalid mode", detail: "mode must be 'insert' or 'upsert'.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (request.Items is null || request.Items.Count == 0 || request.Items.Count > MaxBulkRows)
        {
            return TypedResults.Problem(
                title: "Invalid batch size",
                detail: $"items must contain between 1 and {MaxBulkRows} rows.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var results = new List<BulkRowResult>(request.Items.Count);
        int created = 0, updated = 0, failed = 0;

        // Finding 10-M4: one connection and one transaction for the whole batch, not up to 500 —
        // each row's write used to open and commit its own full transaction (a connection-pool
        // round trip every time), which is most of this route's cost at 500 rows. A per-row
        // SAVEPOINT keeps the same isolation promise (one bad row is discarded without touching
        // the others) at a fraction of it: a savepoint create/release/rollback is one lightweight
        // statement on an already-open connection, not a new connection and transaction.
        //
        // One real trade-off, not present before: rows now commit together at the very end, not
        // independently. If the connection is lost partway through, every row processed so far —
        // including ones already reported "created"/"updated" in an earlier response — is rolled
        // back with it, whereas a genuinely separate transaction per row would have kept whatever
        // had already committed. Ordinary per-row failures (validation, scope, duplicate code)
        // are unaffected by this — those still isolate exactly as before via the savepoint.
        await using var work = await UnitOfWork.BeginAsync(db, ct);

        for (var i = 0; i < request.Items.Count; i++)
        {
            var item = request.Items[i];
            var code = item.CameraCode ?? "";

            if (!TryBuild(item, Guid.Empty, out var camera, out var problem))
            {
                failed++;
                results.Add(new BulkRowResult(i, code, "error", null,
                    (problem!.ProblemDetails.Detail) ?? "validation failed"));
                continue;
            }

            var savepoint = $"bulk_row_{i}";
            await work.Transaction.SaveAsync(savepoint, ct);

            try
            {
                var existingId = upsert
                    ? await repo.FindLiveIdByCodeAsync(code, caller, work, ct)
                    : null;

                if (existingId is { } eid)
                {
                    // Finding 10-M5: captured before the write, same as the single-row
                    // ReplaceAsync — an audit row that always says "before: null" for an update
                    // is a fidelity gap, not just here but for every future reader trying to
                    // reconstruct what changed. Read on its own connection, outside `work`'s
                    // transaction — matching the single-row path exactly (its own `before` fetch
                    // runs before that endpoint even opens a UnitOfWork). A concurrent writer to
                    // this same row could in principle land in the gap and make `before_state`
                    // reflect an intermediate value rather than the immediate predecessor; that
                    // window already exists on the single-row path today and isn't widened here.
                    var before = await repo.GetAsync(eid, caller, ct);

                    if (!await repo.ReplaceAsync(eid, camera!, caller, work, ct))
                    {
                        await work.Transaction.RollbackAsync(savepoint, ct);
                        failed++;
                        results.Add(new BulkRowResult(i, code, "error", null, "not in scope"));
                        continue;
                    }

                    await work.AuditAsync(caller, "update", "camera", eid.ToString(),
                        before: before is null ? null : Redact(before), after: Redact(camera!),
                        camera!.OrganizationUnitId, ct);
                    await work.Transaction.ReleaseAsync(savepoint, ct);

                    updated++;
                    results.Add(new BulkRowResult(i, code, "updated", eid, null));
                }
                else
                {
                    var newId = await repo.CreateAsync(camera!, caller, work, ct);
                    await work.AuditAsync(caller, "create", "camera", newId.ToString(),
                        before: null, after: Redact(camera!), camera!.OrganizationUnitId, ct);
                    await work.Transaction.ReleaseAsync(savepoint, ct);

                    created++;
                    results.Add(new BulkRowResult(i, code, "created", newId, null));
                }
            }
            catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                // A failed statement leaves the transaction "aborted" until rolled back — to the
                // savepoint, not the whole batch, so row i+1 can still run.
                await work.Transaction.RollbackAsync(savepoint, ct);
                failed++;
                results.Add(new BulkRowResult(i, code, "error", null, "duplicate camera_code"));
            }
            catch (ForbiddenException)
            {
                await work.Transaction.RollbackAsync(savepoint, ct);
                failed++;
                results.Add(new BulkRowResult(i, code, "error", null,
                    "organization_unit or geographic_area not in scope"));
            }
        }

        await work.CommitAsync(ct);

        return TypedResults.Ok(new BulkImportResult(created, updated, failed, results));
    }

    private static async Task<Results<Ok<CameraResponse>, NotFound>> GetAsync(
        Guid id, CameraRepository repo, HttpContext http, CancellationToken ct)
    {
        var camera = await repo.GetAsync(id, CallerContextFactory.From(http), ct);
        return camera is null ? TypedResults.NotFound() : TypedResults.Ok(ToResponse(camera));
    }

    private static async Task<Results<Ok<CameraResponse>, NotFound, ProblemHttpResult>> ReplaceAsync(
        Guid id, [FromBody] CameraWriteRequest request, CameraRepository repo,
        NpgsqlDataSource db, HttpContext http, CancellationToken ct)
    {
        var caller = CallerContextFactory.From(http);

        var before = await repo.GetAsync(id, caller, ct);
        if (before is null)
        {
            return TypedResults.NotFound();
        }

        if (!TryBuild(request, id, out var camera, out var problem))
        {
            return problem!;
        }

        if (camera!.MaintenanceStatus == CameraStatus.MaintenanceRetired
            || before.MaintenanceStatus == CameraStatus.MaintenanceRetired)
        {
            return TypedResults.Problem(
                title: "Retirement is not editable here",
                detail: "Retire a camera with DELETE; a retired camera cannot be replaced.",
                statusCode: StatusCodes.Status409Conflict);
        }

        await using var work = await UnitOfWork.BeginAsync(db, ct);

        bool updated;
        try
        {
            updated = await repo.ReplaceAsync(id, camera, caller, work, ct);
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return TypedResults.Problem(
                title: "Camera code already in use", statusCode: StatusCodes.Status409Conflict);
        }

        if (!updated)
        {
            return TypedResults.NotFound();
        }

        await work.AuditAsync(caller, "update", "camera", id.ToString(),
            Redact(before), Redact(camera), camera.OrganizationUnitId, ct);
        await work.CommitAsync(ct);

        var after = await repo.GetAsync(id, caller, ct);
        return after is null ? TypedResults.NotFound() : TypedResults.Ok(ToResponse(after));
    }

    private static async Task<Results<Ok<CameraResponse>, NotFound, ProblemHttpResult>> PatchAsync(
        Guid id, [FromBody] JsonElement body, CameraRepository repo,
        NpgsqlDataSource db, HttpContext http, CancellationToken ct)
    {
        var caller = CallerContextFactory.From(http);

        var before = await repo.GetAsync(id, caller, ct);
        if (before is null)
        {
            return TypedResults.NotFound();
        }

        if (!TryBuildPatch(body, out var patch, out var problem))
        {
            return problem!;
        }

        await using var work = await UnitOfWork.BeginAsync(db, ct);

        bool updated;
        try
        {
            updated = await repo.PatchAsync(id, patch!, caller, work, ct);
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return TypedResults.Problem(
                title: "Camera code already in use", statusCode: StatusCodes.Status409Conflict);
        }

        if (!updated)
        {
            return TypedResults.NotFound();
        }

        // Same connection/transaction as the write above, not a fresh one: a read on a separate
        // connection here would run before this transaction commits and, under read-committed
        // isolation, would not see the write it is meant to report (the bug this replaces — every
        // PATCH response silently echoed the pre-patch row).
        var after = await repo.GetAsync(id, caller, work, ct);
        await work.AuditAsync(caller, "update", "camera", id.ToString(),
            Redact(before), after is null ? null : Redact(after),
            (after ?? before).OrganizationUnitId, ct);
        await work.CommitAsync(ct);

        return after is null ? TypedResults.NotFound() : TypedResults.Ok(ToResponse(after));
    }

    private static async Task<Results<NoContent, NotFound>> RetireAsync(
        Guid id, string? reason, CameraRepository repo,
        NpgsqlDataSource db, HttpContext http, CancellationToken ct)
    {
        var caller = CallerContextFactory.From(http);

        var before = await repo.GetAsync(id, caller, ct);
        if (before is null)
        {
            return TypedResults.NotFound();
        }

        await using var work = await UnitOfWork.BeginAsync(db, ct);

        if (!await repo.RetireAsync(id, caller, work, ct))
        {
            return TypedResults.NotFound();
        }

        await work.AuditAsync(caller, "delete", "camera", id.ToString(),
            Redact(before),
            new { maintenanceStatus = CameraStatus.MaintenanceRetired, reason },
            before.OrganizationUnitId, ct);
        await work.CommitAsync(ct);

        return TypedResults.NoContent();
    }

    // -------------------------------------------------------------------
    // Building and validation
    // -------------------------------------------------------------------

    private static bool TryBuild(
        CameraWriteRequest r, Guid id, out Camera? camera, out ProblemHttpResult? problem)
    {
        camera = null;
        problem = null;

        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(r.CameraCode) || r.CameraCode.Length > 100)
        {
            errors.Add("cameraCode is required and at most 100 characters.");
        }

        if (string.IsNullOrWhiteSpace(r.Name) || r.Name.Length > 255)
        {
            errors.Add("name is required and at most 255 characters.");
        }

        var cameraType = Upper(r.CameraType);
        if (cameraType is null || !CameraVocab.Types.Contains(cameraType))
        {
            errors.Add($"cameraType must be one of: {string.Join(", ", CameraVocab.Types)}.");
        }

        if (r.OrganizationUnitId == Guid.Empty)
        {
            errors.Add("organizationUnitId is required.");
        }

        if (r.GeographicAreaId == Guid.Empty)
        {
            errors.Add("geographicAreaId is required.");
        }

        if (r.Latitude is < -90 or > 90)
        {
            errors.Add("latitude must be between -90 and 90.");
        }

        if (r.Longitude is < -180 or > 180)
        {
            errors.Add("longitude must be between -180 and 180.");
        }

        Check(errors, "azimuth", r.Azimuth, 0, 359.999);
        Check(errors, "tilt", r.Tilt, -90, 90);
        Check(errors, "horizontalFov", r.HorizontalFov, 0.001, 360);
        Check(errors, "verticalFov", r.VerticalFov, 0.001, 180);
        Check(errors, "effectiveRange", r.EffectiveRange, 0.001, 5000);
        Check(errors, "altitude", r.Altitude, -500, 9000);
        Check(errors, "mountingHeight", r.MountingHeight, 0, 200);

        if (r.Port is < 1 or > 65535)
        {
            errors.Add("port must be between 1 and 65535.");
        }

        var protocol = Upper(r.Protocol);
        if (protocol is not null && !CameraVocab.Protocols.Contains(protocol))
        {
            errors.Add($"protocol must be one of: {string.Join(", ", CameraVocab.Protocols)}.");
        }

        var streamPreference = Upper(r.StreamPreference) ?? CameraVocab.StreamPreferenceRtsp;
        if (!CameraVocab.StreamPreferences.Contains(streamPreference))
        {
            errors.Add($"streamPreference must be one of: {string.Join(", ", CameraVocab.StreamPreferences)}.");
        }

        var op = Upper(r.OperationalStatus) ?? CameraStatus.OperationalUnknown;
        var conn = Upper(r.ConnectivityStatus) ?? CameraStatus.ConnectivityUnknown;
        var maint = Upper(r.MaintenanceStatus) ?? CameraStatus.MaintenanceNormal;

        if (!CameraStatus.Operational.Contains(op))
        {
            errors.Add($"operationalStatus must be one of: {string.Join(", ", CameraStatus.Operational)}.");
        }

        if (!CameraStatus.Connectivity.Contains(conn))
        {
            errors.Add($"connectivityStatus must be one of: {string.Join(", ", CameraStatus.Connectivity)}.");
        }

        if (maint == CameraStatus.MaintenanceRetired)
        {
            errors.Add("maintenanceStatus cannot be set to RETIRED; use DELETE.");
        }
        else if (!CameraStatus.Maintenance.Contains(maint))
        {
            errors.Add($"maintenanceStatus must be one of: NORMAL, REQUIRED, UNDER_MAINTENANCE.");
        }

        if (errors.Count > 0)
        {
            problem = TypedResults.Problem(
                title: "Invalid camera", detail: string.Join(" ", errors),
                statusCode: StatusCodes.Status400BadRequest);
            return false;
        }

        camera = new Camera
        {
            Id = id,
            Code = r.CameraCode.Trim(),
            Name = r.Name.Trim(),
            OrganizationUnitId = r.OrganizationUnitId,
            GeographicAreaId = r.GeographicAreaId,
            Manufacturer = Trim(r.Manufacturer),
            Model = Trim(r.Model),
            CameraType = cameraType!,
            SerialNumber = Trim(r.SerialNumber),
            Latitude = r.Latitude,
            Longitude = r.Longitude,
            Altitude = r.Altitude,
            MountingHeight = r.MountingHeight,
            Azimuth = r.Azimuth,
            Tilt = r.Tilt,
            HorizontalFov = r.HorizontalFov,
            VerticalFov = r.VerticalFov,
            EffectiveRange = r.EffectiveRange,
            IpAddress = Trim(r.IpAddress),
            Port = r.Port,
            Protocol = protocol,
            VmsId = r.VmsId,
            StreamReference = Trim(r.StreamReference),
            StreamPreference = streamPreference,
            NativeHlsUrl = Trim(r.NativeHlsUrl),
            NativeWebrtcUrl = Trim(r.NativeWebrtcUrl),
            CredentialReference = Trim(r.CredentialReference),
            InstallationDate = r.InstallationDate,
            RecordEvents = r.RecordEvents,
            OperationalStatus = op,
            ConnectivityStatus = conn,
            MaintenanceStatus = maint,
        };
        return true;
    }

    private static bool TryBuildPatch(
        JsonElement body, out CameraPatch? patch, out ProblemHttpResult? problem)
    {
        patch = null;
        problem = null;

        if (body.ValueKind != JsonValueKind.Object)
        {
            problem = Bad("The patch body must be a JSON object.");
            return false;
        }

        var built = new CameraPatch();
        var errors = new List<string>();

        foreach (var prop in body.EnumerateObject())
        {
            switch (prop.Name)
            {
                case "name":
                    built.Set("name", "p_name", RequireString(prop.Value, "name", errors, 255));
                    break;
                case "manufacturer":
                    built.Set("manufacturer", "p_manufacturer", NullableString(prop.Value, "manufacturer", errors, 255));
                    break;
                case "model":
                    built.Set("model", "p_model", NullableString(prop.Value, "model", errors, 255));
                    break;
                case "serialNumber":
                    built.Set("serial_number", "p_serial", NullableString(prop.Value, "serialNumber", errors, 255));
                    break;
                case "cameraType":
                    var t = Upper(prop.Value.GetString());
                    if (t is null || !CameraVocab.Types.Contains(t))
                    {
                        errors.Add("cameraType is invalid.");
                    }

                    built.Set("camera_type", "p_type", t);
                    break;
                case "organizationUnitId":
                    built.Set("organization_unit_id", "p_org", RequireGuid(prop.Value, "organizationUnitId", errors));
                    break;
                case "geographicAreaId":
                    built.Set("geographic_area_id", "p_area", RequireGuid(prop.Value, "geographicAreaId", errors));
                    break;
                case "latitude":
                    built.Set("latitude", "p_lat", RequireDouble(prop.Value, "latitude", -90, 90, errors));
                    break;
                case "longitude":
                    built.Set("longitude", "p_lon", RequireDouble(prop.Value, "longitude", -180, 180, errors));
                    break;
                case "altitude":
                    built.Set("altitude", "p_alt", NullableDouble(prop.Value, "altitude", -500, 9000, errors));
                    break;
                case "mountingHeight":
                    built.Set("mounting_height", "p_mh", NullableDouble(prop.Value, "mountingHeight", 0, 200, errors));
                    break;
                case "azimuth":
                    built.Set("azimuth", "p_az", NullableDouble(prop.Value, "azimuth", 0, 359.999, errors));
                    break;
                case "tilt":
                    built.Set("tilt", "p_tilt", NullableDouble(prop.Value, "tilt", -90, 90, errors));
                    break;
                case "horizontalFov":
                    built.Set("horizontal_fov", "p_hfov", NullableDouble(prop.Value, "horizontalFov", 0.001, 360, errors));
                    break;
                case "verticalFov":
                    built.Set("vertical_fov", "p_vfov", NullableDouble(prop.Value, "verticalFov", 0.001, 180, errors));
                    break;
                case "effectiveRange":
                    built.Set("effective_range", "p_range", NullableDouble(prop.Value, "effectiveRange", 0.001, 5000, errors));
                    break;
                case "ipAddress":
                    built.Set("ip_address", "p_ip", NullableString(prop.Value, "ipAddress", errors, 45), cast: "::inet");
                    break;
                case "port":
                    built.Set("port", "p_port", NullableInt(prop.Value, "port", 1, 65535, errors));
                    break;
                case "protocol":
                    built.Set("protocol", "p_proto", NullableString(prop.Value, "protocol", errors, 50));
                    break;
                case "vmsId":
                    built.Set("vms_id", "p_vms", NullableGuid(prop.Value, "vmsId", errors));
                    break;
                case "streamReference":
                    built.Set("stream_reference", "p_stream", NullableString(prop.Value, "streamReference", errors, 512));
                    break;
                case "streamPreference":
                    var pref = Upper(prop.Value.GetString());
                    if (pref is null || !CameraVocab.StreamPreferences.Contains(pref))
                    {
                        errors.Add($"streamPreference must be one of: {string.Join(", ", CameraVocab.StreamPreferences)}.");
                    }

                    built.Set("stream_preference", "p_stream_pref", pref);
                    break;
                case "nativeHlsUrl":
                    built.Set("native_hls_url", "p_native_hls", NullableString(prop.Value, "nativeHlsUrl", errors, 2048));
                    break;
                case "nativeWebrtcUrl":
                    built.Set("native_webrtc_url", "p_native_webrtc", NullableString(prop.Value, "nativeWebrtcUrl", errors, 2048));
                    break;
                case "credentialReference":
                    built.Set("credential_reference", "p_cred", NullableString(prop.Value, "credentialReference", errors, 255));
                    break;
                case "installationDate":
                    // installation_date is a calendar DATE (Camera.InstallationDate is DateOnly).
                    // Parse "yyyy-MM-dd" directly — same as the create path (STJ binds its DTO
                    // DateOnly the same way) — rather than going via DateTime, whose result would
                    // depend on the server timezone for a zoned value (finding 10-L3, CLAUDE.md #6).
                    built.Set("installation_date", "p_install",
                        prop.Value.ValueKind == JsonValueKind.Null ? null
                            : prop.Value.ValueKind == JsonValueKind.String
                              && DateOnly.TryParseExact(
                                     prop.Value.GetString(), "yyyy-MM-dd",
                                     CultureInfo.InvariantCulture, DateTimeStyles.None, out var instDate)
                                ? instDate
                                : Fail(errors, "installationDate"));
                    break;
                case "recordEvents":
                    built.Set("record_events", "p_record_events",
                        RequireBool(prop.Value, "recordEvents", errors));
                    break;
                case "operationalStatus":
                    built.Set("operational_status", "p_op",
                        Enumerated(prop.Value, CameraStatus.Operational, "operationalStatus", errors));
                    break;
                case "connectivityStatus":
                    built.Set("connectivity_status", "p_conn",
                        Enumerated(prop.Value, CameraStatus.Connectivity, "connectivityStatus", errors));
                    break;
                case "maintenanceStatus":
                    var m = Upper(prop.Value.GetString());
                    if (m == CameraStatus.MaintenanceRetired || m is null
                        || !CameraStatus.Maintenance.Contains(m))
                    {
                        errors.Add("maintenanceStatus must be NORMAL, REQUIRED or UNDER_MAINTENANCE.");
                    }

                    built.Set("maintenance_status", "p_maint", m);
                    break;
                default:
                    // Unknown fields are ignored rather than rejected: a patch body built from a
                    // wider write-request shape (e.g. the register-camera form re-sending fields
                    // the camera doesn't accept a change to) should still apply the fields it does
                    // recognize instead of failing the whole request over one it doesn't.
                    break;
            }
        }

        if (errors.Count > 0)
        {
            problem = Bad(string.Join(" ", errors));
            return false;
        }

        patch = built;
        return true;
    }

    // -------------------------------------------------------------------
    // Small helpers
    // -------------------------------------------------------------------

    private static void Check(List<string> errors, string name, double? value, double min, double max)
    {
        if (value is { } v && (v < min || v > max))
        {
            errors.Add($"{name} must be between {min} and {max}.");
        }
    }

    private static string? Upper(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim().ToUpperInvariant();

    private static string? Trim(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static ProblemHttpResult Bad(string detail) => TypedResults.Problem(
        title: "Invalid patch", detail: detail, statusCode: StatusCodes.Status400BadRequest);

    private static object? Fail(List<string> errors, string field)
    {
        errors.Add($"{field} is not a valid value.");
        return null;
    }

    private static string? RequireString(JsonElement e, string field, List<string> errors, int max)
    {
        if (e.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(e.GetString()))
        {
            errors.Add($"{field} must be a non-empty string.");
            return null;
        }

        var s = e.GetString()!.Trim();
        if (s.Length > max)
        {
            errors.Add($"{field} is at most {max} characters.");
        }

        return s;
    }

    private static string? NullableString(JsonElement e, string field, List<string> errors, int max)
    {
        if (e.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var s = Trim(e.GetString());
        if (s is not null && s.Length > max)
        {
            errors.Add($"{field} is at most {max} characters.");
        }

        return s;
    }

    private static bool? RequireBool(JsonElement e, string field, List<string> errors)
    {
        if (e.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return e.GetBoolean();
        }

        errors.Add($"{field} must be a boolean.");
        return null;
    }

    private static Guid? RequireGuid(JsonElement e, string field, List<string> errors)
    {
        if (e.ValueKind == JsonValueKind.String && Guid.TryParse(e.GetString(), out var g) && g != Guid.Empty)
        {
            return g;
        }

        errors.Add($"{field} must be a non-empty GUID.");
        return null;
    }

    private static Guid? NullableGuid(JsonElement e, string field, List<string> errors)
    {
        if (e.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (e.ValueKind == JsonValueKind.String && Guid.TryParse(e.GetString(), out var g))
        {
            return g;
        }

        errors.Add($"{field} must be a GUID or null.");
        return null;
    }

    private static double? RequireDouble(JsonElement e, string field, double min, double max, List<string> errors)
    {
        if (e.ValueKind == JsonValueKind.Number && e.TryGetDouble(out var d) && d >= min && d <= max)
        {
            return d;
        }

        errors.Add($"{field} must be a number between {min} and {max}.");
        return null;
    }

    private static double? NullableDouble(JsonElement e, string field, double min, double max, List<string> errors)
    {
        if (e.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return RequireDouble(e, field, min, max, errors);
    }

    private static int? NullableInt(JsonElement e, string field, int min, int max, List<string> errors)
    {
        if (e.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var i) && i >= min && i <= max)
        {
            return i;
        }

        errors.Add($"{field} must be an integer between {min} and {max}.");
        return null;
    }

    private static string? Enumerated(
        JsonElement e, IReadOnlySet<string> allowed, string field, List<string> errors)
    {
        var v = Upper(e.GetString());
        if (v is null || !allowed.Contains(v))
        {
            errors.Add($"{field} must be one of: {string.Join(", ", allowed)}.");
        }

        return v;
    }

    private static bool TryParseBbox(string raw, out BoundingBox box, out ProblemHttpResult? problem)
    {
        box = default;
        problem = null;

        var parts = raw.Split(',');
        if (parts.Length != 4
            || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var minLon)
            || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var minLat)
            || !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var maxLon)
            || !double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var maxLat))
        {
            problem = Bad("bbox must be four comma-separated numbers: minLon,minLat,maxLon,maxLat.");
            return false;
        }

        if (minLon >= maxLon || minLat >= maxLat
            || minLon < -180 || maxLon > 180 || minLat < -90 || maxLat > 90)
        {
            problem = Bad("bbox is out of range or has min >= max.");
            return false;
        }

        box = new BoundingBox(minLon, minLat, maxLon, maxLat);
        return true;
    }

    // Finding 10-L6: a malformed cursor used to fail open (silently restart from page 1),
    // which masks a client bug — a truncated/corrupted cursor now reports as a 400 instead,
    // matching EventEndpoints.TryDecodeCursor.
    // Finding 10-M6: keyed on (code, id) together, not code alone — a retired camera's code is
    // freed for reuse, so two rows can share one once `includeRetired=true` is set; code-only
    // paging could then skip or loop on the tied rows. The pair together is always unique and
    // total, matching the repository's ORDER BY.
    private static bool TryDecodeCursor(string? cursor, out string? code, out Guid? id)
    {
        code = null;
        id = null;

        if (string.IsNullOrEmpty(cursor))
        {
            return true;
        }

        try
        {
            // The id comes first specifically because it's fixed-format and never contains '|' —
            // camera_code has no charset restriction, so splitting on the FIRST '|' and taking
            // everything after it as the code is safe even if a code itself contains one.
            // Ordering it the other way round would not be.
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(cursor)).Split('|', 2);

            if (parts.Length != 2 || !Guid.TryParse(parts[0], out var parsedId))
            {
                return false;
            }

            id = parsedId;
            code = parts[1];
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string EncodeCursor(string code, Guid id) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{id}|{code}"));

    private static CameraResponse ToResponse(Camera c) => new(
        c.Id, c.Code, c.Name, c.OrganizationUnitId, c.GeographicAreaId, c.CameraType,
        c.Latitude, c.Longitude, c.Manufacturer, c.Model, c.SerialNumber,
        c.Altitude, c.MountingHeight, c.Azimuth, c.Tilt, c.HorizontalFov,
        c.VerticalFov, c.EffectiveRange, c.IpAddress, c.Port, c.Protocol,
        c.VmsId, c.StreamReference, c.StreamPreference, c.NativeHlsUrl, c.NativeWebrtcUrl,
        c.CredentialReference, c.InstallationDate,
        c.RecordEvents,
        c.OperationalStatus, c.ConnectivityStatus, c.MaintenanceStatus,
        CoverageSector.CanCompute(c.Azimuth, c.HorizontalFov, c.EffectiveRange),
        c.LastSeenAt, c.LastHealthCheckAt, c.DeletedAt);

    /// <summary>Audit projection. Records the credential <i>reference</i>, never a secret.</summary>
    private static object Redact(Camera c) => new
    {
        c.Code, c.Name, c.OrganizationUnitId, c.GeographicAreaId, c.CameraType,
        c.Latitude, c.Longitude, c.Azimuth, c.HorizontalFov, c.EffectiveRange,
        c.VmsId, c.CredentialReference, c.RecordEvents,
        c.OperationalStatus, c.ConnectivityStatus, c.MaintenanceStatus,
    };
}
