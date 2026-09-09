namespace Trinetra.Federation.Storage.Repositories;

/// <summary>
/// A slice of a list query: at most <c>Limit</c> rows from <c>Offset</c>, plus <see cref="Total"/>
/// — the full match count, so the caller can render a pager and detect that a hard cap trimmed a
/// non-paginated request.
/// </summary>
/// <remarks>
/// The count comes from <c>count(*) OVER()</c> in the same statement as the rows: one round trip,
/// one plan, computed <b>before</b> the <c>LIMIT</c> so it reflects everything that matched. When
/// the query returns no rows the count column is absent, so <see cref="Total"/> is 0 — a client
/// that has paged past the end cannot distinguish that from an empty result and should compute
/// <c>totalPages</c> from page 1.
/// </remarks>
public sealed record PagedRows<T>(IReadOnlyList<T> Items, int Total);

/// <summary>A <c>LIMIT</c>/<c>OFFSET</c> window for a list query.</summary>
/// <remarks>
/// Endpoints translate <c>?page</c> into one of these; a non-paginated request still gets a
/// window whose <see cref="Limit"/> is the endpoint's hard safety cap, so no query is unbounded.
/// <see cref="Offset"/> is a <c>long</c> — PostgreSQL <c>OFFSET</c> is <c>bigint</c> and a large
/// page number times a large page size overflows <c>int</c>.
/// </remarks>
public readonly record struct PageWindow(int Limit, long Offset)
{
    /// <summary>A window that returns everything up to <paramref name="hardCap"/> rows.</summary>
    public static PageWindow UpTo(int hardCap) => new(hardCap, 0);
}

/// <summary>Narrows a <c>bigint</c> <c>count(*) OVER()</c> result to <c>int</c> without overflowing.</summary>
public static class PagedCount
{
    public static int From(long total) => total > int.MaxValue ? int.MaxValue : (int)total;
}
