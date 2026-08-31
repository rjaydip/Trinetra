using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Trinetra.Federation.Api.Auth;
using Trinetra.Federation.Api.Contracts;
using Trinetra.Federation.Api.OpenApi;
using Trinetra.Federation.Storage.Repositories;

namespace Trinetra.Federation.Api.Endpoints;

/// <summary>Liveness for Model 2's AI-worker processes.</summary>
public static class WorkerHealthEndpoints
{
    public static void MapWorkerHealthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/worker-health")
            .WithTags(ApiTags.WorkerHealth).RequireAuthorization();

        group.MapPost("/heartbeat", HeartbeatAsync)
          .RequirePermission("worker.heartbeat")
          .WithSummary("Report an AI worker's liveness")
          .WithDescription(
              "What `ai-worker/monitoring/heartbeat.py` POSTs on its configured interval. An "
              + "upsert on `workerId` — not audited, matching the treatment `worker_node` "
              + "already gets: non-sensitive, high-frequency, observability only.");

        group.MapGet("/", ListAsync)
          .RequirePermission("worker.heartbeat")
          .WithSummary("List known AI workers and their last heartbeat")
          .WithDescription(
              "Every worker that has ever reported in. A worker whose `lastHeartbeatAt` has gone "
              + "stale is the operational signal that instance has died or lost connectivity.");
    }

    private static async Task<NoContent> HeartbeatAsync(
        [FromBody] WorkerHeartbeatRequest request, AiWorkerHealthRepository repo,
        CancellationToken ct)
    {
        await repo.UpsertHeartbeatAsync(request.WorkerId, request.Hostname, request.ReportedAt, ct);
        return TypedResults.NoContent();
    }

    private static async Task<Ok<IReadOnlyList<AiWorkerHealthResponse>>> ListAsync(
        AiWorkerHealthRepository repo, CancellationToken ct)
    {
        var rows = await repo.ListAsync(ct);

        return TypedResults.Ok<IReadOnlyList<AiWorkerHealthResponse>>(
        [
            .. rows.Select(r => new AiWorkerHealthResponse(
                r.WorkerId, r.Hostname, r.FirstSeenAt, r.LastHeartbeatAt)),
        ]);
    }
}
