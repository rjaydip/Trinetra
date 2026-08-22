using Trinetra.Federation.Storage;
using Npgsql;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Trinetra.Federation.Api.Auth;
using Trinetra.Federation.Api.Contracts;
using Trinetra.Federation.Storage.Repositories;

namespace Trinetra.Federation.Api.Endpoints;

/// <summary>
/// Setting device credentials. <b>Write-only, and scoped to a target by construction.</b>
/// </summary>
/// <remarks>
/// <para>
/// There is no GET returning secret material at any path, and no method on
/// <see cref="SecretWriter"/> that returns one. Reading is reachable only from a connector worker
/// resolving a credential in order to connect. A contributor cannot accidentally expose one by
/// adding a field to a response, because no response type has ever carried it.
/// </para>
/// <para>
/// <b>The route is <c>/vms/{id}/credential</c>, not <c>/credentials/{reference}</c>.</b> The
/// earlier flat route took a caller-supplied reference in a global namespace with no scope check
/// at all, so a district-scoped administrator could overwrite the credential of every target in
/// the estate — 80,000 cameras dark at the next reconnect, and every integration account locked
/// out. Deriving the reference from a target the caller can already reach removes the class of
/// bug rather than adding a check that a later route might forget.
/// </para>
/// </remarks>
public static class CredentialEndpoints
{
    public static void MapCredentialEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/vms/{id:guid}/credential")
                       .WithTags("Credentials")
                       .RequireAuthorization();

        group.MapPut("/", async Task<Results<Ok<CredentialResponse>, NotFound, ProblemHttpResult>> (
            Guid id,
            [FromBody] CredentialRequest request,
            ConnectorTargetRepository targets,
            SecretWriter secrets,
            NpgsqlDataSource db, HttpContext http,
            CancellationToken ct) =>
        {
            var caller = CallerContextFactory.From(http);

            // Separately gated from vms.update: writing the password a connector authenticates
            // with is the highest-privilege action in the system, and most clients that
            // legitimately edit a target have no business performing it.
            caller.Require("credential.write");

            // Scope is enforced by reaching the target first. Out of scope reads as 404, so an
            // unauthorised caller cannot confirm which target ids exist.
            var target = await targets.GetAsync(id, caller, ct);
            if (target is null)
            {
                return TypedResults.NotFound();
            }

            if (string.IsNullOrEmpty(request.Password) && string.IsNullOrEmpty(request.Token))
            {
                return TypedResults.Problem(
                    title: "Nothing to store",
                    detail: "Supply a password or a token.",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            await using var work = await UnitOfWork.BeginAsync(db, ct);

            // The reference comes from the target row, never from the request body.
            await secrets.WriteAsync(
                target.CredentialReference, request.Username, request.Password, request.Token,
                request.Description, caller, work, ct);

            await work.CommitAsync(ct);

            // Echoes the reference and a timestamp. Never any part of what was stored.
            return TypedResults.Ok(
                new CredentialResponse(target.CredentialReference, DateTimeOffset.UtcNow));
        }).RequirePermission("credential.write");

        // Presence only, so an operator can see whether a target is provisioned without any path
        // existing that reveals the value.
        group.MapGet("/status", async Task<Results<Ok<CredentialExistsResponse>, NotFound>> (
            Guid id, ConnectorTargetRepository targets, SecretWriter secrets,
            HttpContext http, CancellationToken ct) =>
        {
            var caller = CallerContextFactory.From(http);
            caller.Require("vms.read");

            var target = await targets.GetAsync(id, caller, ct);
            if (target is null)
            {
                return TypedResults.NotFound();
            }

            return TypedResults.Ok(new CredentialExistsResponse(
                target.CredentialReference, await secrets.ExistsAsync(target.CredentialReference, ct)));
        }).RequirePermission("vms.read");
    }
}
