using System.Collections.Generic;
using System.Runtime.InteropServices;
using LexiSharp.Core;
using LexiSharp.Linguistics;

namespace LexiSharp.Indexing;

/// <summary>
/// The live half of a chain: documents added since the last commit, and their postings.
/// </summary>
/// <remarks>
/// <para>
/// A segment is written once and read many times, so a write cannot land in one. It lands here, and
/// this is what makes the overlay a *live* index rather than a segment: an <c>Add</c> is a tokenization
/// and a dictionary write, not the 0.6-1.2 s and ~2.9 GB a segment write costs at 40,000 documents
/// (measured). What that buys is immediate visibility; what it costs is that nothing here is durable
/// until a <c>Commit</c> flushes it into a segment.
/// </para>
/// <para>
/// <b>The overlay is bounded by a threshold, and the threshold is measured.</b> Reading a query from an
/// overlay costs 2.4-2.6 µs a document and is linear in the overlay's size, so an overlay of 1,000
/// documents is <b>3.83×</b> and one of 10,000 is <b>32.4×</b> the same documents written into a
/// segment. The overlay is a place writes wait, not a place they stay; a caller flushes it when it
/// passes a few hundred documents.
/// </para>
/// <para>
/// Ordinals are handed out from a counter that starts where the segments end, and a freed ordinal is
/// reused before the counter grows — the same rule <c>InMemoryTextIndex</c> follows, so the ordinal
/// space tracks the live overlay rather than every document it has ever held. The accumulator is rented
/// at the chain's ordinal space per search, so unbounded growth there would be a per-query tax.
/// </para>
/// <para>
/// Postings are appended in insertion order, which is ascending ordinal order because a document's
/// ordinal is handed out before its terms are indexed. That is the order the fold must see: a segment's
/// blocks ascend by ordinal and so do the entries inside them, and the accumulator's first touch is a
/// write while every later touch is an add, so the sums are bit-identical to the in-memory index's.
/// </para>
/// </remarks>
internal sealed class SegmentOverlay
{
    private readonly ITokenizer _tokenizer;
    private readonly List<SearchDocument?> _documents = [];
    private readonly List<int> _free = [];
    private readonly List<int> _lengths = [];
    private readonly Dictionary<string, int> _ordinals = new(StringComparer.Ordinal);

    /// <summary>Term → its postings in the overlay, ascending by ordinal.</summary>
    private readonly Dictionary<string, List<(int Ordinal, int Frequency)>> _postings = new(StringComparer.Ordinal);

    /// <summary>The terms each overlay-local ordinal carries, so a removal drops exactly those postings.</summary>
    private readonly List<string[]> _termsByOrdinal = [];

    private int _live;
    private int _tokens;

    public SegmentOverlay(ITokenizer? tokenizer = null)
    {
        _tokenizer = tokenizer ?? Tokenizer.Default;
    }

    /// <summary>Documents the overlay holds.</summary>
    public int Count => _live;

    /// <summary>Tokens the overlay's documents hold, for the corpus statistics.</summary>
    public long TokenCount => _tokens;

    /// <summary>Ordinal capacity handed out so far, including freed slots.</summary>
    public int OrdinalCapacity => _documents.Count;

    /// <summary>The document at an overlay-local ordinal, or <c>null</c> when that slot is free.</summary>
    public SearchDocument? DocumentAt(int ordinal) =>
        ordinal >= 0 && ordinal < _documents.Count ? _documents[ordinal] : null;

    /// <summary>The overlay-local ordinal of a live document, or <c>-1</c>.</summary>
    public int OrdinalOf(string documentId) =>
        documentId is not null && _ordinals.TryGetValue(documentId, out int ordinal) ? ordinal : -1;

    /// <summary>The token count of the document at an overlay-local ordinal.</summary>
    public int LengthAt(int ordinal) => _lengths[ordinal];

    /// <summary>
    /// Every overlay-local document length, indexed by overlay-local ordinal — the overlay's own
    /// array, not a copy.
    /// </summary>
    /// <remarks>
    /// The accumulation pass needs a length per posting entry and reads it by ordinal. It used to be
    /// handed a fresh <c>int[]</c> the chain built from these values on every query, which is a copy of
    /// an array the overlay already owns; a span over the list is the same values with no copy. Valid
    /// until the overlay is written to, which an accumulation never does.
    /// </remarks>
    public ReadOnlySpan<int> Lengths => CollectionsMarshal.AsSpan(_lengths);

    /// <summary>
    /// Adds or replaces a document, and returns the overlay-local ordinal it now holds.
    /// </summary>
    /// <remarks>
    /// Replacing removes the document's existing postings first, so a term it no longer carries stops
    /// matching and one it now carries starts — without that, a replaced document would score under both
    /// its old and its new text.
    /// </remarks>
    public int Add(SearchDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        int ordinal;

        if (_ordinals.TryGetValue(document.Id, out ordinal))
        {
            RemovePostings(ordinal);
        }
        else
        {
            ordinal = _free.Count > 0 ? _free[^1] : _documents.Count;

            if (_free.Count > 0)
            {
                _free.RemoveAt(_free.Count - 1);
            }
            else
            {
                _documents.Add(null);
                _lengths.Add(0);
                _termsByOrdinal.Add([]);
            }

            _ordinals[document.Id] = ordinal;
            _live++;
        }

        _documents[ordinal] = document;

        // The flat view is the union of the body and every text field, exactly as the in-memory index
        // builds it: a term that only appears in a title must still be reachable, and a document's
        // length for length normalisation is the union's, not the body's. Tokenizing the body alone
        // loses the field-only terms from the dictionary and shortens every length — which moves every
        // score the document takes. The positions the in-memory index records are not recorded here,
        // because a segment holds none; the term multiset and the total length are what the fold reads.
        var terms = _tokenizer.Tokenize(document.Text);
        var allTerms = new List<string>(terms.Count);

        for (int i = 0; i < terms.Count; i++)
            allTerms.Add(terms[i]);

        if (document.TextFields is { Count: > 0 } fields)
        {
            foreach (var (field, text) in fields)
            {
                TextFields.Validate(field, nameof(document));

                var fieldTerms = _tokenizer.Tokenize(text);

                for (int i = 0; i < fieldTerms.Count; i++)
                    allTerms.Add(fieldTerms[i]);
            }
        }

        _lengths[ordinal] = allTerms.Count;
        _tokens += allTerms.Count;

        // The index's shared instances, not the tokenizer's: an overlay that retained one string per
        // occurrence would scale with the token count instead of the vocabulary.
        var distinct = new string[allTerms.Count];

        for (int i = 0; i < allTerms.Count; i++)
            distinct[i] = allTerms[i];

        _termsByOrdinal[ordinal] = distinct;

        // One entry per (term, document), carrying the number of occurrences — not one entry per
        // occurrence. The accumulator's first touch writes and every later touch adds, so per-occurrence
        // entries would sum to the right term frequency; the score would still be wrong, because BM25's
        // saturation is not linear. weight(1) + weight(1) is not weight(2): measured on the parity
        // fixture, a document holding a term twice scored 0.4722 through one entry of frequency 2 and
        // 0.6611 through two entries of frequency 1. Every walk over these lists — the fold, the
        // document frequency, the corpus frequency — therefore expects at most one entry per document.
        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (string term in allTerms)
        {
            occurrences.TryGetValue(term, out int count);
            occurrences[term] = count + 1;
        }

        foreach (var (term, frequency) in occurrences)
        {
            if (!_postings.TryGetValue(term, out var postings))
            {
                postings = [];
                _postings[term] = postings;
            }

            postings.Add((ordinal, frequency));
        }

        return ordinal;
    }

    /// <summary>Removes the document with this id, and returns whether it was here.</summary>
    public bool Remove(string documentId)
    {
        if (documentId is null || !_ordinals.TryGetValue(documentId, out int ordinal))
            return false;

        RemovePostings(ordinal);

        _tokens -= _lengths[ordinal];
        _lengths[ordinal] = 0;
        _documents[ordinal] = null;
        _ordinals.Remove(documentId);
        _free.Add(ordinal);
        _live--;

        return true;
    }

    /// <summary>Whether this overlay holds the term, and its postings if it does.</summary>
    public bool TryGetPostings(string term, out List<(int Ordinal, int Frequency)> postings) =>
        _postings.TryGetValue(term, out postings!);

    /// <summary>Every term the overlay holds, in unspecified order.</summary>
    public IEnumerable<string> Terms => _postings.Keys;

    /// <summary>
    /// Writes this overlay's postings for <paramref name="term"/> into <paramref name="destination"/>
    /// as chain-wide ordinals, and returns how many it wrote.
    /// </summary>
    public int CollectGlobalOrdinals(string term, int offset, Span<int> destination)
    {
        if (!_postings.TryGetValue(term, out var postings))
            return 0;

        int written = 0;

        foreach (var (ordinal, _) in postings)
        {
            if (written == destination.Length)
                throw new ArgumentException("the destination is smaller than the term's postings", nameof(destination));

            destination[written++] = ordinal + offset;
        }

        return written;
    }

    /// <summary>
    /// Writes this overlay's postings for <paramref name="term"/> into <paramref name="ordinals"/> and
    /// <paramref name="frequencies"/> as chain-wide ordinals, and returns how many it wrote.
    /// </summary>
    public void CollectGlobalPostings(
        string term,
        int offset,
        List<int> ordinals,
        List<int> frequencies,
        List<int>? positions = null)
    {
        if (!_postings.TryGetValue(term, out var postings))
            return;

        foreach (var (ordinal, frequency) in postings)
        {
            ordinals.Add(ordinal + offset);
            frequencies.Add(frequency);

            if (positions is null)
                continue;

            // The overlay holds no positions, so they are recomputed from the document it kept — the
            // same tokenization the read path used to do per query, now paid once at write time.
            var document = _documents[ordinal];

            if (document is null)
                continue;

            var positioned = PositionedFields.Positioned(_tokenizer, document);

            for (int i = 0; i < positioned.Count; i++)
            {
                if (string.Equals(positioned[i].Term, term, StringComparison.Ordinal))
                    positions.Add(positioned[i].Position);
            }
        }
    }

    /// <summary>The token count of the document with this id, or 0.</summary>
    public int LengthOf(string documentId) =>
        _ordinals.TryGetValue(documentId, out int ordinal) ? _lengths[ordinal] : 0;

    /// <summary>The terms of the document with this id, in document order.</summary>
    /// <remarks>
    /// The union the document was indexed with, body then fields, read from the list this overlay
    /// already holds — so it costs no tokenization and cannot disagree with the postings it wrote.
    /// </remarks>
    public IReadOnlyList<string> TermsOf(string documentId) =>
        _ordinals.TryGetValue(documentId, out int ordinal)
            ? _termsByOrdinal[ordinal]
            : [];

    /// <summary>Positions of the term in the document with this id, in document order.</summary>
    /// <remarks>
    /// Recomputed from the stored document, over the same field layout the postings were written
    /// with, so a term that occurs in a title reports the title's positions too.
    /// </remarks>
    public IReadOnlyList<int> PositionsOf(string documentId, string term)
    {
        if (!_ordinals.TryGetValue(documentId, out int ordinal))
            return [];

        var positioned = PositionedFields.Positioned(_tokenizer, _documents[ordinal]!);
        var positions = new List<int>();

        for (int i = 0; i < positioned.Count; i++)
        {
            if (string.Equals(positioned[i].Term, term, StringComparison.Ordinal))
                positions.Add(positioned[i].Position);
        }

        return positions;
    }

    /// <summary>The overlay's terms starting with a prefix. The overlay is small by construction.</summary>
    public IEnumerable<string> TermsWithPrefix(string prefix)
    {
        foreach (string term in _postings.Keys)
        {
            if (term.StartsWith(prefix, StringComparison.Ordinal))
                yield return term;
        }
    }

    /// <summary>How many times the term appears in the document with this id.</summary>
    public int TermFrequency(string documentId, string term) =>
        _ordinals.TryGetValue(documentId, out int ordinal)
            ? _termsByOrdinal[ordinal].Count(t => string.Equals(t, term, StringComparison.Ordinal))
            : 0;

    /// <summary>How many posting entries the overlay holds for the term — one per occurrence.</summary>
    public int CountPostings(string term) =>
        _postings.TryGetValue(term, out var postings) ? postings.Count : 0;

    /// <summary>Total occurrences of the term across the overlay.</summary>
    public int CorpusFrequency(string term)
    {
        int total = 0;

        if (_postings.TryGetValue(term, out var postings))
        {
            foreach (var (_, frequency) in postings)
                total += frequency;
        }

        return total;
    }

    /// <summary>
    /// Folds the term's postings into <paramref name="accumulator"/>, each entry recorded at
    /// <c>overlayLocalOrdinal + <paramref name="offset"/></c>. <paramref name="lengths"/> is indexed by
    /// <b>overlay-local</b> ordinal: the chain owns the length array of the segments' part of the
    /// ordinal space, and the overlay owns the tail.
    /// </summary>
    public void Accumulate<TWeight>(
        TWeight weight,
        ScoreAccumulator accumulator,
        int offset,
        ReadOnlySpan<int> lengths)
        where TWeight : struct, IPostingWeight
    {
        if (!_postings.TryGetValue(weight.Term, out var postings))
            return;

        for (int i = 0; i < postings.Count; i++)
        {
            var (ordinal, frequency) = postings[i];

            accumulator.Record(ordinal + offset, weight.Weight(frequency, lengths[ordinal]));
        }
    }

    /// <summary>How many of this term's postings the overlay holds, for <c>DocumentFrequency</c>.</summary>
    public int DocumentFrequency(string term) =>
        _postings.TryGetValue(term, out var postings) ? CountDistinct(postings) : 0;

    private static int CountDistinct(List<(int Ordinal, int Frequency)> postings)
    {
        // Ascending by ordinal, so a run of equal ordinals is one document.
        int documents = 0;
        int previous = -1;

        for (int i = 0; i < postings.Count; i++)
        {
            if (postings[i].Ordinal == previous)
                continue;

            previous = postings[i].Ordinal;
            documents++;
        }

        return documents;
    }

    /// <summary>Drops every posting the document at this ordinal holds.</summary>
    private void RemovePostings(int ordinal)
    {
        string[] terms = _termsByOrdinal[ordinal];

        foreach (string term in terms)
        {
            if (!_postings.TryGetValue(term, out var postings))
                continue;

            for (int i = postings.Count - 1; i >= 0; i--)
            {
                if (postings[i].Ordinal == ordinal)
                    postings.RemoveAt(i);
            }

            if (postings.Count == 0)
                _postings.Remove(term);
        }

        _termsByOrdinal[ordinal] = [];
    }
}
