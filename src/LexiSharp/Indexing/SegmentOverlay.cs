using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
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
/// and a dictionary write, not the about 0.9 s and 126 MB a segment write costs at 40,000 documents
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

    /// <summary>
    /// Every term's positions, in the same order as that term's <see cref="_postings"/> entries: entry
    /// <c>i</c> owns the next <c>Frequencies[i]</c> of them.
    /// </summary>
    /// <remarks>
    /// Term-major, because a segment write is: the writer resolves one term at a time and wants that
    /// term's positions. Kept document-major instead — the natural shape when a document is added — the
    /// write's transpose became a scan of every document once per term it holds, and measured at 20,632
    /// ms of CPU for one 100,000-document commit against 948 ms from a segment: 22x, and two thirds of
    /// the commit. The two layouts hold the same values; this is the one the reader asks for.
    /// </remarks>
    private readonly Dictionary<string, TermPositions> _positionsByTerm = new(StringComparer.Ordinal);

    /// <summary>
    /// Looks a term up without materializing it, so a document's tokens cost a reference each rather
    /// than a string each.
    /// </summary>
    /// <remarks>
    /// The tokenizer allocates a string per token and this store keeps one per distinct term, so the
    /// other 12.3M of a 100,000-document build were created and immediately dropped — measured at
    /// 69.81% of the build's CPU in the collector. A term is materialized here only when it is new,
    /// which is once per vocabulary entry.
    /// </remarks>
    private readonly Dictionary<string, TermPositions>.AlternateLookup<ReadOnlySpan<char>> _spanPositions;

    /// <summary>The destination one run's normalized terms are written into, grown as needed.</summary>
    private NormalizedTerm[] _normalized = new NormalizedTerm[256];

    /// <summary>
    /// One term's state in the overlay: its positions, and where the document being added stopped.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The positions are term-major and in the same order as <see cref="_postings"/>' entries for the
    /// term, so a look-up is a slice. <see cref="CurrentFrequency"/> is the count for the document
    /// <see cref="LastOrdinal"/> names, which is what lets <see cref="Add"/> write that document's
    /// posting entry without a second pass over its terms.
    /// </para>
    /// <para>
    /// Before this, <c>Add</c> allocated a <c>Dictionary&lt;string, int&gt;</c> per document to count
    /// them, filled it with a hashed insert per token, and walked the document's terms again to read it
    /// back. Measured at 100,000 documents that was 11,912 ms of CPU against 904 ms for tokenizing the
    /// same corpus: 93% of building an overlay was the bookkeeping around the tokens, and most of it was
    /// that dictionary.
    /// </para>
    /// </remarks>
    private sealed class TermPositions(string term)
    {
        /// <summary>The term, so a touched entry can be written without looking it up again.</summary>
        public string Term { get; } = term;

        /// <summary>The term's positions, ascending within a document and grouped by document.</summary>
        public List<int> Positions { get; } = [];

        /// <summary>The last document this term was seen in, or <c>-1</c>.</summary>
        public int LastOrdinal { get; set; } = -1;

        /// <summary>How many times the term occurs in <see cref="LastOrdinal"/>.</summary>
        public int CurrentFrequency { get; set; }
    }
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
        _spanPositions = _positionsByTerm.GetAlternateLookup<ReadOnlySpan<char>>();
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
        // The document's tokens, in order — _termsByOrdinal reads it — and the entries this document
        // touched, so the posting entries below need no look-up at all. Both are arrays sized by the
        // tokenization rather than lists that grow: a list and its ToArray were two allocations per
        // document for one array's worth of values, 200,000 of them over a 100,000-document build.
        if (!AddSpans(document, ordinal, out string[] allTerms, out TermPositions[] touched, out int touchedCount))
            AddStrings(document, ordinal, out allTerms, out touched, out touchedCount);

        _lengths[ordinal] = allTerms.Length;
        _tokens += allTerms.Length;

        // The index's shared instances, not the tokenizer's: the array holds the entries' own strings,
        // so it is the document's term list rather than a copy of it.
        _termsByOrdinal[ordinal] = allTerms;

        // One entry per (term, document), carrying the number of occurrences — not one entry per
        // occurrence. The accumulator's first touch writes and every later touch adds, so per-occurrence
        // entries would sum to the right term frequency; the score would still be wrong, because BM25's
        // saturation is not linear. weight(1) + weight(1) is not weight(2): measured on the parity
        // fixture, a document holding a term twice scored 0.4722 through one entry of frequency 2 and
        // 0.6611 through two entries of frequency 1. Every walk over these lists — the fold, the
        // document frequency, the corpus frequency — therefore expects at most one entry per document.
        for (int i = 0; i < touchedCount; i++)
        {
            var entry = touched[i];

            if (!_postings.TryGetValue(entry.Term, out var postings))
            {
                postings = [];
                _postings[entry.Term] = postings;
            }

            postings.Add((ordinal, entry.CurrentFrequency));
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
    public bool TryGetPostings(string term, [NotNullWhen(true)] out List<(int Ordinal, int Frequency)>? postings) =>
        _postings.TryGetValue(term, out postings);

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

        if (positions is null)
        {
            foreach (var (ordinal, frequency) in postings)
            {
                ordinals.Add(ordinal + offset);
                frequencies.Add(frequency);
            }

            return;
        }

        // The positions are already this term's, in entry order: the copy is the look-up.
        var termPositions = _positionsByTerm[term].Positions;
        int at = 0;

        foreach (var (ordinal, frequency) in postings)
        {
            ordinals.Add(ordinal + offset);
            frequencies.Add(frequency);

            for (int k = 0; k < frequency; k++)
                positions.Add(termPositions[at++]);
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
    /// <summary>
    /// Records the document's tokens through the span tokenizer, which names each term as a slice of the
    /// text it came from. Returns <c>false</c> when the analysis cannot be served that way — a stemmer,
    /// a stop list, an n-gram pass — and writes nothing, so the caller can use the string path instead.
    /// </summary>
    /// <remarks>
    /// The run layout is the string path's: the body at position zero, then each text field after
    /// <see cref="TextFields.PositionGap"/>. It is asserted against the string path rather than assumed —
    /// see SpanTokenizationParityTests — because a stored position that disagrees with the one
    /// GetTermPositions recomputes is a store that answers a different question than it indexed.
    /// </remarks>
    private bool AddSpans(
        SearchDocument document,
        int ordinal,
        out string[] allTerms,
        out TermPositions[] touched,
        out int touchedCount)
    {
        allTerms = [];
        touched = [];
        touchedCount = 0;

        if (_tokenizer is not ISpanTokenizer tokenizer)
            return false;

        var fields = document.TextFields is { Count: > 0 } present ? present : null;

        // Sized for the whole document: a run's term count cannot exceed the characters it came from, so
        // with this destination no run can overflow and -1 can only mean the analysis declines.
        int widest = document.Text.Length;

        if (fields is not null)
        {
            foreach (var (_, text) in fields)
                widest += text.Length;
        }

        if (_normalized.Length < widest + 8)
            _normalized = new NormalizedTerm[Math.Max(widest + 8, _normalized.Length * 2)];

        // Probed before anything is recorded. The decline is not a property of the analysis — a tokenizer
        // may serve one text and not another — so every run is asked first, and the counts it gives size
        // the arrays the terms go into.
        int bodyCount = CountSpans(tokenizer, document.Text, _normalized);

        if (bodyCount < 0)
            return false;

        int total = bodyCount;

        if (fields is not null)
        {
            foreach (var (_, text) in fields)
            {
                int count = CountSpans(tokenizer, text, _normalized);

                if (count < 0)
                    return false;

                total += count;
            }
        }

        allTerms = new string[total];
        touched = new TermPositions[total];

        int at = 0;

        AppendSpans(tokenizer, document.Text.AsSpan(), 0, ordinal, allTerms, touched, ref at, ref touchedCount);

        int next = bodyCount;

        if (fields is null)
            return true;

        foreach (var (field, text) in fields)
        {
            TextFields.Validate(field, nameof(document));

            next += TextFields.PositionGap;

            AppendSpans(tokenizer, text.AsSpan(), next, ordinal, allTerms, touched, ref at, ref touchedCount);
            next += CountSpans(tokenizer, text, _normalized);
        }

        return true;
    }

    /// <summary>The term's entry, materializing its string only when it is new to the vocabulary.</summary>
    private TermPositions EntryFor(ReadOnlySpan<char> term)
    {
        if (_spanPositions.TryGetValue(term, out var entry))
            return entry;

        // Once per distinct term, not once per token: this is where the 12.3M dropped strings were.
        string created = term.ToString();
        entry = new TermPositions(created);
        _positionsByTerm[created] = entry;

        return entry;
    }

    /// <summary>
    /// Appends one run's terms at <paramref name="start"/>.
    /// </summary>
    /// <remarks>
    /// Cannot decline: the caller probes the analysis and sizes the destinations for the whole document
    /// before calling this, so nothing here can leave the overlay half-written.
    /// </remarks>
    private void AppendSpans(
        ISpanTokenizer tokenizer,
        ReadOnlySpan<char> text,
        int start,
        int ordinal,
        string[] allTerms,
        TermPositions[] touched,
        ref int at,
        ref int touchedCount)
    {
        int count = CountSpans(tokenizer, text, _normalized);

        for (int i = 0; i < count; i++)
        {
            var term = _normalized[i];

            // The materialized string is the rare case; a term whose normalized form is a slice is
            // looked up without one, and materialized only when it is new to the vocabulary.
            TermPositions entry;

            if (term.Materialized is { } materialized)
            {
                // The out is nullable and read in the branch the lookup returned true in, so
                // neither the declaration nor the read needs a suppression.
                if (!_positionsByTerm.TryGetValue(materialized, out TermPositions? known))
                {
                    entry = new TermPositions(materialized);
                    _positionsByTerm[materialized] = entry;
                }
                else
                {
                    entry = known;
                }
            }
            else
            {
                entry = EntryFor(text.Slice(term.Start, term.Length));
            }

            allTerms[at++] = entry.Term;

            if (entry.LastOrdinal != ordinal)
            {
                entry.LastOrdinal = ordinal;
                entry.CurrentFrequency = 0;
                touched[touchedCount++] = entry;
            }

            entry.Positions.Add(start + i);
            entry.CurrentFrequency++;
        }
    }

    private static int CountSpans(ISpanTokenizer tokenizer, ReadOnlySpan<char> text, NormalizedTerm[] destination)
    {
        int count = tokenizer.TokenizeNormalized(text, destination);

        return count;
    }

    /// <summary>The string path, for an analysis the span tokenizer declines.</summary>
    private void AddStrings(
        SearchDocument document,
        int ordinal,
        out string[] allTerms,
        out TermPositions[] touched,
        out int touchedCount)
    {
        var runs = PositionedFields.Runs(_tokenizer, document);

        int total = 0;

        foreach (var run in runs)
            total += run.Terms.Count;

        allTerms = new string[total];
        touched = new TermPositions[total];

        int at = 0;
        touchedCount = 0;

        foreach (var run in runs)
        {
            for (int i = 0; i < run.Terms.Count; i++)
            {
                string term = run.Terms[i];

                if (!_positionsByTerm.TryGetValue(term, out var entry))
                {
                    entry = new TermPositions(term);
                    _positionsByTerm[term] = entry;
                }

                allTerms[at++] = entry.Term;

                if (entry.LastOrdinal != ordinal)
                {
                    entry.LastOrdinal = ordinal;
                    entry.CurrentFrequency = 0;
                    touched[touchedCount++] = entry;
                }

                entry.Positions.Add(run.Start + i);
                entry.CurrentFrequency++;
            }
        }
    }

    private void RemovePostings(int ordinal)
    {
        string[] terms = _termsByOrdinal[ordinal];

        foreach (string term in terms)
        {
            if (!_postings.TryGetValue(term, out var postings))
                continue;

            for (int i = postings.Count - 1; i >= 0; i--)
            {
                if (postings[i].Ordinal != ordinal)
                    continue;

                // The entry's positions are the next Frequency of them, after every entry before it.
                int start = 0;

                for (int k = 0; k < i; k++)
                    start += postings[k].Frequency;

                _positionsByTerm[term].Positions.RemoveRange(start, postings[i].Frequency);
                postings.RemoveAt(i);
            }

            if (postings.Count == 0)
            {
                _postings.Remove(term);
                _positionsByTerm.Remove(term);
            }
        }

        _termsByOrdinal[ordinal] = [];
    }
}
