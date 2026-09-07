using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using Trinetra.Federation.Api.Auth;
using Trinetra.Federation.Api.Contracts;
using Trinetra.Federation.Api.OpenApi;
using Trinetra.Federation.Storage.Repositories;

namespace Trinetra.Federation.Api.Endpoints;

/// <summary>Read-only access to the authentication audit trail (finding 4-H3).</summary>
public static class AuthAuditEndpoints
{
    private const int DefaultPageSize = 100;
    private const int MaxPageSize = 500;
    private static readonly TimeSpan MaxWindow = TimeSpan.FromDays(31);
    private static readonly TimeSpan DefaultWindow = TimeSpan.FromDays(7);

    public static void MapAuthAuditEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/auth-audit", QueryAsync)
           .RequireAuthorization()
           .WithTags(ApiTags.AccessControl)
           .RequirePermission("authaudit.read")
           .WithSummary("Read the authentication audit trail")
           .WithDescription(
               "Append-only record of authentication events — login success and failure, "
               + "account lockout, refresh-token rotation and replay-theft, logout, self and "
               + "administrative password changes, on-login password rehash, and API-key "
               + "authentication outcomes. Newest first.\n\n"
               + "The login *response* is coarse — an unknown username and a wrong password for a "
               + "real account both return the same 401 — but the row records which: a resolved "
               + "`userId` means the name existed, a `presentedUsername` with no `userId` means "
               + "it did not. API-key successes are sampled (roughly one row per key per five "
               + "minutes); API-key failures are always recorded.\n\n"
               + "`from`/`to` default to the last 7 days and may span at most 31. Page with the "
               + "opaque `cursor`; a response without one is the end. SUPER_ADMIN only.");
    }

    private static async Task<Results<Ok<AuthAuditPage>, ProblemHttpResult>> QueryAsync(
        DateTimeOffset? from, DateTimeOffset? to,
        string? eventType, string? outcome, Guid? userId,
        string? cursor, int? limit,
        AuthAuditRepository repo, HttpContext http, CancellationToken ct)
    {
        CallerContextFactory.From(http).Require("authaudit.read");

        var toV = to ?? DateTimeOffset.UtcNow;
        var fromV = from ?? toV - DefaultWindow;

        if (toV <= fromV || toV - fromV > MaxWindow)
        {
            return Problem("Invalid range",
                "`from` must precede `to` and the window may span at most 31 days.");
        }

        if (outcome is not null and not ("success" or "failure" or "lockout" or "revoked"))
        {
            return Problem("Invalid outcome",
                "`outcome` must be one of success, failure, lockout, revoked.");
        }

        var pageSize = Math.Clamp(limit ?? DefaultPageSize, 1, MaxPageSize);

        DateTimeOffset? cursorTime = null;
        long? cursorId = null;
        if (!string.IsNullOrEmpty(cursor) && !TryDecodeCursor(cursor, out cursorTime, out cursorId))
        {
            return Problem("Invalid cursor", "The cursor is malformed. Restart without one.");
        }

        var rows = await repo.ListAsync(
            new AuthAuditQuery(fromV, toV, eventType, outcome, userId, cursorTime, cursorId, pageSize),
            ct);

        var items = rows.Select(r => new AuthAuditItem(
            r.OccurredAt, r.Id, r.EventType, r.Outcome, r.UserId, r.ApiKeyId,
            r.PresentedUsername, r.SourceAddress, r.UserAgent, r.Jti,
            r.Detail is null ? null : JsonSerializer.Deserialize<JsonElement>(r.Detail))).ToList();

        var next = rows.Count == pageSize
            ? EncodeCursor(rows[^1].OccurredAt, rows[^1].Id)
            : null;

        return TypedResults.Ok(new AuthAuditPage(items, next));
    }

    private static ProblemHttpResult Problem(string title, string detail) =>
        (ProblemHttpResult)TypedResults.Problem(
            title: title, detail: detail, statusCode: StatusCodes.Status400BadRequest);

    private static string EncodeCursor(DateTimeOffset occurredAt, long id) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(
            $"{occurredAt.UtcDateTime:O}|{id.ToString(CultureInfo.InvariantCulture)}"));

    private static bool TryDecodeCursor(string cursor, out DateTimeOffset? occurredAt, out long? id)
    {
        occurredAt = null;
        id = null;
        try
        {
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(cursor)).Split('|', 2);
            if (parts.Length != 2
                || !DateTimeOffset.TryParse(parts[0], CultureInfo.InvariantCulture,
                       DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                       out var parsedTime)
                || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture,
                       out var parsedId))
            {
                return false;
            }

            occurredAt = parsedTime;
            id = parsedId;
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
