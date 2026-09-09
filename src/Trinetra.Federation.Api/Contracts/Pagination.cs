using System.Globalization;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Trinetra.Federation.Api.Contracts;

/// <summary>
/// Per-request pagination, <b>opt-in</b>. A list endpoint paginates only when the caller supplies
/// <c>?page=N</c> (N ≥ 1); otherwise it returns the plain array as before, trimmed to a hard
/// safety cap so no request can scan an unbounded table.
/// </summary>
/// <param name="Page">1-based page number. Null / 0 / negative ⇒ not paginated.</param>
/// <param name="PageSize">Rows per page. Clamped to the endpoint's maximum; defaults to 50.</param>
public sealed record PageQuery(int? Page = null, int? PageSize = null)
{
    public const int DefaultPageSize = 50;

    /// <summary>True when the caller asked for a specific page.</summary>
    public bool Enabled => Page is >= 1;

    /// <summary>Rows per page, clamped to <paramref name="max"/>.</summary>
    public int ResolvedPageSize(int max) => Math.Clamp(PageSize ?? DefaultPageSize, 1, max);

    /// <summary>Rows to skip for the requested page (0 when not paginated).</summary>
    public int Offset(int max) => Enabled ? (Page!.Value - 1) * ResolvedPageSize(max) : 0;

    /// <summary>The SQL <c>LIMIT</c> for this request: the page size, or the hard cap.</summary>
    public int Limit(int hardCap, int max) => Enabled ? ResolvedPageSize(max) : hardCap;
}

/// <summary>
/// One page of results, returned only when the request carried <c>?page</c>. A non-paginated
/// request gets the bare array instead (with <c>X-Total-Count</c> and, if the cap trimmed the
/// result, <c>X-Result-Capped: true</c> headers).
/// </summary>
public sealed record PageResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int Total)
{
    public int TotalPages => PageSize == 0 ? 0 : (Total + PageSize - 1) / PageSize;
}

/// <summary>
/// Shapes a list response per <see cref="PageQuery"/>: a <see cref="PageResult{T}"/> wrapper when
/// the caller paginated, otherwise the plain array with count / cap headers.
/// </summary>
internal static class Paginate
{
    /// <summary>Boilerplate appended to the OpenAPI description of every paginable list.</summary>
    public const string Doc =
        "**Pagination is opt-in.** Send `page` (1-based) and optionally `pageSize` (default 50) "
        + "to get a `{ items, page, pageSize, total, totalPages }` envelope. Omit `page` and the "
        + "response is the plain array as before, capped at a safety maximum — when the cap trims "
        + "the result the response carries `X-Result-Capped: true`. Either way `X-Total-Count` "
        + "gives the full match count.";

    /// <summary>
    /// The endpoint has already asked the repository for <paramref name="rows"/> (at most
    /// <c>query.Limit(hardCap, max)</c> of them) plus the full match count <paramref name="total"/>.
    /// Returns a <see cref="PageResult{T}"/> body when <paramref name="query"/> is paginated, or
    /// the bare array (with <c>X-Total-Count</c> / <c>X-Result-Capped</c> headers) otherwise.
    /// </summary>
    public static IResult Render<T>(
        HttpContext http, PageQuery query, int max, IReadOnlyList<T> rows, int total)
    {
        http.Response.Headers["X-Total-Count"] = total.ToString(CultureInfo.InvariantCulture);

        if (query.Enabled)
        {
            return TypedResults.Ok(
                new PageResult<T>(rows, query.Page!.Value, query.ResolvedPageSize(max), total));
        }

        if (total > rows.Count)
        {
            http.Response.Headers["X-Result-Capped"] = "true";
        }

        return TypedResults.Ok(rows);
    }
}
