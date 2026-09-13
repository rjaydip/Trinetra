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
}
