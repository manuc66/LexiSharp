using LexiSharp.Core;

namespace LexiSharp.Ranking;

/// <summary>
/// A bounded, worst-first accumulator holding the best <c>Limit</c> entries seen so far, used by
/// the engines to cut a page without ever sorting the whole candidate set.
/// </summary>
/// <remarks>
/// Both in-process engines rank a candidate set that is routinely far larger than the page
/// requested, so a full sort pays O(n log n) plus one <see cref="Core.SearchResult"/> per
/// surviving candidate to produce <c>Limit</c> rows. This keeps only the window: each candidate
/// costs one comparison against the current worst, and <see cref="Core.SearchResult"/> objects
/// are materialized only for the entries that survive.
/// <para>
/// The order is total and deterministic: higher score first, and ties broken by ordinal document
/// id. Ids are unique, so the order never depends on the order candidates were produced in — which
/// is what keeps a ranking reproducible across machines.
/// </para>
/// </remarks>
internal sealed class TopRankedWindow
{
    private readonly int _limit;
    private readonly TieBreak _tieBreak;
    private (double Score, SearchDocument Document, long Ordinal)[] _entries;
    private int _count;

    /// <param name="limit">Window size: the number of best entries to retain.</param>
    /// <param name="tieBreak">How to order candidates whose scores are exactly equal.</param>
    public TopRankedWindow(int limit, TieBreak tieBreak = TieBreak.DocumentId)
    {
        _limit = limit;
        _tieBreak = tieBreak;
        _entries = limit <= 0 ? Array.Empty<(double, SearchDocument, long)>() : new (double, SearchDocument, long)[limit];
    }

    /// <summary>Number of entries currently retained (at most the window size).</summary>
    public int Count => _count;

    /// <summary>
    /// Offers a candidate to the window. Returns without keeping it when the window is full and the
    /// candidate ranks at or below the current worst.
    /// </summary>
    /// <param name="score">The candidate's score.</param>
    /// <param name="document">The candidate's document.</param>
    /// <param name="ordinal">
    /// Position of the candidate in the order the engine produced candidates. Only read under
    /// <see cref="TieBreak.InsertionOrder"/>, where it is what makes the order total.
    /// </param>
    public void Add(double score, SearchDocument document, long ordinal = 0)
    {
        var entry = (Score: score, Document: document, Ordinal: ordinal);

        if (_count < _limit)
        {
            _entries[_count++] = entry;

            // Sift the newcomer up from the bottom (worst) end. The list is worst-first, so the
            // order is settled as soon as the entry above is *worse* than the one below.
            for (int j = _count - 1; j > 0; j--)
            {
                if (RanksBefore(_entries[j], _entries[j - 1]))
                    break;

                (_entries[j - 1], _entries[j]) = (_entries[j], _entries[j - 1]);
            }

            return;
        }

        if (RanksBefore(_entries[0], entry))
            return; // the incumbent worst already outranks the newcomer

        _entries[0] = entry;

        // Sift the new worst back down to where it belongs.
        for (int j = 0; j < _count - 1; j++)
        {
            if (RanksBefore(_entries[j + 1], _entries[j]))
                break;

            (_entries[j], _entries[j + 1]) = (_entries[j + 1], _entries[j]);
        }
    }

    /// <summary>
    /// Writes the best-first page <c>[skip, skip + count)</c> of the window into
    /// <paramref name="destination"/>, which must be exactly <paramref name="count"/> long.
    /// </summary>
    public void CopyBestTo(SearchResult[] destination, int skip, int count)
    {
        for (int i = 0; i < count; i++)
        {
            var entry = _entries[_count - 1 - (skip + i)];
            destination[i] = new SearchResult(entry.Document.Id, entry.Score, entry.Document);
        }
    }

    /// <summary>
    /// Whether <paramref name="left"/> belongs before <paramref name="right"/> in best-first order:
    /// a higher score, or the same score ordered by the configured tie-break.
    /// </summary>
    private bool RanksBefore(
        (double Score, SearchDocument Document, long Ordinal) left,
        (double Score, SearchDocument Document, long Ordinal) right)
    {
        if (left.Score > right.Score)
            return true;

        if (!ScoresAreTied(left.Score, right.Score))
            return false;

        // The id is what makes the order total. Tie-breaking on enumeration order did not, because
        // that order came from Directory.EnumerateFiles and is therefore a property of the
        // filesystem: the same checkout ranked differently on a developer machine and on a CI
        // runner, which is what made the golden master gate environment-dependent.
        return _tieBreak == TieBreak.InsertionOrder
            ? left.Ordinal < right.Ordinal
            : string.CompareOrdinal(left.Document.Id, right.Document.Id) < 0;
    }

    /// <summary>
    /// Whether two scores are the same value. Bitwise equality on purpose, so this cannot be
    /// "fixed" into a tolerance comparison: an epsilon here would treat two documents whose scores
    /// differ in the last bits as interchangeable and then order them by id, quietly overriding
    /// the ranking the scores actually expressed.
    /// </summary>
    private static bool ScoresAreTied(double left, double right) => left == right; // NOSONAR:S1244
}
