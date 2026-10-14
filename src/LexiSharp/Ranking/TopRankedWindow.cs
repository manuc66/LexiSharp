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
/// <para>
/// Under <see cref="TieBreak.InsertionOrder"/> the tie is decided by the document's ordinal instead,
/// which for a text index is the position the document was added at. That order is total too, and it
/// is the same whichever path produced the candidates: every path supplies the document's ordinal,
/// so a page served by the term-at-a-time pass and the same page served by the per-document loop
/// agree among tied documents, at a given corpus size and across sizes.
/// </para>
/// </remarks>
internal sealed class TopRankedWindow
{
    private readonly int _limit;
    private readonly TieBreak _tieBreak;
    private readonly Func<int, SearchDocument?> _resolve;

    /// <summary>
    /// The id of the document at an ordinal, without decoding it, when the index can answer it that way.
    /// </summary>
    /// <remarks>
    /// The tie-break compares document ids, and a segment index costs a stored-document decode to answer
    /// either; an <see cref="IOrdinalIdIndex"/> answers the id alone, which is what the comparison reads
    /// and the page cut does not. Measured on a corpus whose documents tie, the difference is the whole
    /// of a store's per-query allocation. When the index offers it, the id comes from here and the
    /// document is decoded only for the rows the page keeps.
    /// </remarks>
    private readonly Func<int, string?>? _resolveId;
    private readonly (double Score, int DocumentOrdinal)[] _entries;

    /// <summary>
    /// Documents the caller already resolved, one per retained slot, aligned with
    /// <see cref="_entries"/>; empty for a search that keeps its page by ordinal.
    /// </summary>
    private SearchDocument?[]? _resolved;

    /// <summary>Arrival positions, allocated exactly when <see cref="_tieBreak"/> reads them.</summary>
    private readonly long[]? _ordinals;

    /// <summary>
    /// Whether the document ordinal handed to the window is the document's position in the index's
    /// ordinal space — which for a text index is the order documents were added — and so decides a
    /// tie under <see cref="TieBreak.InsertionOrder"/> on its own, without reading the order
    /// candidates happened to arrive in.
    /// </summary>
    /// <remarks>
    /// An index that cannot enumerate its ordinals passes <c>-1</c> for a document ordinal, and for
    /// it this is false: the arrival position is the only insertion order such an index can offer,
    /// and that is what it keeps.
    /// </remarks>
    private readonly bool _byDocumentOrdinal;

    private int _count;

    /// <param name="limit">Window size: the number of best entries to retain.</param>
    /// <param name="tieBreak">How to order candidates whose scores are exactly equal.</param>
    /// <param name="resolve">
    /// The document for an ordinal, called only on ties and at the page cut, never per candidate.
    /// </param>
    /// <param name="resolveId">
    /// The id for an ordinal without decoding the document, when the index can answer it that way.
    /// Called in exactly the places the tie-break reads an id, so a segment index decodes a document
    /// only for the rows a page keeps.
    /// </param>
    /// <param name="ordinalsAreInsertionPositions">
    /// Whether the document ordinal this window is handed for each candidate is a position in the
    /// index's ordinal space. When it is, <see cref="TieBreak.InsertionOrder"/> compares those
    /// positions and the arrival order is not recorded at all — the document's own ordinal already
    /// says where it was added.
    /// </param>
    public TopRankedWindow(
        int limit,
        TieBreak tieBreak,
        Func<int, SearchDocument?> resolve,
        Func<int, string?>? resolveId = null,
        bool ordinalsAreInsertionPositions = false)
    {
        _limit = limit;
        _tieBreak = tieBreak;
        _resolve = resolve;
        _resolveId = resolveId;
        _entries = limit <= 0 ? Array.Empty<(double, int)>() : new (double, int)[limit];
        _resolved = null;
        _byDocumentOrdinal = ordinalsAreInsertionPositions;
        _ordinals = limit > 0 && tieBreak == TieBreak.InsertionOrder && !_byDocumentOrdinal ? new long[limit] : null;
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
    /// <see cref="TieBreak.InsertionOrder"/> when the index has no ordinal space, where it is what
    /// makes the order total.
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
            ? InsertionKeyOf(left) < InsertionKeyOf(right)
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

        var incumbentOrdinal = _entries[incumbent].DocumentOrdinal;
        var incumbentId = _resolveId?.Invoke(incumbentOrdinal);
        string newcomerId;

        if (resolved is not null)
        {
            newcomerId = resolved.Id;
        }
        else if (_resolveId is not null)
        {
            newcomerId = _resolveId(documentOrdinal)
                ?? throw new InvalidOperationException($"the window held ordinal {documentOrdinal}, which cannot resolve an id");
        }
        else
        {
            newcomerId = _resolve(documentOrdinal)?.Id
                ?? throw new InvalidOperationException($"the window held ordinal {documentOrdinal}, which its index cannot resolve");
        }

        return _tieBreak == TieBreak.InsertionOrder
            ? InsertionKeyOf(incumbent) < InsertionKeyOf(documentOrdinal, ordinal)
            : string.CompareOrdinal(incumbentId ?? Resolve(incumbent, incumbentOrdinal).Id, newcomerId) < 0;
    }

    /// <summary>
    /// The position that decides a tie under <see cref="TieBreak.InsertionOrder"/> for a retained
    /// entry: the document's own ordinal when the index has an ordinal space — the position it holds
    /// in it, which is where it was added — and the position the engine produced it in otherwise.
    /// </summary>
    private long InsertionKeyOf(int slot)
    {
        // The arrival array exists exactly where the second branch is reachable: the constructor
        // records it for an insertion-order tie-break on a window whose ordinals are not insertion
        // positions, and that combination is the only one that reads it. A missing array is therefore
        // a broken window rather than a null to index — named here, as the invariants below are.
        return _byDocumentOrdinal
            ? _entries[slot].DocumentOrdinal
            : ArrivalPositions()[slot];
    }

    /// <summary>
    /// The arrival position recorded for each retained entry: what the engine handed the window, kept
    /// because an index with no ordinal space has no other insertion order to read. Null until the
    /// constructor allocates it, which it does only where this is the tie-break in force.
    /// </summary>
    private long[] ArrivalPositions() =>
        _ordinals ?? throw new InvalidOperationException(
            "the arrival order was not recorded, so an insertion-order tie-break has nothing to read");

    /// <summary>The same position for a candidate the window has not retained.</summary>
    private long InsertionKeyOf(int documentOrdinal, long arrival) =>
        _byDocumentOrdinal ? documentOrdinal : arrival;

    private string IdOf(int index)
    {
        int ordinal = _entries[index].DocumentOrdinal;

        string id = _resolveId?.Invoke(ordinal) ?? Resolve(index, ordinal).Id;

        return id ?? throw new InvalidOperationException($"the window held ordinal {ordinal}, which cannot resolve an id");
    }

    /// <summary>
    /// Whether two scores are the same value. Bitwise equality on purpose, so this cannot be
    /// "fixed" into a tolerance comparison: an epsilon here would treat two documents whose scores
    /// differ in the last bits as interchangeable and then order them by id, quietly overriding
    /// the ranking the scores actually expressed.
    /// </summary>
    private static bool ScoresAreTied(double left, double right) => left == right; // NOSONAR:S1244
}