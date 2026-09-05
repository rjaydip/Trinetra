using System.Text.Json;
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

/// <summary>Connector targets, their health, capabilities and discovered cameras.</summary>
/// <remarks>
/// <para>
/// The onboarding order is: <c>POST /vms</c> to register the target, <c>PUT
/// /vms/{id}/credential</c> to give it a credential, <c>POST /vms/{id}/test</c> to confirm the
/// device answers, then <c>POST /vms/{id}/state</c> with <c>Active</c> to hand it to the workers.
/// Cameras appear under <c>GET /vms/{id}/cameras</c> on their own after the first inventory poll.
/// </para>
/// <para>
/// <b>There is no route that onboards a single camera</b>, and that is structural rather than
/// unfinished: a target is polled as a whole, because per-camera work against a vendor device is
/// ~2,670 req/s at 80,000 cameras and no NVR survives it.
/// </para>
/// </remarks>
public static class VmsEndpoints
{
    /// <summary>Widest status-history window a single query may span.</summary>
    private const int MaxStatusHistoryDays = 30;

    private const int DefaultStatusHistoryLimit = 200;
    private const int MaxStatusHistoryLimit = 1000;

    public static void MapVmsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/vms").WithTags(ApiTags.Vms).RequireAuthorization();

        group.MapGet("/", ListAsync)
          .RequirePermission("vms.read")
          .WithSummary("List the VMS targets the caller can reach")
          .WithDescription(
              "Every connector target within the caller's organization and geography scope, with "
              + "its vendor, endpoint, current state and expected camera count. Targets outside "
              + "scope are absent rather than refused. Credential *references* appear here; "
              + "credential values never do.");

        group.MapGet("/{id:guid}", GetAsync)
          .RequirePermission("vms.read")
          .WithSummary("Read one VMS target's configuration")
          .WithDescription(
              "The full configuration row: vendor, runtime class, endpoint, TLS verification, "
              + "state and credential reference.\n\n"
              + "A target the caller is not scoped to returns **404, not 403** — telling an "
              + "unauthorized caller that an id exists is itself a disclosure.");

        group.MapPost("/", RegisterAsync)
          .RequirePermission("vms.create")
          .WithSummary("Register a VMS, NVR or camera gateway")
          .WithDescription(
              "**Step 1 of onboarding, and the only way cameras enter the system.** Creates the "
              + "connector target a worker will poll; its cameras are then discovered, not "
              + "registered individually.\n\n"
              + "`vendor` selects the adapter — CP Plus and other Dahua OEM units use "
              + "`DahuaCgi`. `credentialReference` names where the secret will live; the secret "
              + "itself is written separately through `PUT /vms/{id}/credential`. Polling "
              + "intervals, rate limit and concurrency all have defaults tuned for a mid-size "
              + "NVR and only need setting for a device that cannot keep up.\n\n"
              + "The target is created in its default state and is not polled until `POST "
              + "/vms/{id}/state` activates it. An unparseable endpoint or unknown vendor is "
              + "rejected here rather than becoming an unexplained dead site later. So is a "
              + "deactivated `organizationUnitId` or `siteId` — an active-looking target attached "
              + "to a retired unit would otherwise never appear in anyone's scope.");

        group.MapPut("/{id:guid}", ReplaceAsync)
          .RequirePermission("vms.update")
          .WithSummary("Replace a VMS target's configuration")
          .WithDescription(
              "A full replacement, not a patch: every field is taken from the body, and one left "
              + "out reverts to its default. Read the target first and send it back modified.\n\n"
              + "Does not change the target's state, and does not write a credential — those are "
              + "`POST /vms/{id}/state` and `PUT /vms/{id}/credential`. Changing `endpoint` or "
              + "`vendor` takes effect at the worker's next poll cycle.\n\n"
              + "`organizationUnitId` and `siteId` must reference an **active** unit and site — a "
              + "deactivated one is a 400, not a silent drop out of scope resolution.\n\n"
              + "Omitting `verifyTls` sets it to `true`, the safe default — **not** the target's "
              + "previous value, because this endpoint replaces rather than patches. Sending "
              + "`verifyTls: false` explicitly disables certificate verification and is recorded "
              + "as such in the audit log.\n\n"
              + "The before and after values are recorded in the audit log, with the credential "
              + "reference kept and its value never present to begin with.");

        group.MapPost("/{id:guid}/state", SetStateAsync)
          .RequirePermission("vms.update")
          .WithSummary("Start, stop or quarantine polling for a target")
          .WithDescription(
              "**Step 4 of onboarding: this is what puts a target into service.** The workers act "
              + "on state, so nothing is polled until it is `Active`, and setting it back stops "
              + "the polling without losing the configuration or the discovered inventory.\n\n"
              + "Quarantine is the state for a device that is answering badly enough to be worth "
              + "taking out of the fleet without deleting it. Send the value as a string; the 400 "
              + "response lists the states this build accepts.");

        group.MapDelete("/{id:guid}", RemoveAsync)
          .RequirePermission("vms.delete")
          .WithSummary("Remove a VMS target")
          .WithDescription(
              "A hard delete that cascades: the discovered camera inventory, capability matrix, "
              + "health history, event cursor and connection-test records for this target all go "
              + "with it. Events already published to the bus and stored are keyed by source and "
              + "remain. `camera_status_history` rows for this target's cameras are **not** "
              + "cascaded either — they carry no foreign key to the target by design (an "
              + "append-only timeline, not owned state) and stay queryable by `targetId` after "
              + "deletion, ageing out under the normal retention window.\n\n"
              + "**The target must not be `Active`.** Set its state to something else with "
              + "`POST /vms/{id}/state` first — a 409 otherwise. This stops a delete from racing "
              + "a worker that currently holds the target's lease.\n\n"
              + "To stop polling a device without losing any of that, set its state instead. The "
              + "deleted configuration is captured in the audit row.\n\n"
              + "Gated on `vms.delete`, not `vms.update`: permanently destroying a target and its "
              + "whole inventory is a distinct, irreversible action from editing it.");

        // ---- Monitoring ----------------------------------------------------

        group.MapGet("/{id:guid}/health", HealthAsync)
          .RequirePermission("vms.read")
          .WithSummary("Read a target's connector health history")
          .WithDescription(
              "What the workers have observed about this target, newest first: poll latency, "
              + "camera count seen, consecutive failures, whether the circuit breaker is open, "
              + "the last error, events collected since the previous check, and how far the event "
              + "cursor is behind.\n\n"
              + "`cursorLagSeconds` is the number to watch — a healthy target with a growing lag "
              + "is collecting events slower than the device produces them.\n\n"
              + "Defaults to the last 50 checks within 7 days; `limit` and `days` narrow or widen "
              + "that.");

        group.MapGet("/{id:guid}/capabilities", CapabilitiesAsync)
          .RequirePermission("vms.read")
          .WithSummary("Read what this target's adapter can actually do")
          .WithDescription(
              "The capability matrix probed when a worker last connected, plus the adapter "
              + "version that probed it. No VMS supports every capability, so a client should ask "
              + "here rather than assume — an unsupported call is a wasted round trip against a "
              + "device that is already rate limited.\n\n"
              + "Served from the stored matrix and **never by probing the device**: letting a "
              + "dashboard render trigger probes would turn one page load into thousands of "
              + "vendor round trips.\n\n"
              + "404 with 'Not probed yet' means the target exists but no worker has reached it "
              + "— check its state and its connection test before reading anything into it.");

        group.MapGet("/{id:guid}/cameras", CamerasAsync)
          .RequirePermission("vms.read")
          .WithSummary("List the cameras discovered behind this target")
          .WithDescription(
              "**The camera inventory, and it is read-only.** Cameras are not onboarded through "
              + "this API — a worker calls the adapter's inventory poll for the target's whole "
              + "estate and upserts what comes back, so rows appear here on their own once the "
              + "target is active and has been reached. An empty list on a healthy target means "
              + "the first inventory poll has not run yet.\n\n"
              + "Each row carries the vendor's native id, the platform camera id it maps to, "
              + "model and firmware, whether it is enabled and recording, its last-seen time, and "
              + "its stream **references**. Model 3 never touches video: a reference is an address "
              + "to hand to a player or to Model 2, not a stream.");

        group.MapGet("/{id:guid}/cameras/{nativeCameraId}/status-history", CameraStatusHistoryAsync)
          .RequirePermission("vms.read")
          .WithSummary("Read one camera's status timeline")
          .WithDescription(
              "How this camera's health, enabled and recording flags have changed over time — "
              + "newest first.\n\n"
              + "**Transitions, not samples.** A row exists only where something actually "
              + "changed, so a camera healthy for a month returns one row rather than a month of "
              + "identical readings. Recording every poll would be 230M rows/day at 80,000 "
              + "cameras. The status at any instant is the newest row at or before it, and each "
              + "row carries the previous value as well as the new one so a timeline can be drawn "
              + "without a second lookup.\n\n"
              + "The last element may pre-date `from`: it is the transition that established the "
              + "status the window opened with. Null previous values mean this is the camera's "
              + "first observation, not that the earlier value is unknown.\n\n"
              + "Defaults to the last 30 days; `from`, `to` and `limit` narrow it. This does not "
              + "answer *whether the camera was being polled* at a given moment — that is "
              + "`/vms/{id}/health` for the target, and `lastSeen` on the camera.");

        // ---- Fleet overview -------------------------------------------------

        app.MapGet("/api/v1/overview", OverviewAsync)
          .RequireAuthorization().WithTags(ApiTags.Vms).RequirePermission("vms.read")
          .WithSummary("Fleet totals for the caller's scope")
          .WithDescription(
              "One row of counters for a landing page: targets, how many are active, how many are "
              + "quarantined, total cameras and how many are unreachable. Aggregated in the "
              + "database over the caller's scope, so two operators with different grants "
              + "legitimately see different totals.");
    }

    /// <summary>
    /// Validates a request and builds the domain record.
    /// </summary>
    /// <remarks>
    /// Rejected at the boundary rather than in a worker. A target with an unparseable endpoint
    /// that reaches the fleet surfaces later as an unexplained dead site, which is far harder to
    /// diagnose than a 400 at the moment somebody typed it.
    /// </remarks>
    private static bool TryBuild(
        ConnectorTargetRequest request, Guid id, out ConnectorTarget? target,
        out ProblemHttpResult? problem)
    {
        target = null;
        problem = null;

        if (!Enum.TryParse<VendorKind>(request.Vendor, ignoreCase: true, out var vendor))
        {
            problem = TypedResults.Problem(
                title: "Unknown vendor",
                detail: $"Valid vendors: {string.Join(", ", Enum.GetNames<VendorKind>())}. "
                      + "CP Plus and other Dahua OEM units use DahuaCgi.",
                statusCode: StatusCodes.Status400BadRequest);
            return false;
        }

        if (!Uri.TryCreate(
                request.Endpoint.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    ? request.Endpoint : $"http://{request.Endpoint}",
                UriKind.Absolute, out _))
        {
            problem = TypedResults.Problem(
                title: "Invalid endpoint",
                detail: $"'{request.Endpoint}' is not a usable address.",
                statusCode: StatusCodes.Status400BadRequest);
            return false;
        }

        var runtimeClass = RuntimeClass.Managed;
        if (request.RuntimeClass is { } rc
            && !Enum.TryParse(rc, ignoreCase: true, out runtimeClass))
        {
            problem = TypedResults.Problem(
                title: "Unknown runtime class",
                detail: "Valid values: Managed, Native.",
                statusCode: StatusCodes.Status400BadRequest);
            return false;
        }

        target = new ConnectorTarget
        {
            Id = id,
            Code = request.Code,
            OrganizationUnitId = request.OrganizationUnitId,
            SiteId = request.SiteId,
            DisplayName = request.DisplayName,
            Vendor = vendor,
            RuntimeClass = runtimeClass,
            Endpoint = request.Endpoint,
            CredentialReference = request.CredentialReference,
            VerifyTls = request.VerifyTls,
            RateLimitPerSecond = request.RateLimitPerSecond ?? 5.0,
            RateLimitBurst = request.RateLimitBurst ?? 10,
            InventoryPollInterval = TimeSpan.FromSeconds(request.InventoryPollSeconds ?? 300),
            StatusPollInterval = TimeSpan.FromSeconds(request.StatusPollSeconds ?? 30),
            EventPollInterval = TimeSpan.FromSeconds(request.EventPollSeconds ?? 10),
            MaxConcurrentRequests = request.MaxConcurrentRequests ?? 4,
            ExpectedCameraCount = request.ExpectedCameraCount,
        };

        return true;
    }

    /// <summary>Capability row. <c>notes</c> arrives as JSON text and is parsed here.</summary>


    /// <summary>
    /// Projects the domain record onto the wire contract.
    /// </summary>
    /// <remarks>
    /// Deliberately explicit rather than serialising <see cref="ConnectorTarget"/> directly: a
    /// field added to the domain type would otherwise appear in the API automatically, which is
    /// how internal state and lease bookkeeping leak into a public contract.
    /// </remarks>
    private static VmsResponse ToResponse(ConnectorTarget t) => new(
        t.Id, t.Code, t.OrganizationUnitId, t.SiteId, t.DisplayName,
        t.Vendor.ToString(), t.RuntimeClass.ToString(), t.Endpoint, t.CredentialReference,
        t.VerifyTls, t.State.ToString(), t.ExpectedCameraCount);

    /// <summary>Audit projection. Records the credential <i>reference</i>, never its value.</summary>
    private static object Redact(ConnectorTargetRequest r) => new
    {
        r.Code, r.OrganizationUnitId, r.SiteId, r.DisplayName, r.Vendor,
        r.Endpoint, r.CredentialReference, r.VerifyTls, r.ExpectedCameraCount,
    };

    private static object Redact(ConnectorTarget t) => new
    {
        t.Code, t.OrganizationUnitId, t.SiteId, t.DisplayName,
        Vendor = t.Vendor.ToString(), t.Endpoint, t.CredentialReference,
        t.VerifyTls, t.ExpectedCameraCount,
        // Included so the delete audit row (RemoveAsync) records the state the target was in
        // when removed — the 409 guard right before it exists entirely because of that value.
        State = t.State.ToString(),
    };

    private static async Task<Ok<IReadOnlyList<VmsResponse>>> ListAsync(
        ConnectorTargetRepository repo, HttpContext http, CancellationToken ct)
    {
        var targets = await repo.ListAsync(CallerContextFactory.From(http), ct);
        return TypedResults.Ok<IReadOnlyList<VmsResponse>>([.. targets.Select(ToResponse)]);
    }

    private static async Task<Results<Ok<VmsResponse>, NotFound>> GetAsync(
        Guid id, ConnectorTargetRepository repo, HttpContext http, CancellationToken ct)
    {
        var target = await repo.GetAsync(id, CallerContextFactory.From(http), ct);

        // Out-of-scope targets return 404 rather than 403: telling an unauthorised caller
        // that an id exists is itself a disclosure.
        return target is null
            ? TypedResults.NotFound()
            : TypedResults.Ok(ToResponse(target));
    }

    private static async Task<Results<Created<CreatedResponse>, ProblemHttpResult>> RegisterAsync(
        [FromBody] ConnectorTargetRequest request, ConnectorTargetRepository repo,
        NpgsqlDataSource db, HttpContext http, CancellationToken ct)
    {
        var caller = CallerContextFactory.From(http);

        if (!TryBuild(request, Guid.Empty, out var target, out var problem))
        {
            return problem!;
        }

        await using var work = await UnitOfWork.BeginAsync(db, ct);

        var id = await repo.UpsertAsync(target!, caller, work, ct);
        await work.AuditAsync(caller, "create", "connector_target", id.ToString(),
            before: null, after: Redact(request), request.OrganizationUnitId, ct);
        await work.CommitAsync(ct);

        return TypedResults.Created($"/api/v1/vms/{id}", new CreatedResponse(id));
    }

    private static async Task<Results<NoContent, NotFound, ProblemHttpResult>> ReplaceAsync(
        Guid id, [FromBody] ConnectorTargetRequest request, ConnectorTargetRepository repo,
        NpgsqlDataSource db, HttpContext http, CancellationToken ct)
    {
        var caller = CallerContextFactory.From(http);
        var before = await repo.GetAsync(id, caller, ct);

        if (before is null)
        {
            return TypedResults.NotFound();
        }

        if (!TryBuild(request, id, out var target, out var problem))
        {
            return problem!;
        }

        await using var work = await UnitOfWork.BeginAsync(db, ct);

        await repo.UpsertAsync(target!, caller, work, ct);
        await work.AuditAsync(caller, "update", "connector_target", id.ToString(),
            Redact(before), Redact(request), request.OrganizationUnitId, ct);
        await work.CommitAsync(ct);

        return TypedResults.NoContent();
    }

    private static async Task<Results<NoContent, NotFound, ProblemHttpResult>> SetStateAsync(
        Guid id, [FromBody] TargetStateRequest request, ConnectorTargetRepository repo,
        NpgsqlDataSource db, HttpContext http, CancellationToken ct)
    {
        var caller = CallerContextFactory.From(http);

        if (!Enum.TryParse<TargetState>(request.State, ignoreCase: true, out var state))
        {
            return TypedResults.Problem(
                title: "Unknown state",
                detail: $"Valid states: {string.Join(", ", Enum.GetNames<TargetState>())}.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        await using var work = await UnitOfWork.BeginAsync(db, ct);

        if (!await repo.SetStateAsync(id, state, caller, work, ct))
        {
            return TypedResults.NotFound();
        }

        await work.AuditAsync(caller, "update", "connector_target", id.ToString(),
            before: null, after: new { state = state.ToString() }, organizationUnitId: null, ct);
        await work.CommitAsync(ct);

        return TypedResults.NoContent();
    }

    private static async Task<Results<NoContent, NotFound, ProblemHttpResult>> RemoveAsync(
        Guid id, ConnectorTargetRepository repo, NpgsqlDataSource db,
        HttpContext http, CancellationToken ct)
    {
        var caller = CallerContextFactory.From(http);
        var before = await repo.GetAsync(id, caller, ct);

        if (before is null)
        {
            return TypedResults.NotFound();
        }

        // Deleting a target a worker currently holds the lease on races its in-flight writes.
        // Quarantine already means "not polled", so only Active blocks the delete.
        if (before.State == TargetState.Active)
        {
            return TypedResults.Problem(
                title: "Target is active",
                detail: "Set the target's state to something other than Active with "
                      + "POST /vms/{id}/state before deleting it.",
                statusCode: StatusCodes.Status409Conflict);
        }

        await using var work = await UnitOfWork.BeginAsync(db, ct);

        if (!await repo.DeleteAsync(id, caller, work, ct))
        {
            return TypedResults.NotFound();
        }

        await work.AuditAsync(caller, "delete", "connector_target", id.ToString(),
            Redact(before), after: null, before.OrganizationUnitId, ct);
        await work.CommitAsync(ct);

        return TypedResults.NoContent();
    }

    private static async Task<Results<Ok<IReadOnlyList<ConnectorHealthResponse>>, NotFound>> HealthAsync(
        Guid id, int? limit, int? days, ConnectorTargetRepository repo, FederationQueryRepository queries,
        HttpContext http, CancellationToken ct)
    {
        var caller = CallerContextFactory.From(http);

        // Reached through the repository so the scope check is the same one the list uses.
        if (await repo.GetAsync(id, caller, ct) is null)
        {
            return TypedResults.NotFound();
        }

        var rows = await queries.HealthAsync(id, limit ?? 50, days ?? 7, ct);

        return TypedResults.Ok<IReadOnlyList<ConnectorHealthResponse>>(
        [
            .. rows.Select(r => new ConnectorHealthResponse(
                r.CheckedAt, r.Status, r.LatencyMs, r.CameraCount, r.ConsecutiveFailures,
                r.CircuitOpen, r.LastError, r.EventsSinceCheck, r.CursorLagSeconds)),
        ]);
    }

    private static async Task<Results<Ok<CapabilityResponse>, ProblemHttpResult>> CapabilitiesAsync(
        Guid id, ConnectorTargetRepository repo, FederationQueryRepository queries,
        HttpContext http, CancellationToken ct)
    {
        var caller = CallerContextFactory.From(http);

        if (await repo.GetAsync(id, caller, ct) is null)
        {
            return TypedResults.Problem(
                title: "Not found", statusCode: StatusCodes.Status404NotFound);
        }

        // Served from the stored matrix, never by probing the device. At 80k cameras,
        // letting a page render trigger probes turns one dashboard load into thousands of
        // vendor round trips.
        var row = await queries.CapabilitiesAsync(id, ct);

        if (row is null)
        {
            return TypedResults.Problem(
                title: "Not probed yet",
                detail: "This target has not been contacted by a worker. Capabilities appear "
                      + "once it connects for the first time.",
                statusCode: StatusCodes.Status404NotFound);
        }

        return TypedResults.Ok(new CapabilityResponse(
            row.Supported, row.AdapterVersion, row.ProbedAt,
            JsonSerializer.Deserialize<Dictionary<string, string>>(row.NotesJson ?? "{}")
                ?? []));
    }

    private static async Task<Results<Ok<IReadOnlyList<FederatedCameraResponse>>, NotFound>> CamerasAsync(
        Guid id, ConnectorTargetRepository repo, FederationQueryRepository queries,
        HttpContext http, CancellationToken ct)
    {
        var caller = CallerContextFactory.From(http);

        if (await repo.GetAsync(id, caller, ct) is null)
        {
            return TypedResults.NotFound();
        }

        var rows = await queries.CamerasAsync(id, ct);

        return TypedResults.Ok<IReadOnlyList<FederatedCameraResponse>>(
        [
            .. rows.Select(r => new FederatedCameraResponse(
                r.NativeCameraId, r.CameraId, r.Name, r.VendorModel, r.Firmware,
                r.IsEnabled, r.IsRecording, r.Health, r.LastSeen,
                r.StreamReferences ?? [], r.StatusChangedAt)),
        ]);
    }

    private static async Task<Results<Ok<CameraStatusHistoryResponse>, NotFound, ProblemHttpResult>> CameraStatusHistoryAsync(
        Guid id, string nativeCameraId, DateTimeOffset? from, DateTimeOffset? to, int? limit,
        ConnectorTargetRepository repo, FederationQueryRepository queries,
        HttpContext http, CancellationToken ct)
    {
        var caller = CallerContextFactory.From(http);

        if (await repo.GetAsync(id, caller, ct) is null)
        {
            return TypedResults.NotFound();
        }

        // Defaulted rather than required, unlike /events. Transitions are rare — a stable
        // camera has one row, not one per poll — so a default window cannot produce the
        // billion-row scan that made a mandatory range necessary there.
        var end = to ?? DateTimeOffset.UtcNow;
        var start = from ?? end.AddDays(-MaxStatusHistoryDays);

        if (end <= start)
        {
            return TypedResults.Problem(
                title: "Invalid time range", detail: "'to' must be after 'from'.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (end - start > TimeSpan.FromDays(MaxStatusHistoryDays))
        {
            return TypedResults.Problem(
                title: "Time range too wide",
                detail: $"The window may span at most {MaxStatusHistoryDays} days. The table "
                      + "is partitioned by day, and an unbounded window defeats the pruning "
                      + "that keeps this query cheap.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var rows = await queries.CameraStatusHistoryAsync(
            id, nativeCameraId, start, end,
            Math.Clamp(limit ?? DefaultStatusHistoryLimit, 1, MaxStatusHistoryLimit), ct);

        return TypedResults.Ok(new CameraStatusHistoryResponse(
            nativeCameraId, start, end,
            [
                .. rows.Select(r => new CameraStatusChangeResponse(
                    r.ChangedAt, r.PreviousHealth, r.Health,
                    r.PreviousEnabled, r.IsEnabled,
                    r.PreviousRecording, r.IsRecording)),
            ]));
    }

    private static async Task<Ok<OverviewResponse>> OverviewAsync(
        FederationQueryRepository queries, HttpContext http, CancellationToken ct)
    {
        var caller = CallerContextFactory.From(http);
        caller.Require("vms.read");

        var row = await queries.OverviewAsync(caller, ct);

        return TypedResults.Ok(new OverviewResponse(
            row.Targets, row.ActiveTargets, row.QuarantinedTargets,
            row.Cameras, row.UnreachableCameras));
    }
}
