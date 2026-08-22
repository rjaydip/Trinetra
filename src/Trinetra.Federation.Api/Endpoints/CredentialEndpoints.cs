using Trinetra.Federation.Storage;
using Npgsql;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Trinetra.Federation.Api.Auth;
using Trinetra.Federation.Api.Contracts;
using Trinetra.Federation.Api.OpenApi;
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
                       .WithTags(ApiTags.Credentials)
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
        }).RequirePermission("credential.write")
          .WithSummary("Store the credential a connector authenticates to this device with")
          .WithDescription(
              "**Step 2 of onboarding.** Seals a username with a password or a token under the "
              + "reference already recorded on the target, encrypting it application-side with "
              + "AES-256-GCM so the database never holds the key. Supply a password or a token; "
              + "supplying neither is a 400.\n\n"
              + "Writing again replaces what is there — this is also the rotation route. Workers "
              + "pick the new value up at their next connect.\n\n"
              + "The reference comes from the target row, **never from the request body**: a "
              + "caller-supplied reference in a flat namespace would let a district-scoped "
              + "administrator overwrite the credential of every target in the estate.\n\n"
              + "The response echoes the reference and a timestamp, and nothing that was stored. "
              + "Gated separately from `vms.update` because writing the password a connector "
              + "authenticates with is the highest-privilege action in the system.");

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
        }).RequirePermission("vms.read")
          .WithSummary("Check whether this target has a credential stored")
          .WithDescription(
              "Presence only — the reference and a boolean. This exists so an operator can tell "
              + "an unprovisioned target from a misconfigured one without any route existing that "
              + "reveals a secret.\n\n"
              + "`exists: false` on a target that fails its connection test is the ordinary cause: "
              + "it was registered but never given a credential.");
    }
}
