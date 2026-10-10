using System;
using System.Buffers;
using System.Collections.Generic;
using LexiSharp.Core;
using LexiSharp.Linguistics;

namespace LexiSharp.Indexing;

/// <summary>
/// A chain of segments over one ordinal space, with a live overlay: several immutable generations and
/// the documents added since the last commit, read as one index.
/// </summary>
/// <remarks>
/// <para>
/// A storage a search server must write to cannot be one segment, because a segment is written once and
/// read many times. The chain is the shape that fixes that without touching the format: segment
/// <c>i</c> owns the global ordinals <c>[base[i], base[i] + count[i])</c>, the overlay owns the
/// ordinals after the last segment, and a fold walks the segments oldest-first then the overlay, each
/// at its base, into one accumulator. Every document in the whole chain then holds exactly one ordinal
/// and receives each query term's contribution exactly once — so the page a chain serves is
/// <b>bit-identical to the in-memory index's</b> for the same corpus, which is the property the whole
/// design rests on. It is asserted rather than assumed.
/// </para>
/// <para>
/// <b>A tombstone is an ordinal, and it touches nothing but the fold.</b> The corpus statistics —
/// <see cref="StatisticDocumentCount"/>, <see cref="AverageDocumentLength"/>,
/// <see cref="CorpusTokenCount"/>, <see cref="DocumentFrequency"/>, <see cref="VocabularySize"/> —
/// describe the segments as they were written and are <b>frozen until a merge</b>. That is not a
/// limitation, it is the only rule bit-exact parity is possible under: BM25's idf reads <c>N</c> and
/// its length normalisation reads the average, so any statistic a deletion touches moves every score on
/// the index. Measured, the same deletions over the same segment: <b>frozen statistics left 0 of 401
/// scores moved, corrected statistics moved 401 of 401</b>, at four deletion rates. What a tombstone
/// does change is <see cref="Count"/> (the number <see cref="Documents"/> enumerates and the engine's
/// empty-index check reads) and the fold, which skips the dead ordinal so the document never enters the
/// accumulator. A merge rewrites the segment and the statistics move then, once, in a step the caller
/// schedules.
/// </para>
/// <para>
/// <b>What a tombstone costs, measured in the library rather than in a prototype.</b> A per-entry
/// membership test in the fold makes the head query <b>1.18× slower</b> at 400 deleted documents of
/// 40,000 (1,158.7 µs against 980.3 µs), and the tax *falls* as the deletion rate rises — 1.10× at
/// 2,000, 1.07× at 4,000, and <b>0.95× at 10,000</b>, i.e. slightly faster than no tombstones at all,
/// because a skipped posting is a <see cref="ScoreAccumulator.Record"/> the accumulator never does.
/// </para>
/// <para>
/// <b>The number above is a correction.</b> An earlier prototype measured the same protocol on the
/// same corpus and host and reported <b>2.1-2.5×</b> at these same deletion rates. The library measures
/// 1.18× and falling. Two runs of the library's own probe agree within 1-2 %; the prototype was taken
/// on a host at load 1.5-2.2 and its p95 spread was 1,677-3,048 µs against a 1,054 µs median, which is
/// the signature of a machine that was not at rest. <b>The mechanism of the difference is not
/// established</b>, and the figure that stands is the library's, because it is the one that is
/// reproducible here and the one the code actually implements.
/// </para>
/// <para>
/// <b>The per-block window bitmap was tried and bought nothing</b>, because at 10 ‰ nearly every
/// 128-ordinal window already holds a dead ordinal. It is not in the code.
/// </para>
/// <para>
/// <b>What an overlay costs to read, also corrected.</b> The prototype reported an overlay of 100
/// documents at <b>3.83×</b> and 10,000 at <b>32.4×</b>, because its fold tokenized each document to
/// find its length, per posting entry. The library reads a length array instead, and measures
/// <b>1.02× at 100, 1.02× at 1,000 and 1.05× at 10,000 documents</b> — an overlay is nearly free to
/// read. The commit threshold is therefore not a read-cost decision, which is what the earlier plan
/// said it was; it is a durability and memory one.
/// </para>
/// <para>
/// Documents are resolved lazily, and that is not incidental. Decoding every document when a chain
/// opens costs <b>34-100 ms against 3.6-10.2 ms</b> for the id to ordinal map and the length array at
/// 40,000 documents — a factor of 10-14 that grows with the segment count. A chain pays the cheap
/// half at open and the expensive half only for the rows a page actually keeps.
/// </para>
/// <para>
/// <b>Not yet, and what each omission costs:</b> no merge — the chain stays as long as it is opened
/// as, and the tombstone tax stays with it; no durability — the overlay is lost on a crash, which is
/// the reason a <c>Commit</c> exists.
/// </para>
/// <para>
/// <b>One writer, any number of readers.</b> The segments are immutable and a writer builds a new
/// overlay rather than mutating a shared one, so a search in flight sees a consistent corpus. This is
/// the same contract <c>InMemoryTextIndex</c> documents.
/// </para>
/// <para>
/// <b>One reader at a time over a source-backed segment.</b> A <see cref="SegmentReader"/> that cannot
/// view its bytes fetches blocks into its own scratch buffer, so two walks interleaved over the same
/// segment would overwrite each other. The chain folds its segments one at a time and never
/// interleaves them.
/// </para>
/// </remarks>
internal sealed class SegmentChain : IReadOnlyTextIndex, IAccumulatingIndex, ICandidateIndex
{
    private readonly SegmentTextIndex[] _segments;
    private readonly int[] _bases;
    private readonly int _segmentOrdinalSpace;
    private readonly int[] _lengths;
    private readonly long _segmentTokens;
    private readonly HashSet<int> _dead = [];
    private readonly SegmentOverlay _overlay;

    /// <param name="segments">
    /// The chain's generations, oldest first. Each segment's ordinal space is appended to the previous
    /// one's, so a chain of two 20,000-document segments owns the global ordinals <c>[0, 40000)</c>.
    /// </param>
    /// <param name="tokenizer">
    /// The tokenizer the segments were written with. It is what the overlay tokenizes new documents
    /// with, and it must be the same one — the dictionary holds normalized terms, so a lookup is by the
    /// normalized form.
    /// </param>
    public SegmentChain(IReadOnlyList<SegmentTextIndex> segments, ITokenizer? tokenizer = null)
    {
        ArgumentNullException.ThrowIfNull(segments);

        if (segments.Count == 0)
            throw new ArgumentException("a chain holds at least one segment", nameof(segments));

        _segments = new SegmentTextIndex[segments.Count];
        _bases = new int[segments.Count];
        _segmentOrdinalSpace = 0;

        for (int i = 0; i < segments.Count; i++)
        {
            ArgumentNullException.ThrowIfNull(segments[i]);

            _segments[i] = segments[i];
            _bases[i] = _segmentOrdinalSpace;
            _segmentOrdinalSpace += segments[i].Count;
        }

        _lengths = new int[_segmentOrdinalSpace];

        for (int i = 0; i < _segments.Length; i++)
            _segments[i].CopyLengthsInto(_lengths, _bases[i]);

        long tokens = 0;

        for (int ordinal = 0; ordinal < _segmentOrdinalSpace; ordinal++)
            tokens += _lengths[ordinal];

        _segmentTokens = tokens;
        _overlay = new SegmentOverlay(tokenizer);
    }

    /// <summary>Segments in the chain, oldest first.</summary>
    public int SegmentCount => _segments.Length;

    /// <summary>
    /// The chain's segments, oldest first. Internal for the tests that assert a chain inherits a
    /// segment's capabilities rather than reimplementing them.
    /// </summary>
    internal IReadOnlyList<SegmentTextIndex> SegmentsForTest => _segments;

    /// <summary>Documents removed from the segments and not yet merged away.</summary>
    public int TombstoneCount => _dead.Count;

    /// <summary>Documents added since the last commit.</summary>
    public int OverlayCount => _overlay.Count;

    /// <summary>
    /// Adds a document, or replaces one the chain already holds.
    /// </summary>
    /// <remarks>
    /// Replacing a document a segment holds leaves the segment's bytes alone and tombstones the ordinal
    /// it occupies, then writes the new version into the overlay — the same upsert the in-memory index
    /// performs, with the segment immutable underneath it.
    /// </remarks>
    public void Add(SearchDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        // A replaced document must stop matching its old text, so its ordinal is tombstoned before the
        // new version is indexed — otherwise both versions would score.
        if (((IAccumulatingIndex)this).OrdinalOf(document.Id) is int existing and >= 0 && existing < _segmentOrdinalSpace)
            _dead.Add(existing);

        _overlay.Add(document);
    }

    /// <summary>Removes the document with this id, and returns whether it was held.</summary>
    public bool Remove(string documentId)
    {
        ArgumentNullException.ThrowIfNull(documentId);

        if (_overlay.Remove(documentId))
            return true;

        if (((IAccumulatingIndex)this).OrdinalOf(documentId) is int ordinal and >= 0 && ordinal < _segmentOrdinalSpace)
        {
            _dead.Add(ordinal);

            return true;
        }

        return false;
    }

    /// <inheritdoc />
    /// <remarks>
    /// The live count: the segments' documents minus the tombstoned ones, plus the overlay's. This is
    /// the number <see cref="Documents"/> enumerates and the engine's empty-index check reads.
    /// </remarks>
    public int Count => _segmentOrdinalSpace - _dead.Count + _overlay.Count;

    /// <inheritdoc />
    /// <remarks>
    /// Segments and overlay, tombstoned documents included — the frozen statistics rule. Only
    /// <see cref="Count"/> reports the live number.
    /// </remarks>
    public int StatisticDocumentCount => _segmentOrdinalSpace + _overlay.Count;

    /// <inheritdoc />
    public int VocabularySize => CountDistinctTerms();

    /// <inheritdoc />
    public long CorpusTokenCount => _segmentTokens + _overlay.TokenCount;

    /// <inheritdoc />
    public double AverageDocumentLength
    {
        get
        {
            int documents = StatisticDocumentCount;

            return documents == 0 ? 0 : (double)CorpusTokenCount / documents;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Enumerated on demand rather than materialized: a chain that decoded its whole corpus here would
    /// pay the 34-100 ms the laziness exists to avoid.
    /// </remarks>
    public IReadOnlyCollection<SearchDocument> Documents => new ChainDocumentView(this);

    /// <summary>
    /// Every distinct term across the segments and the overlay, counted.
    /// </summary>
    /// <remarks>
    /// A term held by two segments is one term, so the count is the size of the union and not the sum.
    /// The segments' dictionaries are each sorted by code point, so the union is a merge rather than a
    /// set — nothing proportional to the vocabulary is retained.
    /// </remarks>
    private int CountDistinctTerms()
    {
        var cursors = new IEnumerator<string>[_segments.Length];
        var current = new string?[_segments.Length];
        int distinct = 0;
        string? last = null;

        try
        {
            for (int i = 0; i < _segments.Length; i++)
            {
                cursors[i] = _segments[i].Terms.GetEnumerator();
                current[i] = cursors[i].MoveNext() ? cursors[i].Current : null;
            }

            while (true)
            {
                // The smallest term any cursor holds; the ties are taken together, which is the dedup.
                string? smallest = null;

                for (int i = 0; i < current.Length; i++)
                {
                    if (current[i] is null)
                        continue;

                    if (smallest is null || string.CompareOrdinal(current[i], smallest) < 0)
                        smallest = current[i];
                }

                if (smallest is null)
                    break;

                if (last is null || string.CompareOrdinal(smallest, last) != 0)
                {
                    distinct++;
                    last = smallest;
                }

                for (int i = 0; i < current.Length; i++)
                {
                    if (current[i] is not null && string.CompareOrdinal(current[i], smallest) == 0)
                        current[i] = cursors[i].MoveNext() ? cursors[i].Current : null;
                }
            }

            // The overlay's terms are not in the segments' order, so they are sorted and merged in
            // separately: two of them may be the same term, and a term may already have been counted.
            var overlayTerms = new List<string>(_overlay.Terms);
            overlayTerms.Sort(StringComparer.Ordinal);

            foreach (string term in overlayTerms)
            {
                if (last is not null && string.CompareOrdinal(term, last) <= 0)
                    continue;

                distinct++;
                last = term;
            }

            return distinct;
        }
        finally
        {
            foreach (var cursor in cursors)
                cursor?.Dispose();
        }
    }

    /// <inheritdoc />
    int IAccumulatingIndex.OrdinalSpace => StatisticDocumentCount;

    /// <inheritdoc />
    SearchDocument? IAccumulatingIndex.DocumentAt(int ordinal)
    {
        if (ordinal < 0 || ordinal >= StatisticDocumentCount)
            return null;

        if (ordinal >= _segmentOrdinalSpace)
            return _overlay.DocumentAt(ordinal - _segmentOrdinalSpace);

        if (_dead.Contains(ordinal))
            return null;

        int segment = SegmentIndexAt(ordinal);

        return _segments[segment].DocumentAt(ordinal - _bases[segment]);
    }

    /// <inheritdoc />
    int IAccumulatingIndex.OrdinalOf(string documentId)
    {
        // Oldest generation first, so the same id resolves to the ordinal the fold recorded it at.
        for (int i = 0; i < _segments.Length; i++)
        {
            int ordinal = _segments[i].OrdinalOf(documentId);

            if (ordinal < 0)
                continue;

            int global = _bases[i] + ordinal;

            // A replaced document is tombstoned at its old ordinal and lives in the overlay, so a dead
            // segment ordinal is not the answer — the overlay's is.
            if (!_dead.Contains(global))
                return global;
        }

        int overlayOrdinal = _overlay.OrdinalOf(documentId);

        return overlayOrdinal < 0 ? -1 : _segmentOrdinalSpace + overlayOrdinal;
    }

    /// <inheritdoc />
    /// <remarks>
    /// The whole read path: one accumulator over the chain's ordinal space, one fold per segment in
    /// generation order, each at its base, then the overlay. The tombstone test is per posting entry;
    /// see the type's remarks for the measured cost and why the cheaper-looking alternatives do not
    /// help. The segments are not interleaved, which is what keeps one reader per segment safe.
    /// </remarks>
    void IAccumulatingIndex.Accumulate<TWeight>(TWeight weight, ScoreAccumulator accumulator)
    {
        var lengths = _lengths;
        var dead = _dead;

        for (int i = 0; i < _segments.Length; i++)
        {
            var segment = _segments[i];

            if (dead.Count == 0)
            {
                // The common case, and the fast one: no test in the entry walk at all.
                segment.Accumulate(weight, accumulator, _bases[i], lengths);

                continue;
            }

            segment.Accumulate(weight, accumulator, _bases[i], lengths, dead);
        }

        _overlay.Accumulate(weight, accumulator, _segmentOrdinalSpace, OverlayLengths());
    }

    /// <summary>
    /// The overlay's lengths, indexed by overlay-local ordinal. Allocated per accumulation because the
    /// overlay is small by construction — the commit threshold bounds it — and a cached array would
    /// have to be invalidated on every write.
    /// </summary>
    private int[] OverlayLengths()
    {
        var lengths = new int[_overlay.OrdinalCapacity];

        for (int ordinal = 0; ordinal < _overlay.OrdinalCapacity; ordinal++)
            lengths[ordinal] = _overlay.LengthAt(ordinal);

        return lengths;
    }

    /// <inheritdoc />
    public bool Contains(string documentId)
    {
        for (int i = 0; i < _segments.Length; i++)
        {
            int ordinal = _segments[i].OrdinalOf(documentId);

            if (ordinal < 0)
                continue;

            if (!_dead.Contains(_bases[i] + ordinal))
                return true;
        }

        return _overlay.OrdinalOf(documentId) >= 0;
    }

    /// <inheritdoc />
    public int DocumentFrequency(string term)
    {
        int total = 0;

        // The segments' own frequencies, tombstoned documents included: the frozen statistics rule.
        foreach (var segment in _segments)
            total += segment.DocumentFrequency(term);

        return total + _overlay.DocumentFrequency(term);
    }

    /// <inheritdoc />
    public int CorpusFrequency(string term)
    {
        int total = 0;

        foreach (var segment in _segments)
            total += segment.CorpusFrequency(term);

        return total + _overlay.CorpusFrequency(term);
    }

    /// <inheritdoc />
    public int TermFrequency(string documentId, string term)
    {
        for (int i = 0; i < _segments.Length; i++)
        {
            if (!_segments[i].Contains(documentId))
                continue;

            // A replaced document is tombstoned in its segment and lives in the overlay, so a dead
            // ordinal is not the end of the question — the overlay's answer is. Returning 0 here would
            // make the scorer see a document that carries nothing at all.
            if (_dead.Contains(_bases[i] + _segments[i].OrdinalOf(documentId)))
                break;

            return _segments[i].TermFrequency(documentId, term);
        }

        return _overlay.TermFrequency(documentId, term);
    }

    /// <inheritdoc />
    public int DocumentLength(string documentId)
    {
        for (int i = 0; i < _segments.Length; i++)
        {
            int ordinal = _segments[i].OrdinalOf(documentId);

            if (ordinal < 0)
                continue;

            int global = _bases[i] + ordinal;

            // A replaced document's length is the new version's, which lives in the overlay.
            return _dead.Contains(global) ? _overlay.LengthOf(documentId) : _lengths[global];
        }

        return _overlay.LengthOf(documentId);
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetTerms(string documentId)
    {
        for (int i = 0; i < _segments.Length; i++)
        {
            if (!_segments[i].Contains(documentId))
                continue;

            if (_dead.Contains(_bases[i] + _segments[i].OrdinalOf(documentId)))
                break;

            return _segments[i].GetTerms(documentId);
        }

        return _overlay.TermsOf(documentId);
    }

    /// <inheritdoc />
    public IReadOnlyList<int> GetTermPositions(string documentId, string term)
    {
        for (int i = 0; i < _segments.Length; i++)
        {
            if (!_segments[i].Contains(documentId))
                continue;

            if (_dead.Contains(_bases[i] + _segments[i].OrdinalOf(documentId)))
                break;

            return _segments[i].GetTermPositions(documentId, term);
        }

        return _overlay.PositionsOf(documentId, term);
    }

    /// <inheritdoc />
    public bool TryGetDocument(string documentId, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out SearchDocument? document)
    {
        // Oldest generation first, so the same id resolves to the ordinal the fold recorded it at.
        for (int i = 0; i < _segments.Length; i++)
        {
            int ordinal = _segments[i].OrdinalOf(documentId);

            if (ordinal < 0)
                continue;

            if (!_dead.Contains(_bases[i] + ordinal))
                return (document = _segments[i].DocumentAt(ordinal)) is not null;
        }

        if (_overlay.OrdinalOf(documentId) >= 0)
            return (document = _overlay.DocumentAt(_overlay.OrdinalOf(documentId))) is not null;

        document = null;

        return false;
    }

    /// <inheritdoc />
    IEnumerable<SearchDocument> ICandidateIndex.GetCandidateDocuments(IReadOnlyList<string> terms)
    {
        ArgumentNullException.ThrowIfNull(terms);

        if (terms.Count == 0 || Count == 0)
            return [];

        return EnumerateCandidates(terms);
    }

    /// <summary>
    /// The union of the query terms' postings across every segment and the overlay, each document once,
    /// in ascending ordinal order — which is corpus order, because the chain's ordinal ranges are
    /// contiguous and every document lives in exactly one of them.
    /// </summary>
    private IEnumerable<SearchDocument> EnumerateCandidates(IReadOnlyList<string> terms)
    {
        // The union cannot exceed the sum of the terms' document frequencies, and never the corpus.
        int capacity = 0;

        for (int i = 0; i < terms.Count; i++)
            capacity += DocumentFrequency(terms[i]);

        if (capacity == 0)
            yield break;

        int[] ordinals = ArrayPool<int>.Shared.Rent(capacity);
        int count = 0;

        try
        {
            for (int i = 0; i < terms.Count; i++)
            {
                for (int s = 0; s < _segments.Length; s++)
                    count += _segments[s].CollectGlobalOrdinals(terms[i], _bases[s], ordinals.AsSpan(count));

                count += _overlay.CollectGlobalOrdinals(
                    terms[i],
                    _segmentOrdinalSpace,
                    ordinals.AsSpan(count));
            }

            Array.Sort(ordinals, 0, count);

            int previous = -1;

            for (int i = 0; i < count; i++)
            {
                // A document holding two query terms is in both lists and must be yielded once.
                if (ordinals[i] == previous)
                    continue;

                previous = ordinals[i];

                if (((IAccumulatingIndex)this).DocumentAt(ordinals[i]) is { } document)
                    yield return document;
            }
        }
        finally
        {
            ArrayPool<int>.Shared.Return(ordinals);
        }
    }

    /// <summary>The segment owning a global ordinal: the last whose base is at or below it.</summary>
    private int SegmentIndexAt(int ordinal)
    {
        int low = 0;
        int high = _segments.Length - 1;

        while (low < high)
        {
            int middle = (low + high + 1) / 2;

            if (_bases[middle] <= ordinal)
                low = middle;
            else
                high = middle - 1;
        }

        return low;
    }

    /// <summary>A live view over the chain's ordinal space: no document is decoded until it is asked for.</summary>
    private sealed class ChainDocumentView(SegmentChain owner) : IReadOnlyCollection<SearchDocument>
    {
        public int Count => owner.Count;

        public IEnumerator<SearchDocument> GetEnumerator()
        {
            for (int ordinal = 0; ordinal < owner.StatisticDocumentCount; ordinal++)
            {
                if (((IAccumulatingIndex)owner).DocumentAt(ordinal) is { } document)
                    yield return document;
            }
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
