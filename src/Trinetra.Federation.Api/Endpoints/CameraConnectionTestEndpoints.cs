using System.Net;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using Trinetra.Federation.Api.Auth;
using Trinetra.Federation.Api.Contracts;
using Trinetra.Federation.Api.OpenApi;
using Trinetra.Federation.Core.Model;
using Trinetra.Federation.Runtime;
using Trinetra.Federation.Storage;
using Trinetra.Federation.Storage.Repositories;

namespace Trinetra.Federation.Api.Endpoints;

/// <summary>
/// A pre-save reachability probe for a standalone camera that has not been registered yet.
/// </summary>
/// <remarks>
/// <para>
/// Mirrors <c>POST /vms/{id}/test</c>'s asynchronous create-then-poll shape for operator-
/// experience consistency, but is otherwise unrelated: there is no camera id (the row does not
/// exist yet), no credential, and no vendor adapter. It opens a plain TCP socket to
/// <c>ipAddress:port</c> with a short timeout and reports only whether it opened.
/// </para>
/// <para>
/// Gated on <c>camera.create</c> — the same permission that registers the camera this test
/// precedes — rather than a new permission, since the probe's only purpose is to inform that
/// decision and it identifies no existing resource to scope against.
/// </para>
/// </remarks>
public static class CameraConnectionTestEndpoints
{
    public static void MapCameraConnectionTestEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/cameras/connection-test")
                       .WithTags(ApiTags.Cameras)
                       .RequireAuthorization();

        group.MapPost("/", StartAsync)
          .RequirePermission("camera.create")
          .WithSummary("Test whether a camera is reachable before registering it")
          .WithDescription(
              "**Host:port reachability only — not an authentication check.** Opens a plain TCP "
              + "connection to `ipAddress:port` with a short timeout and reports whether it "
              + "opened. There is no camera id and no credential in the request: this runs before "
              + "the camera is saved, and Model 1 has no vendor adapter to authenticate with "
              + "anyway. A reachable result says nothing about whether the protocol, port or any "
              + "future credential is actually correct for the device.\n\n"
              + "**Asynchronous, same idiom as `POST /vms/{id}/test`:** returns `202` with a "
              + "`testId` and a `statusUrl` to poll, so a black-holed host produces a clear "
              + "result rather than a dropped request.\n\n"
              + "Gated on `camera.create`, not a new permission — the only thing this informs is "
              + "the decision to register a camera, which already needs that permission.");

        group.MapGet("/{testId:guid}", ResultAsync)
          .RequirePermission("camera.create")
          .WithSummary("Poll one reachability test for its result")
          .WithDescription(
              "The status URL returned by `POST /cameras/connection-test`. `status` is `pending` "
              + "until the job finishes, then carries the outcome — `reachable` plus the connect "
              + "time, or a failure reason. A test abandoned by a host that died is closed out by "
              + "the sweeper rather than left pending forever.");
    }

    private static async Task<Results<Accepted<CameraConnectionTestAccepted>, ProblemHttpResult>>
        StartAsync(
            [FromBody] CameraConnectionTestRequest request,
            CameraConnectionTestRepository tests,
            CameraReachabilityProbe prober,
            NpgsqlDataSource db,
            ILogger<CameraReachabilityProbe> logger,
            HttpContext http,
            CancellationToken ct)
    {
        var caller = CallerContextFactory.From(http);
        caller.Require("camera.create");

        if (!TryValidate(request, out var protocol, out var ipAddress, out var problem))
        {
            return problem!;
        }

        // The claim row and its audit record share one transaction: a test that ran against a
        // host is exactly the record an incident review needs, same reasoning as the VMS test.
        await using var work = await UnitOfWork.BeginAsync(db, ct);

        var testId = await tests.ClaimAsync(protocol, ipAddress, request.Port, caller.Actor, work, ct);

        await work.AuditAsync(caller, "create", "camera_connection_test", testId.ToString(),
            before: null, after: new { protocol, ipAddress, request.Port },
            organizationUnitId: null, ct);
        await work.CommitAsync(ct);

        // Runs beyond the request, so it deliberately does not take the request's token — its
        // deadline is CameraReachabilityProbe.ConnectTimeout and the sweeper closes out anything
        // this instance abandons by dying.
        _ = Task.Run(
            async () =>
            {
                try
                {
                    await prober.ExecuteJobAsync(
                        testId, ipAddress, request.Port, db, Environment.MachineName,
                        CancellationToken.None);
                }
                catch (Exception ex)
                {
                    LogJobFaulted(logger, testId, ex);
                }
            },
            CancellationToken.None);

        var statusUrl = $"/api/v1/cameras/connection-test/{testId}";
        return TypedResults.Accepted(
            statusUrl, new CameraConnectionTestAccepted(testId, "pending", statusUrl));
    }

    private static async Task<Results<Ok<CameraConnectionTestResult>, NotFound>> ResultAsync(
        Guid testId, CameraConnectionTestRepository tests, HttpContext http, CancellationToken ct)
    {
        var caller = CallerContextFactory.From(http);
        caller.Require("camera.create");

        var row = await tests.GetAsync(testId, ct);
        if (row is null)
        {
            return TypedResults.NotFound();
        }

        return TypedResults.Ok(new CameraConnectionTestResult(
            row.Id, row.Protocol, row.IpAddress, row.Port, row.Status, row.RequestedAt,
            row.CompletedAt, row.FailureReason,
            row.ResultJson is null
                ? null
                : System.Text.Json.JsonSerializer.Deserialize<CameraReachabilityReport>(
                    row.ResultJson)));
    }

    /// <summary>
    /// Validates and normalizes the request. Pure, so it is unit-tested directly rather than
    /// only through a live endpoint.
    /// </summary>
    private static bool TryValidate(
        CameraConnectionTestRequest request,
        out string protocol, out string ipAddress, out ProblemHttpResult? problem)
    {
        protocol = "";
        ipAddress = "";
        problem = null;

        var normalizedProtocol = request.Protocol?.Trim().ToUpperInvariant();
        if (normalizedProtocol is null || !CameraVocab.Protocols.Contains(normalizedProtocol))
        {
            problem = TypedResults.Problem(
                title: "Invalid request",
                detail: $"protocol must be one of: {string.Join(", ", CameraVocab.Protocols)}.",
                statusCode: StatusCodes.Status400BadRequest);
            return false;
        }

        if (string.IsNullOrWhiteSpace(request.IpAddress)
            || !IPAddress.TryParse(request.IpAddress.Trim(), out _))
        {
            problem = TypedResults.Problem(
                title: "Invalid request", detail: "ipAddress must be a valid IP address.",
                statusCode: StatusCodes.Status400BadRequest);
            return false;
        }

        if (request.Port is < 1 or > 65535)
        {
            problem = TypedResults.Problem(
                title: "Invalid request", detail: "port must be between 1 and 65535.",
                statusCode: StatusCodes.Status400BadRequest);
            return false;
        }

        protocol = normalizedProtocol;
        ipAddress = request.IpAddress.Trim();
        return true;
    }

    private static readonly Action<ILogger, Guid, Exception?> LogJobFaultedAction =
        LoggerMessage.Define<Guid>(LogLevel.Error, new EventId(1, "CameraConnectionTestFaulted"),
            "Camera reachability test {TestId} faulted outside its own handling; the sweeper "
            + "will close it out");

    private static void LogJobFaulted(ILogger logger, Guid testId, Exception exception) =>
        LogJobFaultedAction(logger, testId, exception);
}
