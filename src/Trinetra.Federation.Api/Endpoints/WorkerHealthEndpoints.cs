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

/// <summary>Liveness for Model 2's AI-worker processes.</summary>
public static partial class WorkerHealthEndpoints
{
    private const int ListHardCap = 1000;
    private const int MaxWorkerIdLength = 128;
    private const int MaxHostnameLength = 253;

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
        int? page, int? pageSize, AiWorkerHealthRepository repo, HttpContext http, CancellationToken ct)
    {
        var caller = CallerContextFactory.From(http);
        var q = new PageQuery(page, pageSize);
        var rows = await repo.ListAsync(
            caller, new PageWindow(q.Limit(ListHardCap, ListHardCap), q.Offset(ListHardCap)), ct);

        return Paginate.Render(http, q, ListHardCap,
            [.. rows.Items.Select(ToResponse)], rows.Total);
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
            before: ToResponse(removed), after: null, organizationUnitId: null, ct);
        await work.CommitAsync(ct);

        return TypedResults.NoContent();
    }

    private static AiWorkerHealthResponse ToResponse(AiWorkerHealthRow r) => new(
        r.Id, r.ApiKeyId, r.ApiKeyName, r.WorkerId, r.Hostname,
        r.FirstSeenAt, r.LastHeartbeatAt, r.ReportedAt,
        r.ReportedAt is { } reported ? (r.LastHeartbeatAt - reported).TotalSeconds : null);
}
