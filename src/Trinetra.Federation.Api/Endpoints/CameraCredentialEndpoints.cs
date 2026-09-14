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
/// Provisioning credentials for standalone registry cameras.
/// </summary>
/// <remarks>
/// <para>
/// The same sealing mechanism as <see cref="CredentialEndpoints"/> (application-side AES-256-GCM
/// via <see cref="SecretWriter"/>, keyed on an opaque <c>credential_reference</c> string in the
/// shared <c>federation.secret</c> table) — cameras are simply another row shape under the same
/// reference, so no new secret store or schema was needed.
/// </para>
/// <para>
/// Unlike a VMS target, a registry camera can be created with no <c>credential_reference</c> at
/// all (the field is optional at <c>POST /cameras</c>). Rather than require the operator to type
/// a fake reference into that field to unlock this endpoint — the exact bug this replaces — the
/// first successful <c>PUT .../credential</c> mints one server-side
/// (<c>camera:{id}</c>) and persists it on the camera row. A camera that already carries a
/// reference (set explicitly, or from an earlier call here) keeps it: the reference is the key
/// into <c>federation.secret</c>, so changing it would orphan whatever was already sealed.
/// </para>
/// <para>
/// Gated on <c>camera.update</c>, the same permission that governs every other credential-
/// adjacent field on the camera write path (<c>ipAddress</c>, <c>protocol</c>, …) — there is no
/// separate machine role analogous to <c>VMS_ADMIN</c> for standalone cameras, so reusing
/// <c>credential.write</c> here would strand the operators who actually manage this registry.
/// </para>
/// <para>
/// <c>GET /resolve</c> is the one route here that returns a secret — symmetric to
/// <see cref="CredentialEndpoints"/>'s <c>GET /vms/{id}/credential/resolve</c>, gated on
/// <c>camera.credential.resolve</c> and held only by the <c>STREAMING_GATEWAY</c> machine role
/// (<c>docs/STREAMING-GATEWAY-PLAN.md</c>). It resolves only a camera's own
/// <c>credentialReference</c>; a VMS-managed camera with none of its own falls back to its
/// connector target's credential via the VMS-level route instead, not through this one. Every
/// call is audited to <c>credential_access_log</c> the same way, and the caller's connection to
/// this API must be mTLS (or at minimum TLS), per <c>ARCHITECTURE-MODEL-3.md</c> §9.
/// </para>
/// </remarks>
public static class CameraCredentialEndpoints
{
    public static void MapCameraCredentialEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/cameras/{id:guid}/credential")
                       .WithTags(ApiTags.Credentials)
                       .RequireAuthorization();

        group.MapPut("/", WriteAsync)
          .RequirePermission("camera.update")
          .WithSummary("Store the credential a standalone registry camera authenticates with")
          .WithDescription(
              "Seals a username with a password or a token, encrypting it application-side with "
              + "AES-256-GCM so the database never holds the key. Supply a password or a token; "
              + "supplying neither is a 400.\n\n"
              + "Writing again replaces what is there — this is also the rotation route.\n\n"
              + "A camera with no `credentialReference` yet is given one automatically on first "
              + "write (`camera:{id}`); an existing reference is kept unchanged so a later write "
              + "cannot orphan an already-sealed secret.\n\n"
              + "The response echoes the reference and a timestamp, and nothing that was stored. "
              + "Gated on `camera.update`, the same permission every other credential-adjacent "
              + "camera field (`ipAddress`, `protocol`, …) already requires.");

        // Presence only, so an operator can see whether a camera is provisioned without any path
        // existing that reveals the value.
        group.MapGet("/status", StatusAsync)
          .RequirePermission("camera.read")
          .WithSummary("Check whether this camera has a credential stored")
          .WithDescription(
              "Presence only — the reference and a boolean. `exists: false` (with a `null` "
              + "reference) is the ordinary state for a camera that has never had a credential "
              + "written.");

        group.MapGet("/resolve", ResolveAsync)
          .RequirePermission("camera.credential.resolve")
          .WithSummary("Resolve the credential a caller connects to this camera's stream with")
          .WithDescription(
              "**Returns a secret**, symmetric to `GET /vms/{id}/credential/resolve` "
              + "(`CredentialEndpoints`) but for a standalone registry camera's own "
              + "`credentialReference` rather than a connector target's. Hands back the username, "
              + "password and/or token stored directly on this camera.\n\n"
              + "**Requires mTLS (or at minimum TLS) on the caller's connection to this API** — "
              + "the same transport requirement `ARCHITECTURE-MODEL-3.md` §9 already places on "
              + "every route in this class; it is not optional on a bare-metal deployment for a "
              + "route that discloses camera credentials.\n\n"
              + "Exists for the streaming gateway (`docs/STREAMING-GATEWAY-PLAN.md`), which opens "
              + "an RTSP connection to a camera directly and needs its login. "
              + "`camera.credential.resolve` is carried by exactly one role — "
              + "`STREAMING_GATEWAY`, a machine role with no interactive login and, deliberately, "
              + "no `observation.write` — and the gateway authenticates with `X-Api-Key`, not a "
              + "bearer token.\n\n"
              + "Scoped through the camera in the path, exactly as `/status` and `PUT /` are.\n\n"
              + "Every call is written to `credential_access_log` with the caller and the camera. "
              + "If that audit row cannot be written the secret is withheld and the response is "
              + "503 — there is no path that discloses a credential without a log row.\n\n"
              + "404 if the camera is out of scope, or if it exists but has no "
              + "`credentialReference` of its own (the same state `/status` reports as "
              + "`exists: false`) — this is the ordinary case for a VMS-managed camera that relies "
              + "on its connector target's credential instead; resolve that target's credential "
              + "via `GET /vms/{vmsId}/credential/resolve` in that case, not this route.");
    }

    private static async Task<Results<Ok<CredentialResponse>, NotFound, ProblemHttpResult>> WriteAsync(
        Guid id,
        [FromBody] CredentialRequest request,
        CameraRepository cameras,
        SecretWriter secrets,
        NpgsqlDataSource db, HttpContext http,
        CancellationToken ct)
    {
        var caller = CallerContextFactory.From(http);

        // Belt-and-suspenders behind the .RequirePermission filter.
        caller.Require("camera.update");

        // Scope is enforced by reaching the camera first. Out of scope reads as 404, so an
        // unauthorised caller cannot confirm which camera ids exist.
        var camera = await cameras.GetAsync(id, caller, ct);
        if (camera is null)
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

        // Mint a reference the first time this camera is provisioned. Never overwrites one that
        // is already there — see SetCredentialReferenceIfMissingAsync.
        var reference = ResolveReference(id, camera.CredentialReference);

        await using var work = await UnitOfWork.BeginAsync(db, ct);

        if (camera.CredentialReference is null)
        {
            await cameras.SetCredentialReferenceIfMissingAsync(id, reference, caller, work, ct);
        }

        // Not credential.write — this endpoint is deliberately gated on camera.update (see the
        // class remarks), so it must not let SecretWriter silently re-require the permission it
        // was built to avoid.
        await secrets.WriteAsync(
            reference, request.Username, request.Password, request.Token,
            request.Description, caller, work, ct, requiredPermission: "camera.update");

        await work.CommitAsync(ct);

        // Echoes the reference and a timestamp. Never any part of what was stored.
        return TypedResults.Ok(new CredentialResponse(reference, DateTimeOffset.UtcNow));
    }

    /// <summary>
    /// A camera's existing reference is always kept; a camera with none yet is given a
    /// deterministic one derived from its id. Pulled out as its own method purely so the rule can
    /// be unit tested without a database.
    /// </summary>
    private static string ResolveReference(Guid id, string? existing) =>
        existing ?? $"camera:{id}";

    private static async Task<Results<Ok<CredentialExistsResponse>, NotFound>> StatusAsync(
        Guid id, CameraRepository cameras, SecretWriter secrets,
        HttpContext http, CancellationToken ct)
    {
        var caller = CallerContextFactory.From(http);
        caller.Require("camera.read");

        var camera = await cameras.GetAsync(id, caller, ct);
        if (camera is null)
        {
            return TypedResults.NotFound();
        }

        if (camera.CredentialReference is null)
        {
            // Never provisioned — no reference has been minted yet.
            return TypedResults.Ok(new CredentialExistsResponse(string.Empty, false));
        }

        return TypedResults.Ok(new CredentialExistsResponse(
            camera.CredentialReference, await secrets.ExistsAsync(camera.CredentialReference, ct)));
    }

    private static async Task<Results<Ok<ResolvedCredentialResponse>, NotFound, ProblemHttpResult>> ResolveAsync(
        Guid id,
        CameraRepository cameras,
        ICredentialResolver resolver,
        HttpContext http,
        CancellationToken ct)
    {
        var caller = CallerContextFactory.From(http);

        // Belt-and-suspenders behind the .RequirePermission filter, matching ResolveAsync in
        // CredentialEndpoints.
        caller.Require("camera.credential.resolve");

        // Scope is enforced by reaching the camera first. Out of scope reads as 404, so an
        // unauthorised caller cannot confirm which camera ids exist.
        var camera = await cameras.GetAsync(id, caller, ct);
        if (camera is null)
        {
            return TypedResults.NotFound();
        }

        // No reference of its own: never provisioned, or a VMS-managed camera that relies on its
        // connector target's credential instead. Either way, this route has nothing to resolve —
        // 404, the same state /status reports as exists:false.
        if (camera.CredentialReference is null)
        {
            return TypedResults.NotFound();
        }

        try
        {
            // AuditFailureIsFatal: this path returns the plaintext, so a resolution that cannot
            // be audited must fail rather than disclose an unlogged secret.
            var credential = await resolver.ResolveAsync(
                camera.CredentialReference,
                new CredentialAccessContext(caller.Actor, id, AuditFailureIsFatal: true),
                ct);

            return TypedResults.Ok(new ResolvedCredentialResponse(
                camera.CredentialReference,
                credential.Username, credential.Password, credential.Token));
        }
        catch (CredentialNotProvisionedException)
        {
            // Reference set but nothing ever sealed under it — the state /status reports as
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
}
