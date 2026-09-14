using Microsoft.AspNetCore.Http.HttpResults;
using Trinetra.Federation.Api.Auth;
using Trinetra.Federation.Api.Contracts;
using Trinetra.Federation.Api.OpenApi;
using Trinetra.Federation.Storage.Repositories;

namespace Trinetra.Federation.Api.Endpoints;

/// <summary>
/// Cross-camera possible-match groups, computed by the background correlation pass
/// (architecture §8) rather than on request. Read-only — rule configuration is system-seeded and
/// has no write route in this pass.
/// </summary>
public static class CorrelationEndpoints
{
    /// <summary>Widest window a single list query may span.</summary>
    private static readonly TimeSpan MaxWindow = TimeSpan.FromDays(7);

    private const int DefaultWindowDays = 1;
    private const int ListHardCap = 500;

    public static void MapCorrelationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/correlation")
            .WithTags(ApiTags.Correlation).RequireAuthorization();

        group.MapGet("/groups", ListAsync)
          .WithPaginatedResponse<CorrelationGroupSummary>()
          .RequirePermission("correlation.read")
          .WithSummary("List cross-camera possible-match groups")
          .WithDescription(
              "Groups whose activity overlaps `from`/`to` (default: the last 24 hours, may span "
              + "at most 7 days), newest activity first. A group appears once at least one "
              + "member event is in the caller's organization/geography scope — the group can "
              + "still include cameras outside that scope; `GET /groups/{id}` shows only the "
              + "members the caller can reach.\n\n"
              + "**`confidence` is a possible-match score in [0,1], never certainty** — "
              + "cross-camera identity is probabilistic.\n\n"
              + Paginate.Doc);

        group.MapGet("/groups/{id:guid}", GetAsync)
          .RequirePermission("correlation.read")
          .WithSummary("A correlation group and its member events")
          .WithDescription(
              "The group plus every member event the caller is scoped to reach. 404 if the id "
              + "is unknown or no member is reachable at all.\n\n"
              + "**`confidence` is a possible-match score, never certainty.**");
    }

    private static async Task<IResult> ListAsync(
        DateTimeOffset? from, DateTimeOffset? to, int? page, int? pageSize,
        CorrelationRepository repo, HttpContext http, CancellationToken ct)
    {
        var caller = CallerContextFactory.From(http);

        var end = to ?? DateTimeOffset.UtcNow;
        var start = from ?? end.AddDays(-DefaultWindowDays);

        if (end <= start)
        {
            return TypedResults.Problem(
                title: "Invalid time range", detail: "'to' must be after 'from'.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (end - start > MaxWindow)
        {
            return TypedResults.Problem(
                title: "Time range too wide",
                detail: $"The window may span at most {MaxWindow.TotalDays:F0} days.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var q = new PageQuery(page, pageSize);
        var rows = await repo.ListAsync(
            start, end, new PageWindow(q.Limit(ListHardCap, ListHardCap), q.Offset(ListHardCap)),
            caller, ct);

        return Paginate.Render(http, q, ListHardCap, [.. rows.Items.Select(ToSummary)], rows.Total);
    }

    private static async Task<Results<Ok<CorrelationGroupDetail>, NotFound>> GetAsync(
        Guid id, CorrelationRepository repo, HttpContext http, CancellationToken ct)
    {
        var caller = CallerContextFactory.From(http);
        var row = await repo.GetAsync(id, caller, ct);

        return row is null
            ? TypedResults.NotFound()
            : TypedResults.Ok(new CorrelationGroupDetail(
                ToSummary(row.Group),
                [.. row.Members.Select(m => new CorrelationGroupMember(
                    m.FederationEventId, m.EventOccurredAt, m.CameraId, m.SourceVmsId,
                    m.OrganizationUnitId, m.GeographicAreaId))]));
    }

    private static CorrelationGroupSummary ToSummary(CorrelationGroupRow r) => new(
        r.Id, r.RuleCode, r.NaturalKey, r.WindowBucket, r.Confidence, r.MemberCount,
        r.FirstOccurredAt, r.LastOccurredAt, r.CreatedAt);
}
