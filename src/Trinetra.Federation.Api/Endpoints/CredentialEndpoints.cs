using Trinetra.Federation.Storage;
using Npgsql;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Trinetra.Federation.Api.Auth;
using Trinetra.Federation.Api.Contracts;
using Trinetra.Federation.Api.OpenApi;
using Trinetra.Federation.Core.Abstractions;
using Trinetra.Federation.Storage.Repositories;

namespace Trinetra.Federation.Api.Endpoints;

/// <summary>
/// Provisioning device credentials, and — for one machine caller — resolving them.
/// <b>Scoped to a target by construction.</b>
/// </summary>
/// <remarks>
/// <para>
/// The write side (<c>PUT /</c>) and <c>GET /status</c> never return secret material, and no
/// method on <see cref="SecretWriter"/> returns one. The <b>single</b> route that hands a secret
/// back is <c>GET /resolve</c>: it requires <c>credential.resolve</c>, a permission held only by
/// the <c>DETECTION_WORKER</c> machine role, and <see cref="ICredentialResolver"/> writes
/// <c>credential_access_log</c> for every call — a failure to audit fails the request rather than
/// returning an unlogged secret. It exists for the AI worker (<c>ai-worker/</c>), which connects
/// to camera streams directly. Only <see cref="ResolvedCredentialResponse"/> carries a credential
/// value; no other response type on this API does.
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

        group.MapPut("/", WriteAsync)
          .RequirePermission("credential.write")
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
        group.MapGet("/status", StatusAsync)
          .RequirePermission("vms.read")
          .WithSummary("Check whether this target has a credential stored")
          .WithDescription(
              "Presence only — the reference and a boolean. This exists so an operator can tell "
              + "an unprovisioned target from a misconfigured one without revealing a secret.\n\n"
              + "`exists: false` on a target that fails its connection test is the ordinary cause: "
              + "it was registered but never given a credential.");

        group.MapGet("/resolve", ResolveAsync)
          .RequirePermission("credential.resolve")
          .WithSummary("Resolve the credential a worker connects to this target's streams with")
          .WithDescription(
              "**The one route on this API that returns a secret.** Hands back the username, "
              + "password and/or token stored for this target so a caller can connect to it "
              + "directly.\n\n"
              + "Exists for the standalone AI worker (`ai-worker/`), which reads camera "
              + "RTSP streams itself and needs their login. `credential.resolve` is carried by "
              + "exactly one role — `DETECTION_WORKER`, a machine role with no interactive login — "
              + "and the worker authenticates with `X-Api-Key`, not a bearer token.\n\n"
              + "Scoped through the target in the path, exactly as `/status` and `PUT /` are: a "
              + "caller resolves a credential only for a target their organization and geography "
              + "grants already reach. The `DETECTION_WORKER` access group must therefore be given "
              + "a scope that covers the targets, or this returns 404.\n\n"
              + "Every call is written to `credential_access_log` with the caller and the target. "
              + "If that audit row cannot be written the secret is withheld and the response is "
              + "503 — there is no path that discloses a credential without a log row.\n\n"
              + "404 if the target is out of scope, or if it exists but no credential was ever "
              + "provisioned (the same state `/status` reports as `exists: false`). Vendor extras "
              + "(Milestone OAuth client, Genetec application id) are not returned here.");
    }

    private static async Task<Results<Ok<CredentialResponse>, NotFound, ProblemHttpResult>> WriteAsync(
        Guid id,
        [FromBody] CredentialRequest request,
        ConnectorTargetRepository targets,
        SecretWriter secrets,
        NpgsqlDataSource db, HttpContext http,
        CancellationToken ct)
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
    }

    private static async Task<Results<Ok<ResolvedCredentialResponse>, NotFound, ProblemHttpResult>> ResolveAsync(
        Guid id,
        ConnectorTargetRepository targets,
        ICredentialResolver resolver,
        HttpContext http,
        CancellationToken ct)
    {
        var caller = CallerContextFactory.From(http);

        // Belt-and-suspenders behind the .RequirePermission filter, matching WriteAsync.
        caller.Require("credential.resolve");

        // Scope is enforced by reaching the target first: out of scope reads as 404, so a caller
        // cannot confirm which target ids exist.
        var target = await targets.GetAsync(id, caller, ct);
        if (target is null)
        {
            return TypedResults.NotFound();
        }

        try
        {
            // AuditFailureIsFatal: this path returns the plaintext, so a resolution that cannot
            // be audited must fail rather than disclose an unlogged secret.
            var credential = await resolver.ResolveAsync(
                target.CredentialReference,
                new CredentialAccessContext(caller.Actor, id, AuditFailureIsFatal: true),
                ct);

            return TypedResults.Ok(new ResolvedCredentialResponse(
                target.CredentialReference,
                credential.Username, credential.Password, credential.Token));
        }
        catch (CredentialNotProvisionedException)
        {
            // Target exists but was never given a credential — the state `/status` reports as
            // exists:false. A 404, not the 500 the raw exception would otherwise produce.
            return TypedResults.NotFound();
        }
        catch (CredentialAuditException)
        {
            // Resolved, but the access-audit row could not be written, so the secret was
            // withheld. Retryable once the audit store is reachable.
            return TypedResults.Problem(
                title: "Credential audit unavailable",
                detail: "The credential could not be resolved because its access could not be "
                      + "recorded. Retry shortly.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static async Task<Results<Ok<CredentialExistsResponse>, NotFound>> StatusAsync(
        Guid id, ConnectorTargetRepository targets, SecretWriter secrets,
        HttpContext http, CancellationToken ct)
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
    }
}
