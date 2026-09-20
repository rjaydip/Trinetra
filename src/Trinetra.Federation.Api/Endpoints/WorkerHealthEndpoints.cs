using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using Trinetra.Federation.Api.Auth;
using Trinetra.Federation.Api.Contracts;
using Trinetra.Federation.Api.OpenApi;
using Trinetra.Federation.Storage;
using Trinetra.Federation.Storage.Repositories;

namespace Trinetra.Federation.Api.Endpoints;

/// <summary>Liveness for the AI-worker processes.</summary>
public static partial class WorkerHealthEndpoints
{
    private const int ListHardCap = 1000;
    private const int MaxWorkerIdLength = 128;
    private const int MaxHostnameLength = 253;
    private const int MaxClaimCapacity = 500;

    /// <summary>How long an unrenewed camera lease stays valid before another worker may claim
    /// it — the failover latency for a stopped/crashed worker's cameras (same trade-off
    /// <see cref="LeaseStore"/> documents for VMS connector targets: shorter means faster
    /// failover, longer means more tolerance for one slow poll).</summary>
    private static readonly TimeSpan CameraLeaseTtl = TimeSpan.FromSeconds(90);

    [GeneratedRegex(@"^[A-Za-z0-9._:-]+$")]
    private static partial Regex WorkerIdPattern();

    [GeneratedRegex(@"^[A-Za-z0-9._:-]+$")]
    private static partial Regex HostnamePattern();

    public static void MapWorkerHealthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/worker-health")
            .WithTags(ApiTags.WorkerHealth).RequireAuthorization();

        group.MapPost("/heartbeat", HeartbeatAsync)
          .RequirePermission("worker.heartbeat")
          .ProducesProblem(StatusCodes.Status400BadRequest)
          .ProducesProblem(StatusCodes.Status403Forbidden)
          .WithSummary("Report an AI worker's liveness")
          .WithDescription(
              "What `ai-worker/monitoring/heartbeat.py` POSTs on its configured interval. An "
              + "upsert on `(api key, workerId, hostname)` — the heartbeat is bound to the API "
              + "key it is sent with, so one integration cannot report liveness for another's "
              + "workers (finding 17-M1).\n\n"
              + "Accepted **from an API-key principal only** — a heartbeat a human could assert "
              + "is not a liveness signal. A user token gets **403**.\n\n"
              + "`reportedAt` is the worker's own clock, kept only so clock drift is visible; "
              + "the server stamps the authoritative last-seen time itself (17-M2). Not "
              + "audited — high-frequency observability, the same treatment `worker_node` gets.");

        group.MapGet("/", ListAsync)
          .WithPaginatedResponse<AiWorkerHealthResponse>()
          .RequirePermission("worker.read")
          .WithSummary("List known AI workers and their last heartbeat")
          .WithDescription(
              "Every worker that has reported in, with the API key that reported it. A worker "
              + "whose `lastHeartbeatAt` has gone stale is the operational signal that instance "
              + "has died or lost connectivity; `clockDriftSeconds` surfaces a worker whose "
              + "clock disagrees with the server.\n\n"
              + "Not organization- or geography-scoped — an AI worker is not an org/geo entity.\n\n"
              + Paginate.Doc);

        group.MapPost("/cameras/claim", ClaimCamerasAsync)
          .RequirePermission("worker.heartbeat")
          .ProducesProblem(StatusCodes.Status403Forbidden)
          .WithSummary("Claim/renew up to Capacity cameras for one AI-worker instance")
          .WithDescription(
              "Dynamic replacement for `ai-worker/scaling.py`'s old static hash-partition (v1.27, "
              + "`camera_worker_lease`) — call this on every poll, not just at startup. VMS-"
              + "discovered candidates (`federated_camera`, not yet reconciled into the registry) "
              + "are claimed before standalone registry cameras, until `capacity` is reached or "
              + "candidates run out. A lease this worker already holds is renewed by the same "
              + "call; a camera whose lease has gone stale (another worker stopped renewing it) "
              + "becomes claimable here — no coordinator, no separate 'is worker X still alive' "
              + "check needed. **API-key callers only**, same as the heartbeat route.");

        group.MapPost("/cameras/release", ReleaseCamerasAsync)
          .RequirePermission("worker.heartbeat")
          .ProducesProblem(StatusCodes.Status403Forbidden)
          .WithSummary("Release every camera this worker currently holds")
          .WithDescription(
              "Call on clean shutdown so this worker's cameras are picked up by another worker "
              + "immediately, instead of waiting out the lease TTL. Best-effort — a worker that "
              + "crashes without calling this is exactly what the TTL exists to handle.");

        group.MapDelete("/{id:guid}", RetireAsync)
          .RequirePermission("worker.manage")
          .WithSummary("Retire an AI-worker health record")
          .WithDescription(
              "Removes one record — for a decommissioned worker, or for the stale rows a fleet "
              + "resize leaves behind (drop `WORKER_COUNT` from 4 to 2 and `ai-worker-2-of-4` / "
              + "`-3-of-4` never report again). Without this they read as a permanent crash. "
              + "Audited. 404 if the id is unknown.");
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> HeartbeatAsync(
        [FromBody] WorkerHeartbeatRequest request, AiWorkerHealthRepository repo,
        HttpContext http, CancellationToken ct)
    {
        var caller = CallerContextFactory.From(http);

        if (caller.ApiKeyId is null)
        {
            return TypedResults.Problem(
                title: "Heartbeats come from API keys only",
                detail: "A worker heartbeat is a machine liveness signal and is accepted only "
                      + "from an API-key principal, not a user token.",
                statusCode: StatusCodes.Status403Forbidden);
        }

        var workerId = request.WorkerId?.Trim() ?? "";
        var hostname = request.Hostname?.Trim() ?? "";

        var problem = Validate(workerId, hostname);
        if (problem is not null)
        {
            return problem;
        }

        await repo.UpsertHeartbeatAsync(caller, workerId, hostname, request.ReportedAt, ct);
        return TypedResults.NoContent();
    }

    private static async Task<Results<Ok<IReadOnlyList<ClaimedCameraResponse>>, ProblemHttpResult>> ClaimCamerasAsync(
        [FromBody] ClaimCamerasRequest request, CameraLeaseRepository leases,
        HttpContext http, CancellationToken ct)
    {
        var caller = CallerContextFactory.From(http);

        if (caller.ApiKeyId is null)
        {
            return TypedResults.Problem(
                title: "Camera claims come from API keys only",
                detail: "A camera claim is a machine assignment request and is accepted only "
                      + "from an API-key principal, not a user token.",
                statusCode: StatusCodes.Status403Forbidden);
        }

        var workerId = request.WorkerId?.Trim() ?? "";
        var hostname = request.Hostname?.Trim() ?? "";

        var problem = Validate(workerId, hostname);
        if (problem is not null)
        {
            return problem;
        }

        if (request.Capacity is < 1 or > MaxClaimCapacity)
        {
            return TypedResults.Problem(
                title: "Invalid capacity",
                detail: $"capacity must be between 1 and {MaxClaimCapacity}.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var assignments = await leases.ClaimAsync(
            caller, workerId, hostname, request.Capacity, CameraLeaseTtl, ct);
        var details = await leases.ResolveDetailsAsync(assignments, ct);
        // Preserves ClaimAsync's own VMS-first ordering — ResolveDetailsAsync resolves the two
        // sources with separate queries, so its own result order doesn't necessarily match.
        var byRef = details.ToDictionary(d => d.CameraRef);

        return TypedResults.Ok<IReadOnlyList<ClaimedCameraResponse>>(
        [
            .. assignments
                .Where(a => byRef.ContainsKey(a.CameraRef))
                .Select(a => byRef[a.CameraRef])
                .Select(d => new ClaimedCameraResponse(
                    d.CameraRef, d.Source, d.Name, d.StreamReference, d.StreamPreference,
                    d.NativeHlsUrl, d.NativeWebrtcUrl, d.CredentialReference)),
        ]);
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> ReleaseCamerasAsync(
        [FromBody] ReleaseCamerasRequest request, CameraLeaseRepository leases,
        HttpContext http, CancellationToken ct)
    {
        var caller = CallerContextFactory.From(http);

        if (caller.ApiKeyId is null)
        {
            return TypedResults.Problem(
                title: "Camera release comes from API keys only",
                detail: "A camera release is a machine liveness signal and is accepted only "
                      + "from an API-key principal, not a user token.",
                statusCode: StatusCodes.Status403Forbidden);
        }

        var workerId = request.WorkerId?.Trim() ?? "";
        var hostname = request.Hostname?.Trim() ?? "";

        var problem = Validate(workerId, hostname);
        if (problem is not null)
        {
            return problem;
        }

        await leases.ReleaseAllAsync(caller, workerId, hostname, ct);
        return TypedResults.NoContent();
    }

    private static ProblemHttpResult? Validate(string workerId, string hostname)
    {
        var error = workerId.Length switch
        {
            0 => "workerId is required.",
            > MaxWorkerIdLength => $"workerId is at most {MaxWorkerIdLength} characters.",
            _ when !WorkerIdPattern().IsMatch(workerId) =>
                "workerId may contain only letters, digits and the characters . _ : -",
            _ => null,
        } ?? hostname.Length switch
        {
            0 => "hostname is required.",
            > MaxHostnameLength => $"hostname is at most {MaxHostnameLength} characters.",
            _ when !HostnamePattern().IsMatch(hostname) =>
                "hostname may contain only letters, digits and the characters . _ : -",
            _ => null,
        };

        return error is null
            ? null
            : TypedResults.Problem(
                title: "Invalid heartbeat", detail: error,
                statusCode: StatusCodes.Status400BadRequest);
    }

    private static async Task<IResult> ListAsync(
        int? page, int? pageSize, AiWorkerHealthRepository repo, CameraLeaseRepository leases,
        HttpContext http, CancellationToken ct)
    {
        var caller = CallerContextFactory.From(http);
        var q = new PageQuery(page, pageSize);
        var rows = await repo.ListAsync(
            caller, new PageWindow(q.Limit(ListHardCap, ListHardCap), q.Offset(ListHardCap)), ct);

        // A worker's lease summary isn't itself organization/geography-scoped (same posture as
        // the heartbeat list), so one fetch here covers every row on the page.
        var leaseSummaries = await leases.ListWorkerSummariesAsync(caller, ct);
        var leasesByWorker = leaseSummaries.ToDictionary(s => (s.ApiKeyId, s.WorkerId, s.Hostname));

        return Paginate.Render(http, q, ListHardCap,
            [.. rows.Items.Select(r => ToResponse(r, leasesByWorker.GetValueOrDefault((r.ApiKeyId, r.WorkerId, r.Hostname))))],
            rows.Total);
    }

    private static async Task<Results<NoContent, NotFound>> RetireAsync(
        Guid id, AiWorkerHealthRepository repo, NpgsqlDataSource db,
        HttpContext http, CancellationToken ct)
    {
        var caller = CallerContextFactory.From(http);

        await using var work = await UnitOfWork.BeginAsync(db, ct);

        var removed = await repo.RetireAsync(id, caller, work, ct);
        if (removed is null)
        {
            return TypedResults.NotFound();
        }

        await work.AuditAsync(caller, "delete", "ai_worker_health", id.ToString(),
            before: ToResponse(removed, leaseSummary: null), after: null, organizationUnitId: null, ct);
        await work.CommitAsync(ct);

        return TypedResults.NoContent();
    }

    private static AiWorkerHealthResponse ToResponse(AiWorkerHealthRow r, CameraLeaseWorkerSummary? leaseSummary) => new(
        r.Id, r.ApiKeyId, r.ApiKeyName, r.WorkerId, r.Hostname,
        r.FirstSeenAt, r.LastHeartbeatAt, r.ReportedAt,
        r.ReportedAt is { } reported ? (r.LastHeartbeatAt - reported).TotalSeconds : null,
        leaseSummary?.LeasedCameraCount ?? 0, leaseSummary?.CameraRefs ?? [],
        leaseSummary?.CameraNames ?? []);
}
