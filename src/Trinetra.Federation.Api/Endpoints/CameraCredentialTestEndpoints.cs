using Microsoft.AspNetCore.Http.HttpResults;
using Npgsql;
using Trinetra.Federation.Api.Auth;
using Trinetra.Federation.Api.Contracts;
using Trinetra.Federation.Api.OpenApi;
using Trinetra.Federation.Core.Abstractions;
using Trinetra.Federation.Runtime;
using Trinetra.Federation.Storage;
using Trinetra.Federation.Storage.Repositories;
using Trinetra.Federation.Storage.Secrets;

namespace Trinetra.Federation.Api.Endpoints;

/// <summary>
/// A real, authenticated test against an already-registered camera's device.
/// </summary>
/// <remarks>
/// <para>
/// Distinct from <see cref="CameraConnectionTestEndpoints"/> in every way that matters: that one
/// runs <b>before</b> a camera row exists, opens a bare TCP socket, and never touches a
/// credential. This one names an existing camera, resolves whatever credential is stored for it
/// (<c>PUT /cameras/{id}/credential</c>), and runs a protocol-appropriate authenticated handshake
/// (<see cref="CameraCredentialProbe"/>) — HTTP Basic auth for HTTP/HTTPS/ONVIF, an RFC 2326
/// OPTIONS exchange with Basic/Digest for RTSP/RTSPS, and an honest "not verifiable, reachability
/// only" outcome for a protocol this probe cannot speak (RTMP/SRT/OTHER). It never claims a
/// credential was verified when it was not.
/// </para>
/// <para>
/// This is the second half of the redesigned register-camera flow: the operator's "Test
/// connection" click now creates the camera immediately with the fields collected so far
/// (<c>POST /cameras</c>), stores the entered username/password
/// (<c>PUT /cameras/{id}/credential</c>), and then calls this endpoint. The rest of the
/// registration form becomes an edit (<c>PUT /cameras/{id}</c>) of the camera this created.
/// </para>
/// <para>
/// Gated on <c>camera.update</c> — the same permission every other credential-adjacent camera
/// field requires, and this now tests an <b>existing</b> resource's actual stored credential
/// rather than informing a not-yet-made decision, so <c>camera.create</c> (used by the pre-save
/// probe) is not the right gate here.
/// </para>
/// </remarks>
public static class CameraCredentialTestEndpoints
{
    public static void MapCameraCredentialTestEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/cameras/{id:guid}/credential-test")
                       .WithTags(ApiTags.Cameras)
                       .RequireAuthorization();

        group.MapPost("/", StartAsync)
          .RequirePermission("camera.update")
          .WithSummary("Run a real authenticated test against a registered camera")
          .WithDescription(
              "**Actually connects to the device**, unlike `POST /cameras/connection-test` "
              + "(bare TCP, no camera row, no credential). Resolves this camera's stored "
              + "`credentialReference` and runs a protocol-appropriate handshake: HTTP Basic "
              + "auth for `HTTP`/`HTTPS`/`ONVIF` (2xx/3xx is `authenticated`, `401`/`403` is "
              + "`credential_rejected`); an RFC 2326 `OPTIONS` exchange with Basic or Digest for "
              + "`RTSP`/`RTSPS`; and, for `RTMP`/`SRT`/`OTHER` — protocols this probe has no "
              + "authenticated handshake for — a plain reachability check reported as "
              + "`not_verifiable`, never silently upgraded to a pass.\n\n"
              + "`404` if the camera is out of scope or absent, or if it has no `ipAddress`, "
              + "`port`, `protocol` or stored credential yet — this tests what is actually saved "
              + "on the row, not the request body (there is none).\n\n"
              + "**Asynchronous, same idiom as `POST /vms/{id}/test` and "
              + "`POST /cameras/connection-test`:** returns `202` with a `testId` and a "
              + "`statusUrl` to poll.\n\n"
              + "No vendor adapter is involved — Federation.Adapters is Model 3/VMS-only — and "
              + "this probe is read-only and side-effect-free against the device: one request, "
              + "never retried, so a wrong credential is never hammered into looking like a "
              + "brute-force attempt.");

        group.MapGet("/{testId:guid}", ResultAsync)
          .RequirePermission("camera.read")
          .WithSummary("Poll one credential test for its result")
          .WithDescription(
              "The status URL returned by `POST /cameras/{id}/credential-test`. `status` is "
              + "`pending` until the job finishes, then carries the outcome — `result.authOutcome` "
              + "is `authenticated`, `credential_rejected`, `not_verifiable`, `unreachable` or "
              + "`error`. Re-scoped on every read: a result stops being readable by someone who "
              + "has since lost access to the camera. A test abandoned by a host that died is "
              + "closed out by the sweeper rather than left pending forever.");

        group.MapGet("/", ListAsync)
          .RequirePermission("camera.read")
          .WithSummary("List this camera's credential-test history")
          .WithDescription(
              "Every credential test run against this camera, most recent first — who ran it, "
              + "when, and how it ended. Useful for telling a camera that has never accepted its "
              + "credential from one that broke recently.");
    }

    private static async Task<Results<Accepted<CameraCredentialTestAccepted>, NotFound, ProblemHttpResult>>
        StartAsync(
            Guid id,
            CameraRepository cameras,
            CameraCredentialTestRepository tests,
            CameraCredentialProbe prober,
            ICredentialResolver resolver,
            NpgsqlDataSource db,
            ILogger<CameraCredentialProbe> logger,
            HttpContext http,
            CancellationToken ct)
    {
        var caller = CallerContextFactory.From(http);
        caller.Require("camera.update");

        // Scope is enforced by reaching the camera first. Out of scope reads as 404, so an
        // unauthorised caller cannot confirm which camera ids exist.
        var camera = await cameras.GetAsync(id, caller, ct);
        if (camera is null)
        {
            return TypedResults.NotFound();
        }

        if (string.IsNullOrWhiteSpace(camera.IpAddress)
            || camera.Port is null
            || string.IsNullOrWhiteSpace(camera.Protocol)
            || string.IsNullOrWhiteSpace(camera.CredentialReference))
        {
            return TypedResults.Problem(
                title: "Camera is not ready for a credential test",
                detail: "The camera needs ipAddress, port, protocol (PUT/PATCH /cameras/{id}) "
                      + "and a stored credential (PUT /cameras/{id}/credential) before an "
                      + "authenticated test can run.",
                statusCode: StatusCodes.Status409Conflict);
        }

        Credential credential;
        try
        {
            // In-process resolution, not disclosed to the caller -- same reasoning as the
            // connector worker's own resolution path (AuditFailureIsFatal: false): an audit-write
            // hiccup here must not block an operator from testing the camera they just
            // registered, and no secret material leaves this method.
            credential = await resolver.ResolveAsync(
                camera.CredentialReference,
                new CredentialAccessContext(caller.Actor, TargetId: null, AuditFailureIsFatal: false),
                ct);
        }
        catch (CredentialNotProvisionedException)
        {
            return TypedResults.Problem(
                title: "Camera is not ready for a credential test",
                detail: "The camera has a credentialReference recorded but no secret was ever "
                      + "stored for it. Provision it with PUT /cameras/{id}/credential.",
                statusCode: StatusCodes.Status409Conflict);
        }

        // The claim row and its audit record share one transaction: a test that ran against a
        // device with stored credentials is exactly the record an incident review needs.
        await using var work = await UnitOfWork.BeginAsync(db, ct);

        var testId = await tests.ClaimAsync(
            id, camera.Protocol, camera.IpAddress, camera.Port.Value, camera.CredentialReference,
            caller.Actor, work, ct);

        await work.AuditAsync(caller, "update", "camera_credential_test", testId.ToString(),
            before: null,
            after: new { cameraId = id, camera.Protocol, camera.IpAddress, camera.Port },
            camera.OrganizationUnitId, ct);
        await work.CommitAsync(ct);

        // Runs beyond the request, so it deliberately does not take the request's token -- its
        // deadline is CameraCredentialProbe.Timeout and the sweeper closes out anything this
        // instance abandons by dying.
        _ = Task.Run(
            async () =>
            {
                try
                {
                    await prober.ExecuteJobAsync(
                        testId, camera.Protocol, camera.IpAddress, camera.Port.Value, credential,
                        db, Environment.MachineName, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    LogJobFaulted(logger, testId, ex);
                }
            },
            CancellationToken.None);

        var statusUrl = $"/api/v1/cameras/{id}/credential-test/{testId}";
        return TypedResults.Accepted(
            statusUrl, new CameraCredentialTestAccepted(testId, "pending", statusUrl));
    }

    private static async Task<Results<Ok<CameraCredentialTestResult>, NotFound>> ResultAsync(
        Guid id, Guid testId, CameraRepository cameras, CameraCredentialTestRepository tests,
        HttpContext http, CancellationToken ct)
    {
        var caller = CallerContextFactory.From(http);
        caller.Require("camera.read");

        // Re-checked on read: a result must not be readable by someone who has since lost access
        // to the camera, and the job row alone carries no scope of its own.
        if (await cameras.GetAsync(id, caller, ct) is null)
        {
            return TypedResults.NotFound();
        }

        var row = await tests.GetAsync(testId, ct);
        if (row is null || row.CameraId != id)
        {
            return TypedResults.NotFound();
        }

        return TypedResults.Ok(new CameraCredentialTestResult(
            row.Id, row.CameraId, row.Protocol, row.IpAddress, row.Port, row.Status,
            row.RequestedAt, row.CompletedAt, row.FailureReason,
            row.ResultJson is null
                ? null
                : System.Text.Json.JsonSerializer.Deserialize<CameraCredentialTestReport>(
                    row.ResultJson)));
    }

    private static async Task<Results<Ok<IReadOnlyList<CameraCredentialTestSummary>>, NotFound>> ListAsync(
        Guid id, CameraRepository cameras, CameraCredentialTestRepository tests,
        HttpContext http, CancellationToken ct)
    {
        var caller = CallerContextFactory.From(http);
        caller.Require("camera.read");

        if (await cameras.GetAsync(id, caller, ct) is null)
        {
            return TypedResults.NotFound();
        }

        var rows = await tests.ListAsync(id, ct);

        return TypedResults.Ok<IReadOnlyList<CameraCredentialTestSummary>>(
        [
            .. rows.Select(r => new CameraCredentialTestSummary(
                r.Id, r.Status, r.RequestedAt, r.CompletedAt, r.FailureReason)),
        ]);
    }

    private static readonly Action<ILogger, Guid, Exception?> LogJobFaultedAction =
        LoggerMessage.Define<Guid>(LogLevel.Error, new EventId(1, "CameraCredentialTestFaulted"),
            "Camera credential test {TestId} faulted outside its own handling; the sweeper will "
            + "close it out");

    private static void LogJobFaulted(ILogger logger, Guid testId, Exception exception) =>
        LogJobFaultedAction(logger, testId, exception);
}
