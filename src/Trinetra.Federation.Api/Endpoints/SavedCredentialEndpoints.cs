using Microsoft.AspNetCore.Http.HttpResults;
using Npgsql;
using Trinetra.Federation.Api.Auth;
using Trinetra.Federation.Api.Contracts;
using Trinetra.Federation.Api.OpenApi;
using Trinetra.Federation.Storage;
using Trinetra.Federation.Storage.Repositories;

namespace Trinetra.Federation.Api.Endpoints;

/// <summary>
/// The saved-credential library (v1.23/v1.24): reusable, named device credentials an operator can
/// point several cameras at instead of retyping a username/password for every one of them.
/// </summary>
/// <remarks>
/// <para>
/// Metadata only ever travels through this API — name, description, and which reference. The
/// secret itself is sealed the same way every other credential in this schema is, through
/// <see cref="SecretWriter"/> into the shared <c>secret</c> table, under a reference of the form
/// <c>saved-credential:{id}</c>. A camera adopts a library entry by having its own
/// <c>credentialReference</c> field (already a plain part of <c>POST/PUT /cameras</c>) set to that
/// same reference — a shared pointer, not a copy: <c>PUT /{id}</c> reseals that SAME reference
/// (v1.24), so rotating the library entry rotates it for every camera pointed at it, with no
/// per-camera write. <c>DELETE /{id}</c> removes only the library row — the sealed secret and any
/// camera still pointed at it are left alone, so deleting an entry from the picker never silently
/// breaks a camera already using it.
/// </para>
/// <para>
/// Gated the same way <see cref="CameraCredentialEndpoints"/> already is, and for the same reason
/// (see that class's remarks): <c>camera.read</c> to list/get, <c>camera.update</c> to
/// create/rotate/delete, not <c>credential.write</c> — the audience is the same operators who
/// already register cameras, and requiring the "highest privilege in the system" permission here
/// would strand them.
/// </para>
/// </remarks>
public static class SavedCredentialEndpoints
{
    public static void MapSavedCredentialEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/credential-library")
                       .WithTags(ApiTags.Credentials)
                       .RequireAuthorization();

        group.MapGet("/", ListAsync)
          .RequirePermission("camera.read")
          .WithSummary("List the saved-credential library")
          .WithDescription(
              "Metadata only — name, description, id, which reference, and a true `usageCount` "
              + "(every camera pointed at it, regardless of the caller's own scope) — never "
              + "secret material and no `usedBy` list (see `GET /{id}` for that). Meant to "
              + "populate a picker when registering or editing a camera.");

        group.MapGet("/{id:guid}", GetAsync)
          .RequirePermission("camera.read")
          .WithSummary("One saved-credential entry, with which cameras use it")
          .WithDescription(
              "Same metadata as the list route, plus `usedBy` — the cameras currently pointed "
              + "at this entry's reference, scoped to what the caller can see (the same "
              + "organization/geography reach `GET /cameras/{id}` enforces) — so an operator "
              + "can see the impact of rotating or deleting it before doing so. `usageCount` "
              + "stays a true, unscoped total even when `usedBy` is narrower. 404 if the entry "
              + "does not exist.");

        group.MapPost("/", CreateAsync)
          .RequirePermission("camera.update")
          .WithSummary("Save a reusable named credential")
          .WithDescription(
              "Seals a username with a password or a token under a new reference, exactly as "
              + "`PUT /cameras/{id}/credential` does, and records it under `name` so it can be "
              + "picked again later. Supply a password or a token; supplying neither is a 400. "
              + "`name` must be unique — 409 if it is already taken.");

        group.MapPut("/{id:guid}", UpdateAsync)
          .RequirePermission("camera.update")
          .WithSummary("Rotate or rename a saved credential")
          .WithDescription(
              "Supplying a password or a token reseals the SAME reference every camera pointed "
              + "at this entry already carries — this is the rotation route, and it takes "
              + "effect for every one of those cameras with no per-camera write. Supplying "
              + "neither only updates `name`/`description`. `name`, if supplied, must still be "
              + "unique — 409 if another entry already has it. 404 if the entry does not exist.");

        group.MapDelete("/{id:guid}", DeleteAsync)
          .RequirePermission("camera.update")
          .WithSummary("Remove a saved-credential library entry")
          .WithDescription(
              "Removes only the library row — the sealed secret it pointed at, and any camera "
              + "still carrying that `credentialReference`, are left exactly as they were. This "
              + "only takes the entry out of the picker; it does not revoke or break anything "
              + "already using it. 404 if the entry does not exist.");
    }

    private static SavedCredentialResponse ToResponse(SavedCredentialRow r, IReadOnlyList<SavedCredentialUsedByRow>? usedBy = null) =>
        new(r.Id, r.Name, r.Description, r.CredentialReference, r.CreatedAt, r.UpdatedAt, r.UsageCount,
            usedBy?.Select(u => new SavedCredentialUsedByResponse(u.Id, u.CameraCode, u.Name)).ToList());

    private static async Task<Ok<IReadOnlyList<SavedCredentialResponse>>> ListAsync(
        SavedCredentialRepository repo, HttpContext http, CancellationToken ct)
    {
        var caller = CallerContextFactory.From(http);
        caller.Require("camera.read");

        var rows = await repo.ListAsync(ct);
        return TypedResults.Ok<IReadOnlyList<SavedCredentialResponse>>([.. rows.Select(r => ToResponse(r))]);
    }

    private static async Task<Results<Ok<SavedCredentialResponse>, NotFound>> GetAsync(
        Guid id, SavedCredentialRepository repo, HttpContext http, CancellationToken ct)
    {
        var caller = CallerContextFactory.From(http);
        caller.Require("camera.read");

        var row = await repo.GetAsync(id, ct);
        if (row is null)
        {
            return TypedResults.NotFound();
        }

        var usedBy = await repo.GetUsedByAsync(row.CredentialReference, caller, ct);
        return TypedResults.Ok(ToResponse(row, usedBy));
    }

    private static async Task<Results<Created<SavedCredentialResponse>, ProblemHttpResult>> CreateAsync(
        SavedCredentialRequest request,
        SavedCredentialRepository repo,
        SecretWriter secrets,
        NpgsqlDataSource db, HttpContext http,
        CancellationToken ct)
    {
        var caller = CallerContextFactory.From(http);

        // Belt-and-suspenders behind the .RequirePermission filter.
        caller.Require("camera.update");

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return TypedResults.Problem(
                title: "Name is required",
                detail: "A saved credential needs a name to be picked by later.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (string.IsNullOrEmpty(request.Password) && string.IsNullOrEmpty(request.Token))
        {
            return TypedResults.Problem(
                title: "Nothing to store",
                detail: "Supply a password or a token.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var id = Guid.NewGuid();
        var reference = $"saved-credential:{id}";

        await using var work = await UnitOfWork.BeginAsync(db, ct);

        // Seal first: the table's FK on credential_reference requires the secret row to already
        // exist. Not credential.write — this endpoint is deliberately gated on camera.update (see
        // the class remarks), so it must not let SecretWriter silently re-require the permission
        // it was built to avoid.
        await secrets.WriteAsync(
            reference, request.Username, request.Password, request.Token,
            request.Description, caller, work, ct, requiredPermission: "camera.update");

        SavedCredentialRow row;
        try
        {
            row = await repo.CreateAsync(id, request.Name.Trim(), request.Description, reference, caller.Actor, work, ct);
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return TypedResults.Problem(
                title: "Name already in use",
                detail: $"A saved credential named '{request.Name}' already exists.",
                statusCode: StatusCodes.Status409Conflict);
        }

        await work.AuditAsync(caller, "create", "saved_credential", id.ToString(),
            before: null, after: new { row.Name, row.Description }, organizationUnitId: null, ct);
        await work.CommitAsync(ct);

        return TypedResults.Created($"/api/v1/credential-library/{id}", ToResponse(row));
    }

    private static async Task<Results<Ok<SavedCredentialResponse>, NotFound, ProblemHttpResult>> UpdateAsync(
        Guid id, SavedCredentialUpdateRequest request,
        SavedCredentialRepository repo,
        SecretWriter secrets,
        NpgsqlDataSource db, HttpContext http,
        CancellationToken ct)
    {
        var caller = CallerContextFactory.From(http);
        caller.Require("camera.update");

        if (request.Name is not null && string.IsNullOrWhiteSpace(request.Name))
        {
            return TypedResults.Problem(
                title: "Name cannot be blank",
                detail: "A saved credential needs a name to be picked by later.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var existing = await repo.GetAsync(id, ct);
        if (existing is null)
        {
            return TypedResults.NotFound();
        }

        await using var work = await UnitOfWork.BeginAsync(db, ct);

        var rotating = !string.IsNullOrEmpty(request.Password) || !string.IsNullOrEmpty(request.Token);
        if (rotating)
        {
            // Reseals the SAME reference — this is what makes it a rotation rather than a new
            // credential. Every camera already pointed at this reference picks up the change
            // with no write of its own. Not credential.write, for the same reason CreateAsync
            // isn't (see the class remarks).
            await secrets.WriteAsync(
                existing.CredentialReference, request.Username, request.Password, request.Token,
                request.Description ?? existing.Description, caller, work, ct, requiredPermission: "camera.update");
        }

        SavedCredentialRow row;
        try
        {
            var updated = await repo.UpdateAsync(
                id, request.Name?.Trim(), request.Description, caller.Actor, work, ct);
            if (updated is null)
            {
                // Deleted concurrently between the GetAsync above and this write.
                return TypedResults.NotFound();
            }
            row = updated;
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return TypedResults.Problem(
                title: "Name already in use",
                detail: $"A saved credential named '{request.Name}' already exists.",
                statusCode: StatusCodes.Status409Conflict);
        }

        await work.AuditAsync(caller, rotating ? "rotate" : "update", "saved_credential", id.ToString(),
            before: new { existing.Name, existing.Description },
            after: new { row.Name, row.Description, rotated = rotating }, organizationUnitId: null, ct);
        await work.CommitAsync(ct);

        return TypedResults.Ok(ToResponse(row));
    }

    private static async Task<Results<NoContent, NotFound>> DeleteAsync(
        Guid id, SavedCredentialRepository repo, NpgsqlDataSource db, HttpContext http, CancellationToken ct)
    {
        var caller = CallerContextFactory.From(http);
        caller.Require("camera.update");

        var existing = await repo.GetAsync(id, ct);
        if (existing is null)
        {
            return TypedResults.NotFound();
        }

        await using var work = await UnitOfWork.BeginAsync(db, ct);

        var deleted = await repo.DeleteAsync(id, work, ct);
        if (!deleted)
        {
            return TypedResults.NotFound();
        }

        await work.AuditAsync(caller, "delete", "saved_credential", id.ToString(),
            before: new { existing.Name, existing.Description }, after: null, organizationUnitId: null, ct);
        await work.CommitAsync(ct);

        return TypedResults.NoContent();
    }
}
