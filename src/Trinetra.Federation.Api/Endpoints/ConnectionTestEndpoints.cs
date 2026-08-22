using Microsoft.AspNetCore.Http.HttpResults;
using Npgsql;
using Trinetra.Federation.Api.Auth;
using Trinetra.Federation.Api.Contracts;
using Trinetra.Federation.Runtime;
using Trinetra.Federation.Storage;
using Trinetra.Federation.Storage.Repositories;

namespace Trinetra.Federation.Api.Endpoints;

/// <summary>
/// Asynchronous connection tests against real devices.
/// </summary>
/// <remarks>
/// A test can take up to a minute, which is longer than most reverse proxies will hold a
/// connection. The caller starts a job and polls, so a slow or black-holed device produces a
/// clear result rather than a dropped request.
/// </remarks>
public static class ConnectionTestEndpoints
{
    public static void MapConnectionTestEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/vms/{id:guid}/test")
                       .WithTags("VMS")
                       .RequireAuthorization();

        group.MapPost("/", async Task<Results<Accepted<ConnectionTestAccepted>, NotFound, ProblemHttpResult>> (
            Guid id,
            ConnectorTargetRepository targets, ConnectionTestRepository tests,
            ConnectionTester tester,
                        NpgsqlDataSource db,
            ILogger<ConnectionTestRunner> logger,
            HttpContext http,
            CancellationToken ct) =>
        {
            var caller = CallerContextFactory.From(http);

            // Not vms.read. This authenticates to a live device using stored credentials and
            // consumes one of its limited session slots — that is an integration action, not a
            // read of platform data, and gating it on read let anyone with view access start
            // unlimited long-running device calls.
            caller.Require("integration.manage");

            var target = await targets.GetAsync(id, caller, ct);
            if (target is null)
            {
                return TypedResults.NotFound();
            }

            Guid testId;

            // The claim row and its audit record share one transaction: a test that is booked
            // against a device but leaves no trace of who booked it is exactly the record an
            // incident review needs.
            await using var work = await UnitOfWork.BeginAsync(db, ct);

            {
                var existing = await tests.InFlightAsync(id, work, ct);

                if (existing is { } inFlight)
                {
                    return TypedResults.Problem(
                        title: "A test is already running",
                        detail: $"Test {inFlight} is still in progress for this target. Poll it, "
                              + "or wait for it to finish.",
                        statusCode: StatusCodes.Status409Conflict,
                        extensions: new Dictionary<string, object?>
                        {
                            ["testId"] = inFlight,
                            ["statusUrl"] = $"/api/v1/vms/{id}/test/{inFlight}",
                        });
                }

                testId = await tests.ClaimAsync(id, caller.Actor, work, ct);
            }

            // Contacting a device with stored credentials is a privileged action, so it is
            // recorded like any other. Every other mutation writes an audit row; this one used
            // to write none.
            await work.AuditAsync(caller, "update", "connection_test", testId.ToString(),
                before: null, after: new { targetId = id, target.Code },
                target.OrganizationUnitId, ct);
            await work.CommitAsync(ct);

            // Runs beyond the request, so it deliberately does not take the request's token —
            // its deadline is ConnectionTester.MaxDuration and the sweeper closes out anything
            // this instance abandons by dying. Exceptions are observed here rather than
            // discarded: an unobserved fire-and-forget failure leaves the job row untouched and
            // the caller polling forever.
            _ = Task.Run(
                async () =>
                {
                    try
                    {
                        await tester.ExecuteJobAsync(
                            testId, target, db, Environment.MachineName, CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        LogJobFaulted(logger, testId, ex);
                    }
                },
                CancellationToken.None);

            var statusUrl = $"/api/v1/vms/{id}/test/{testId}";
            return TypedResults.Accepted(
                statusUrl, new ConnectionTestAccepted(testId, "pending", statusUrl));
        }).RequirePermission("integration.manage");

        group.MapGet("/{testId:guid}", async Task<Results<Ok<ConnectionTestResult>, NotFound>> (
            Guid id, Guid testId, ConnectorTargetRepository targets, ConnectionTestRepository tests, NpgsqlDataSource db,
            HttpContext http, CancellationToken ct) =>
        {
            var caller = CallerContextFactory.From(http);
            caller.Require("vms.read");

            // Re-checked on read: a result must not be readable by someone who has since lost
            // access to the target, and the job row alone carries no scope of its own.
            if (await targets.GetAsync(id, caller, ct) is null)
            {
                return TypedResults.NotFound();
            }

            var row = await tests.GetAsync(id, testId, ct);

            if (row is null)
            {
                return TypedResults.NotFound();
            }

            return TypedResults.Ok(new ConnectionTestResult(
                row.Id, row.TargetId, row.Status, row.RequestedAt, row.CompletedAt,
                row.FailureReason,
                row.ResultJson is null
                    ? null
                    : System.Text.Json.JsonSerializer.Deserialize<object>(row.ResultJson)));
        }).RequirePermission("vms.read");

        group.MapGet("/", async Task<Results<Ok<IReadOnlyList<ConnectionTestSummary>>, NotFound>> (
            Guid id, ConnectorTargetRepository targets, ConnectionTestRepository tests, NpgsqlDataSource db,
            HttpContext http, CancellationToken ct) =>
        {
            var caller = CallerContextFactory.From(http);
            caller.Require("vms.read");

            if (await targets.GetAsync(id, caller, ct) is null)
            {
                return TypedResults.NotFound();
            }

            var rows = await tests.ListAsync(id, ct);

            return TypedResults.Ok<IReadOnlyList<ConnectionTestSummary>>(
            [
                .. rows.Select(r => new ConnectionTestSummary(
                    r.Id, r.Status, r.RequestedBy, r.RequestedAt, r.CompletedAt, r.FailureReason)),
            ]);
        }).RequirePermission("vms.read");
    }

    /// <summary>Log category for the detached job, so its failures are attributable.</summary>
    internal sealed class ConnectionTestRunner;

    private static readonly Action<ILogger, Guid, Exception?> LogJobFaultedAction =
        LoggerMessage.Define<Guid>(LogLevel.Error, new EventId(1, "ConnectionTestFaulted"),
            "Connection test {TestId} faulted outside its own handling; the sweeper will close it out");

    private static void LogJobFaulted(ILogger logger, Guid testId, Exception exception) =>
        LogJobFaultedAction(logger, testId, exception);

}
