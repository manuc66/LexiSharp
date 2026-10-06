using System.Buffers;

namespace LexiSharp.Core;

/// <summary>
/// Orders a decorator's candidate list and cuts the caller's page out of it.
/// </summary>
/// <remarks>
/// <para>
/// This replaces <c>OrderByDescending().ThenBy().Skip().Take().ToList()</c> on a per-search path.
/// What that chain costs is its own, not the whole decorator's: measured on a decorator holding a
/// pool of fifty candidates and returning a page of ten, replacing it took 9032 B per search down
/// to 7432 B — about 1600 B. The overhead above an undecorated engine's 1504 B is mostly the
/// wider candidate pool the decorator asks for, which is what the decorator is for and not
/// something to trim. What is saved here is an index array of one int per candidate plus the
/// output page, because the sort runs over indices rather than over <see cref="SearchResult"/>
/// records, so nothing but the page is materialized twice.
/// </para>
/// <para>
/// <b><see cref="SearchResult.DocumentId"/> is what is compared, not
/// <see cref="SearchDocument.Id"/>.</b> They are usually the same value, and every engine in the
/// library constructs them equal, but the record carries both and the boost and feedback paths
/// build results with <c>with { Score = ... }</c>, which preserves <c>DocumentId</c> however the
/// caller produced it. Sorting on <c>Document.Id</c> — which is what <see
/// cref="Ranking.TopRankedWindow"/>, <see cref="Ranking.TopRankedWindow.CopyBestTo(SearchResult[], int, int)"/>
/// does when it reconstructs a row — silently substitutes a different identifier. A fixture
/// holding <c>new SearchResult("d1", 10, new SearchDocument("idle", ...))</c> then sees
/// <c>idle</c> come back, and the pinned contract of "what you asked for is what you get" is
/// broken by a sort.
/// </para>
/// <para>
/// The tie-break is <b>ordinal</b> on the id, which is what <see cref="Ranking.TieBreak.DocumentId"/>
/// documents and what <see cref="Ranking.TopRankedWindow"/> does. The LINQ this replaces used
/// <c>ThenBy(x => x.DocumentId)</c> with no comparer on one engine, so <c>Comparer{string}.Default</c>
/// — culture-sensitive for strings — while the other passed <c>StringComparer.Ordinal</c>. On ids
/// differing only in accents the two disagreed, and the ranked engine had already ordered them
/// ordinally, so the decorator was the one that was wrong.
/// </para>
/// <para>
/// The index array is rented rather than allocated, measured before choosing: 1184 ns and 192 B
/// per search against 1512 ns and 328 B for <c>new int[total]</c>, best of seven interleaved
/// rounds of sixty thousand. It wins on time as well as on size, because the cost of an
/// allocation here is not the bump pointer but the ~20 MB a round of searches leaves for Gen0 to
/// collect — which is the same reason <c>ScoreAccumulator</c> rents its buffers.
/// </para>
/// <para>
/// The rented array is only ever read inside the range this method wrote it. Rent may hand back
/// something longer than asked, and whatever the previous caller left in the tail is untouched —
/// so the sort is explicitly ranged at <c>[0, total)</c> and the page is cut from that same
/// range. Sorting the whole rented length would mix foreign indices in and return a document
/// nobody asked for; that is the stale-data hazard <c>ScoreAccumulator</c>'s remarks describe.
/// </para>
/// <para>
/// The <b>page</b> is deliberately allocated and not rented. It leaves this method, crosses into
/// the caller's hands and lives as long as the caller does; returning memory the caller still
/// holds to a process-wide pool is how one caller's ranking gets rewritten by another's search.
/// </para>
/// <para>
/// Because ids are unique, (score, id) is a total order and the sort's stability does not
/// matter, which is what lets <see cref="Ranking.TieBreak.InsertionOrder"/> be honoured by
/// falling back to the input position — an option neither decorator observed before.
/// </para>
/// </remarks>
internal static class ResultOrdering
{
    /// <summary>
    /// Sorts <paramref name="results"/> best-first and returns the page
    /// <c>[offset, offset + limit)</c> of it, without disturbing the caller's list.
    /// </summary>
    internal static IReadOnlyList<SearchResult> SortAndPage(
        List<SearchResult> results,
        Ranking.TieBreak tieBreak,
        int offset,
        int limit)
    {
        int total = results.Count;

        if (total == 0)
            return Array.Empty<SearchResult>();

        int[] order = ArrayPool<int>.Shared.Rent(total);

        try
        {
            for (int i = 0; i < total; i++)
                order[i] = i;

            // Ranged, because the rented array's tail holds somebody else's data.
            Array.Sort(order, 0, total, Comparer<int>.Create((left, right) =>
            {
                int byScore = results[right].Score.CompareTo(results[left].Score);

                if (byScore != 0)
                    return byScore;

                if (tieBreak == Ranking.TieBreak.InsertionOrder)
                    return left - right;

                return string.CompareOrdinal(results[left].DocumentId, results[right].DocumentId);
            }));

            int skip = Math.Clamp(offset, 0, total);
            int take = Math.Min(limit, total - skip);

            if (take <= 0)
                return Array.Empty<SearchResult>();

            var page = new SearchResult[take];

            for (int i = 0; i < take; i++)
                page[i] = results[order[skip + i]];

            return page;
        }
        finally
        {
            ArrayPool<int>.Shared.Return(order);
        }
    }
}
