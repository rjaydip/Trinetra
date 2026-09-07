using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using Trinetra.Federation.Api.Auth;
using Trinetra.Federation.Api.Contracts;
using Trinetra.Federation.Api.OpenApi;
using Trinetra.Federation.Storage;
using Trinetra.Federation.Storage.Repositories;

namespace Trinetra.Federation.Api.Endpoints;

/// <summary>
/// Provisions and manages API keys for machine-to-machine callers — closes the gap that left the
/// AI worker with no way to obtain a real <c>X-Api-Key</c> value.
/// </summary>
public static class ApiKeyEndpoints
{
    public static void MapApiKeyEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/api-keys")
            .WithTags(ApiTags.ApiKeys).RequireAuthorization();

        group.MapGet("/", ListAsync)
          .RequirePermission("apikey.read")
          .WithSummary("List provisioned API keys")
          .WithDescription(
              "API keys you may see, newest first: each with its access group, expiry, last-used "
              + "time and whether it has been revoked. **Never returns key material** — the raw "
              + "value exists only in the creation response, and only its SHA-256 is stored.\n\n"
              + "A key is listed only if you could have provisioned it: its access group is "
              + "within your reach for `apikey.read` (the same rule that governs granting the "
              + "group). A scoped administrator therefore never sees a key bound to the "
              + "platform-admin group or to another department's group. An unscoped administrator "
              + "sees every key.");

        group.MapPost("/", CreateAsync)
          .RequirePermission("apikey.manage")
          .WithSummary("Provision an API key")
          .WithDescription(
              "Generates a key acting through the given access group, exactly as a user does. "
              + "The raw value is returned **once, in this response, and never again** — only "
              + "its SHA-256 is stored. A caller that loses it has to provision a new one.\n\n"
              + "This is how a service integration such as the standalone AI worker "
              + "(`ai-worker/`) obtains its `TRINETRA_API_KEY`.");

        group.MapDelete("/{id:guid}", RevokeAsync)
          .RequirePermission("apikey.manage")
          .WithSummary("Revoke an API key")
          .WithDescription(
              "Takes effect on the key's next request: it resolves to no groups and therefore "
              + "no permissions, and authentication then fails as a uniform `401 Invalid API "
              + "key` — indistinguishable from an unknown or expired key.\n\n"
              + "You may revoke a key only if you could have provisioned it — its access group is "
              + "within your reach for `apikey.manage`. A key outside your reach returns `404`, "
              + "identical to an unknown id and left untouched, so `apikey.manage` can no longer "
              + "kill another department's integration key or the platform-admin key.\n\n"
              + "Idempotent: revoking an already-revoked key you can reach still returns `204`. "
              + "The row is kept for audit and is never deleted.");
    }

    private static async Task<Ok<IReadOnlyList<ApiKeyResponse>>> ListAsync(
        ApiKeyRepository repo, HttpContext http, CancellationToken ct)
    {
        var caller = CallerContextFactory.From(http);
        caller.Require("apikey.read");
        var keys = await repo.ListAsync(caller, ct);

        return TypedResults.Ok<IReadOnlyList<ApiKeyResponse>>(
        [
            .. keys.Select(k => new ApiKeyResponse(
                k.Id, k.KeyId, k.DisplayName, k.GroupId, k.GroupCode,
                k.CreatedAt, k.ExpiresAt, k.LastUsedAt, k.RevokedAt)),
        ]);
    }

    private static async Task<Results<Created<ApiKeyCreatedResponse>, ProblemHttpResult>> CreateAsync(
        [FromBody] CreateApiKeyRequest request, NpgsqlDataSource db,
        AccessGroupRepository groups, HttpContext http, CancellationToken ct)
    {
        var caller = CallerContextFactory.From(http);

        // A key acts through its group exactly as a user does, so minting one runs the same
        // privilege-escalation chokepoint as adding a user to that group. Without it,
        // apikey.manage alone let a scoped administrator mint a key in the platform-admin group.
        if (await GroupGrantGuard.CheckAsync(groups, caller, request.GroupId, "apikey.manage", ct)
            is { } denied)
        {
            return denied;
        }

        var rawKey = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var keyId = $"ak_{Guid.NewGuid():N}"[..16];
        var keyHash = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(rawKey)));

        await using var work = await UnitOfWork.BeginAsync(db, ct);

        var id = await ApiKeyRepository.CreateAsync(
            keyId, keyHash, request.DisplayName, request.GroupId, request.ExpiresAt, caller,
            work, ct);

        await work.AuditAsync(caller, "create", "api_key", id.ToString(),
            before: null,
            after: new { request.DisplayName, request.GroupId, request.ExpiresAt, keyId },
            organizationUnitId: null, ct);
        await work.CommitAsync(ct);

        return TypedResults.Created(
            $"/api/v1/access-groups/{request.GroupId}", new ApiKeyCreatedResponse(id, keyId, rawKey));
    }

    private static async Task<Results<NoContent, NotFound>> RevokeAsync(
        Guid id, NpgsqlDataSource db, HttpContext http, CancellationToken ct)
    {
        var caller = CallerContextFactory.From(http);

        await using var work = await UnitOfWork.BeginAsync(db, ct);

        var result = await ApiKeyRepository.RevokeAsync(id, caller, work, ct);

        switch (result.Outcome)
        {
            case RevokeOutcome.NotFound:
                return TypedResults.NotFound();

            case RevokeOutcome.AlreadyRevoked:
                // No state change, so no audit row: the log records what happened, and nothing did.
                return TypedResults.NoContent();

            default:
                await work.AuditAsync(caller, "revoke", "api_key", id.ToString(),
                    before: new
                    {
                        result.DisplayName,
                        result.GroupId,
                        RevokedAt = result.PriorRevokedAt,
                    },
                    after: new { Revoked = true },
                    organizationUnitId: null, ct);
                await work.CommitAsync(ct);
                return TypedResults.NoContent();
        }
    }
}
