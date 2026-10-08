using LexiSharp.Core;

namespace LexiSharp.Ranking;

/// <summary>
/// A bounded, worst-first accumulator holding the best <c>Limit</c> entries seen so far, used by
/// the engines to cut a page without ever sorting the whole candidate set.
/// </summary>
/// <remarks>
/// <para>
/// Both in-process engines rank a candidate set that is routinely far larger than the page
/// requested, so a full sort pays O(n log n) plus one <see cref="Core.SearchResult"/> per
/// surviving candidate to produce <c>Limit</c> rows. This keeps only the window: each candidate
/// costs one comparison against the current worst, and <see cref="Core.SearchResult"/> objects
/// are materialized only for the entries that survive.
/// </para>
/// <para>
/// The window holds <b>ordinals</b>, not documents, and resolves a document only where a document
/// is actually needed — to break a tie on the identifier, and to materialize the page. That is what
/// lets a search pay for resolution once per kept row instead of once per candidate, which for an
/// in-memory index is the difference between almost nothing and a list index, and for a segment
/// index is the difference between almost nothing and decoding a whole stored document.
/// </para>
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
    private readonly Func<int, SearchDocument?> _resolve;
    private readonly (double Score, int DocumentOrdinal)[] _entries;

    /// <summary>
    /// Documents the caller already resolved, one per retained slot, aligned with
    /// <see cref="_entries"/>; empty for a search that keeps its page by ordinal.
    /// </summary>
    private SearchDocument?[]? _resolved;

    /// <summary>Arrival positions, allocated exactly when <see cref="_tieBreak"/> reads them.</summary>
    private readonly long[]? _ordinals;

    private int _count;

    /// <param name="limit">Window size: the number of best entries to retain.</param>
    /// <param name="tieBreak">How to order candidates whose scores are exactly equal.</param>
    /// <param name="resolve">
    /// The document for an ordinal, called only on ties and at the page cut, never per candidate.
    /// </param>
    public TopRankedWindow(int limit, TieBreak tieBreak, Func<int, SearchDocument?> resolve)
    {
        _limit = limit;
        _tieBreak = tieBreak;
        _resolve = resolve;
        _entries = limit <= 0 ? Array.Empty<(double, int)>() : new (double, int)[limit];
        _resolved = null;
        _ordinals = limit > 0 && tieBreak == TieBreak.InsertionOrder ? new long[limit] : null;
    }

    /// <summary>Number of entries currently retained (at most the window size).</summary>
    public int Count => _count;

    /// <summary>
    /// Offers a candidate to the window. Returns without keeping it when the window is full and the
    /// candidate ranks at or below the current worst.
    /// </summary>
    /// <param name="score">The candidate's score.</param>
    /// <param name="documentOrdinal">The candidate's document, as an ordinal.</param>
    /// <param name="resolved">
    /// The candidate's document when the caller already holds it — otherwise the window resolves the
    /// ordinal later, and the kept rows and the ties are where the resolution lands.
    /// </param>
    /// <param name="ordinal">
    /// Position of the candidate in the order the engine produced candidates. Only read under
    /// <see cref="TieBreak.InsertionOrder"/>, where it is what makes the order total.
    /// </param>
    public void Add(double score, int documentOrdinal, SearchDocument? resolved = null, long ordinal = 0)
    {
        if (_count < _limit)
        {
            Set(_count, score, documentOrdinal, ordinal, resolved);
            _count++;

            // Sift the newcomer up from the bottom (worst) end. The list is worst-first, so the
            // order is settled as soon as the entry above is *worse* than the one below.
            for (int j = _count - 1; j > 0; j--)
            {
                if (RanksBefore(j, j - 1))
                    break;

                Swap(j - 1, j);
            }

            return;
        }

        if (RanksBefore(0, score, documentOrdinal, ordinal, resolved))
            return; // the incumbent worst already outranks the newcomer

        Set(0, score, documentOrdinal, ordinal, resolved);

        // Sift the new worst back down to where it belongs.
        for (int j = 0; j < _count - 1; j++)
        {
            if (RanksBefore(j + 1, j))
                break;

            Swap(j, j + 1);
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
            int at = _count - 1 - (skip + i);
            var entry = _entries[at];
            var document = Resolve(at, entry.DocumentOrdinal);
            destination[i] = new SearchResult(document.Id, entry.Score, document);
        }
    }

    /// <summary>
    /// The document an entry or a tie needs: the caller-resolved one when there is one, the index's
    /// otherwise. The refusal is loud because either path is a contract the caller met.
    /// </summary>
    private SearchDocument Resolve(int at, int documentOrdinal) =>
        _resolved is not null && _resolved[at] is { } known
            ? known
            : _resolve(documentOrdinal)
                ?? throw new InvalidOperationException($"the window held ordinal {documentOrdinal}, which its index cannot resolve");

    private void Set(int index, double score, int documentOrdinal, long ordinal, SearchDocument? resolved)
    {
        _entries[index] = (score, documentOrdinal);

        if (resolved is not null)
        {
            _resolved ??= new SearchDocument?[_entries.Length];
            _resolved[index] = resolved;
        }

        if (_ordinals is not null)
            _ordinals[index] = ordinal;
    }

    private void Swap(int left, int right)
    {
        (_entries[left], _entries[right]) = (_entries[right], _entries[left]);

        if (_resolved is not null)
            (_resolved[left], _resolved[right]) = (_resolved[right], _resolved[left]);

        if (_ordinals is not null)
            (_ordinals[left], _ordinals[right]) = (_ordinals[right], _ordinals[left]);
    }

    /// <summary>
    /// Whether the entry at <paramref name="left"/> belongs before the entry at
    /// <paramref name="right"/> in best-first order: a higher score, or the same score ordered by
    /// the configured tie-break.
    /// </summary>
    private bool RanksBefore(int left, int right)
    {
        if (_entries[left].Score > _entries[right].Score)
            return true;

        if (!ScoresAreTied(_entries[left].Score, _entries[right].Score))
            return false;

        return _tieBreak == TieBreak.InsertionOrder
            ? _ordinals![left] < _ordinals[right]
            : string.CompareOrdinal(IdOf(left), IdOf(right)) < 0;
    }

    /// <summary>
    /// Whether the entry at <paramref name="incumbent"/> belongs before a candidate the window has
    /// not retained — the full-window test, which is the only comparison asking about a candidate
    /// that is not an entry.
    /// </summary>
    private bool RanksBefore(int incumbent, double score, int documentOrdinal, long ordinal, SearchDocument? resolved)
    {
        if (_entries[incumbent].Score > score)
            return true;

        if (!ScoresAreTied(_entries[incumbent].Score, score))
            return false;

        var incumbentDocument = Resolve(incumbent, _entries[incumbent].DocumentOrdinal);
        var newcomer = resolved ?? _resolve(documentOrdinal)
            ?? throw new InvalidOperationException($"the window held ordinal {documentOrdinal}, which its index cannot resolve");

        return _tieBreak == TieBreak.InsertionOrder
            ? _ordinals![incumbent] < ordinal
            : string.CompareOrdinal(incumbentDocument.Id, newcomer.Id) < 0;
    }

    private string IdOf(int index) => Resolve(index, _entries[index].DocumentOrdinal).Id;

    /// <summary>
    /// Whether two scores are the same value. Bitwise equality on purpose, so this cannot be
    /// "fixed" into a tolerance comparison: an epsilon here would treat two documents whose scores
    /// differ in the last bits as interchangeable and then order them by id, quietly overriding
    /// the ranking the scores actually expressed.
    /// </summary>
    private static bool ScoresAreTied(double left, double right) => left == right; // NOSONAR:S1244
}