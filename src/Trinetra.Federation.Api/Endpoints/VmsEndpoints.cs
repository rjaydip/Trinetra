using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using Trinetra.Federation.Api.Auth;
using Trinetra.Federation.Api.Contracts;
using Trinetra.Federation.Core.Model;
using Trinetra.Federation.Storage;
using Trinetra.Federation.Storage.Repositories;

namespace Trinetra.Federation.Api.Endpoints;

/// <summary>Connector targets, their health, capabilities and discovered cameras.</summary>
public static class VmsEndpoints
{
    public static void MapVmsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/vms").WithTags("VMS").RequireAuthorization();

        group.MapGet("/", async Task<Ok<IReadOnlyList<VmsResponse>>> (
            ConnectorTargetRepository repo, HttpContext http, CancellationToken ct) =>
        {
            var targets = await repo.ListAsync(CallerContextFactory.From(http), ct);
            return TypedResults.Ok<IReadOnlyList<VmsResponse>>([.. targets.Select(ToResponse)]);
        }).RequirePermission("vms.read");

        group.MapGet("/{id:guid}", async Task<Results<Ok<VmsResponse>, NotFound>> (
            Guid id, ConnectorTargetRepository repo, HttpContext http, CancellationToken ct) =>
        {
            var target = await repo.GetAsync(id, CallerContextFactory.From(http), ct);

            // Out-of-scope targets return 404 rather than 403: telling an unauthorised caller
            // that an id exists is itself a disclosure.
            return target is null
                ? TypedResults.NotFound()
                : TypedResults.Ok(ToResponse(target));
        }).RequirePermission("vms.read");

        group.MapPost("/", async Task<Results<Created<CreatedResponse>, ProblemHttpResult>> (
            [FromBody] ConnectorTargetRequest request, ConnectorTargetRepository repo,
            NpgsqlDataSource db, HttpContext http, CancellationToken ct) =>
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
        }).RequirePermission("vms.create");

        group.MapPut("/{id:guid}", async Task<Results<NoContent, NotFound, ProblemHttpResult>> (
            Guid id, [FromBody] ConnectorTargetRequest request, ConnectorTargetRepository repo,
            NpgsqlDataSource db, HttpContext http, CancellationToken ct) =>
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
        }).RequirePermission("vms.update");

        group.MapPost("/{id:guid}/state", async Task<Results<NoContent, NotFound, ProblemHttpResult>> (
            Guid id, [FromBody] TargetStateRequest request, ConnectorTargetRepository repo,
            NpgsqlDataSource db, HttpContext http, CancellationToken ct) =>
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
        }).RequirePermission("vms.update");

        group.MapDelete("/{id:guid}", async Task<Results<NoContent, NotFound>> (
            Guid id, ConnectorTargetRepository repo, NpgsqlDataSource db,
            HttpContext http, CancellationToken ct) =>
        {
            var caller = CallerContextFactory.From(http);
            var before = await repo.GetAsync(id, caller, ct);

            if (before is null)
            {
                return TypedResults.NotFound();
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
        }).RequirePermission("vms.update");

        // ---- Monitoring ----------------------------------------------------

        group.MapGet("/{id:guid}/health",
            async Task<Results<Ok<IReadOnlyList<ConnectorHealthResponse>>, NotFound>> (
            Guid id, int? limit, int? days, ConnectorTargetRepository repo, FederationQueryRepository queries,
            HttpContext http, CancellationToken ct) =>
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
        }).RequirePermission("vms.read");

        group.MapGet("/{id:guid}/capabilities",
            async Task<Results<Ok<CapabilityResponse>, ProblemHttpResult>> (
            Guid id, ConnectorTargetRepository repo, FederationQueryRepository queries,
            HttpContext http, CancellationToken ct) =>
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
        }).RequirePermission("vms.read");

        group.MapGet("/{id:guid}/cameras",
            async Task<Results<Ok<IReadOnlyList<FederatedCameraResponse>>, NotFound>> (
            Guid id, ConnectorTargetRepository repo, FederationQueryRepository queries,
            HttpContext http, CancellationToken ct) =>
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
                    r.StreamReferences ?? [])),
            ]);
        }).RequirePermission("vms.read");

        // ---- Fleet overview -------------------------------------------------

        app.MapGet("/api/v1/overview", async Task<Ok<OverviewResponse>> (
            FederationQueryRepository queries, HttpContext http, CancellationToken ct) =>
        {
            var caller = CallerContextFactory.From(http);
            caller.Require("vms.read");

            var row = await queries.OverviewAsync(caller, ct);

            return TypedResults.Ok(new OverviewResponse(
                row.Targets, row.ActiveTargets, row.QuarantinedTargets,
                row.Cameras, row.UnreachableCameras));
        }).RequireAuthorization().WithTags("VMS").RequirePermission("vms.read");
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
    };
}
