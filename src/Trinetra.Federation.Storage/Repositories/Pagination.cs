namespace Trinetra.Federation.Storage.Repositories;

/// <summary>
/// A slice of a list query: at most <c>Limit</c> rows from <c>Offset</c>, plus <see cref="Total"/>
/// — the full match count, so the caller can render a pager and detect that a hard cap trimmed a
/// non-paginated request.
/// </summary>
/// <remarks>
/// The count comes from <c>count(*) OVER()</c> in the same statement as the rows: one round trip,
/// one plan, computed <b>before</b> the <c>LIMIT</c> so it reflects everything that matched. When
/// the query returns no rows the count column is absent, so <see cref="Total"/> is 0.
/// </remarks>
public readonly record struct PagedRows<T>(IReadOnlyList<T> Items, int Total);

/// <summary>A <c>LIMIT</c>/<c>OFFSET</c> window for a list query.</summary>
/// <remarks>
/// Endpoints translate <c>?page</c> into one of these; a non-paginated request still gets a
/// window whose <see cref="Limit"/> is the endpoint's hard safety cap, so no query is unbounded.
/// </remarks>
public readonly record struct PageWindow(int Limit, int Offset)
{
    /// <summary>A window that returns everything up to <paramref name="hardCap"/> rows.</summary>
    public static PageWindow UpTo(int hardCap) => new(hardCap, 0);
}
