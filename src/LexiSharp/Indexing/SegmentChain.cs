using System;
using System.Buffers;
using System.Collections.Generic;
using LexiSharp.Core;
using LexiSharp.Linguistics;

namespace LexiSharp.Indexing;

/// <summary>
/// A chain of segments over one ordinal space: several immutable generations read as a single index.
/// </summary>
/// <remarks>
/// <para>
/// A storage a search server must write to cannot be one segment, because a segment is written once and
/// read many times. The chain is the shape that fixes that without touching the format: segment
/// <c>i</c> owns the global ordinals <c>[base[i], base[i] + count[i])</c>, and a fold walks the
/// segments oldest-first, each at its base, into one accumulator. Every document in the whole chain then
/// holds exactly one ordinal and receives each query term's contribution exactly once — so the page a
/// chain serves is <b>bit-identical to the in-memory index's</b> for the same corpus, which is the
/// property the whole design rests on. It is asserted rather than assumed.
/// </para>
/// <para>
/// Documents are resolved lazily, and that is not incidental. Decoding every document when a chain
/// opens costs <b>34-100 ms against 3.6-10.2 ms</b> for the id to ordinal map and the length array at
/// 40,000 documents — a factor of 10-14 that grows with the segment count. A chain pays the cheap half
/// at open and the expensive half only for the rows a page actually keeps.
/// </para>
/// <para>
/// What a chain does not yet do, and what each omission costs:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>No writes.</b> The segments are immutable and the chain has no overlay, so there is nothing to
/// add or remove yet. The seam is here — a fold, an ordinal space, a resolution path — and the overlay
/// lands on it next.
/// </description></item>
/// <item><description>
/// <b>No tombstones.</b> Without a tombstone the chain is a read-only view over a whole corpus; a
/// deletion needs its own record, because the segments' bytes describing the deleted document stay
/// where they are.
/// </description></item>
/// <item><description>
/// <b>No merge.</b> The chain stays as long as it is opened as; a merge rewrites the ordinals and is
/// scheduled by a caller, never by a query.
/// </description></item>
/// </list>
/// <para>
/// The read order is <b>generation order, oldest first</b>, and it is load-bearing: changing it changes
/// the float rounding of every sum, and the parity tests would fail on the last bits rather than on the
/// ids. Ordinals within a segment ascend, and the bases are contiguous, so a fold writes to the
/// accumulator in ascending ordinal order across the whole chain — the same shape
/// <see cref="SegmentTextIndex"/> produces alone.
/// </para>
/// <para>
/// <b>One reader at a time over a source-backed segment.</b> A <see cref="SegmentReader"/> that cannot
/// view its bytes fetches blocks into its own scratch buffer, so two walks interleaved over the same
/// segment would overwrite each other. The chain folds its segments one at a time and never
/// interleaves them, which is what makes one reader per segment safe.
/// </para>
/// </remarks>
internal sealed class SegmentChain : IReadOnlyTextIndex, IAccumulatingIndex, ICandidateIndex
{
    private readonly SegmentTextIndex[] _segments;
    private readonly int[] _bases;
    private readonly int _ordinalSpace;
    private readonly int[] _lengths;
    private readonly long _tokens;

    /// <param name="segments">
    /// The chain's generations, oldest first. Each segment's ordinal space is appended to the previous
    /// one's, so a chain of two 20,000-document segments owns the global ordinals <c>[0, 40000)</c>.
    /// </param>
    public SegmentChain(IReadOnlyList<SegmentTextIndex> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);

        if (segments.Count == 0)
            throw new ArgumentException("a chain holds at least one segment", nameof(segments));

        _segments = new SegmentTextIndex[segments.Count];
        _bases = new int[segments.Count];
        _ordinalSpace = 0;
        _tokens = 0;

        for (int i = 0; i < segments.Count; i++)
        {
            ArgumentNullException.ThrowIfNull(segments[i]);

            _segments[i] = segments[i];
            _bases[i] = _ordinalSpace;
            _ordinalSpace += segments[i].Count;
        }

        _lengths = new int[_ordinalSpace];

        for (int i = 0; i < _segments.Length; i++)
            _segments[i].CopyLengthsInto(_lengths, _bases[i]);

        long tokens = 0;

        for (int ordinal = 0; ordinal < _ordinalSpace; ordinal++)
            tokens += _lengths[ordinal];

        _tokens = tokens;
    }

    /// <summary>Segments in the chain, oldest first.</summary>
    public int SegmentCount => _segments.Length;

    /// <summary>
    /// The chain's segments, oldest first. Internal for the tests that assert a chain inherits a
    /// segment's capabilities rather than reimplementing them.
    /// </summary>
    internal IReadOnlyList<SegmentTextIndex> SegmentsForTest => _segments;

    /// <inheritdoc />
    public int Count => _ordinalSpace;

    /// <inheritdoc />
    public int VocabularySize => CountDistinctTerms();

    /// <summary>
    /// Every distinct term across the segments, counted.
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
                    return distinct;

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
        }
        finally
        {
            foreach (var cursor in cursors)
                cursor?.Dispose();
        }
    }

    /// <inheritdoc />
    public long CorpusTokenCount => _tokens;

    /// <inheritdoc />
    public double AverageDocumentLength => Count == 0 ? 0 : (double)_tokens / Count;

    /// <inheritdoc />
    /// <remarks>
    /// Enumerated on demand rather than materialized: a chain that decoded its whole corpus here would
    /// pay the 34-100 ms the laziness exists to avoid.
    /// </remarks>
    public IReadOnlyCollection<SearchDocument> Documents => new ChainDocumentView(this);

    int IAccumulatingIndex.OrdinalSpace => _ordinalSpace;

    /// <inheritdoc />
    SearchDocument? IAccumulatingIndex.DocumentAt(int ordinal)
    {
        if (ordinal < 0 || ordinal >= _ordinalSpace)
            return null;

        int segment = SegmentIndexAt(ordinal);

        return _segments[segment].DocumentAt(ordinal - _bases[segment]);
    }

    /// <inheritdoc />
    int IAccumulatingIndex.OrdinalOf(string documentId)
    {
        for (int i = _segments.Length - 1; i >= 0; i--)
        {
            int ordinal = _segments[i].OrdinalOf(documentId);

            if (ordinal >= 0)
                return ordinal + _bases[i];
        }

        return -1;
    }

    /// <inheritdoc />
    /// <remarks>
    /// The whole read path: one accumulator over the chain's ordinal space, one fold per segment in
    /// generation order, each at its base. The segments are not interleaved, which is what keeps one
    /// reader per segment safe.
    /// </remarks>
    void IAccumulatingIndex.Accumulate<TWeight>(TWeight weight, ScoreAccumulator accumulator)
    {
        var lengths = _lengths;

        for (int i = 0; i < _segments.Length; i++)
            _segments[i].Accumulate(weight, accumulator, _bases[i], lengths);
    }

    /// <inheritdoc />
    public bool Contains(string documentId)
    {
        foreach (var segment in _segments)
        {
            if (segment.Contains(documentId))
                return true;
        }

        return false;
    }

    /// <inheritdoc />
    public int DocumentFrequency(string term)
    {
        int total = 0;

        foreach (var segment in _segments)
            total += segment.DocumentFrequency(term);

        return total;
    }

    /// <inheritdoc />
    public int CorpusFrequency(string term)
    {
        int total = 0;

        foreach (var segment in _segments)
            total += segment.CorpusFrequency(term);

        return total;
    }

    /// <inheritdoc />
    public int TermFrequency(string documentId, string term)
    {
        foreach (var segment in _segments)
        {
            if (segment.TermFrequency(documentId, term) is int frequency and > 0)
                return frequency;
        }

        return 0;
    }

    /// <inheritdoc />
    public int DocumentLength(string documentId)
    {
        for (int i = 0; i < _segments.Length; i++)
        {
            int ordinal = _segments[i].OrdinalOf(documentId);

            if (ordinal >= 0)
                return _lengths[_bases[i] + ordinal];
        }

        return 0;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetTerms(string documentId)
    {
        for (int i = 0; i < _segments.Length; i++)
        {
            if (_segments[i].Contains(documentId))
                return _segments[i].GetTerms(documentId);
        }

        return [];
    }

    /// <inheritdoc />
    public IReadOnlyList<int> GetTermPositions(string documentId, string term)
    {
        for (int i = 0; i < _segments.Length; i++)
        {
            if (_segments[i].Contains(documentId))
                return _segments[i].GetTermPositions(documentId, term);
        }

        return [];
    }

    /// <inheritdoc />
    public bool TryGetDocument(string documentId, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out SearchDocument? document)
    {
        // Oldest generation first, so the same id resolves to the same ordinal the fold recorded it at.
        for (int i = 0; i < _segments.Length; i++)
        {
            int ordinal = _segments[i].OrdinalOf(documentId);

            if (ordinal < 0)
                continue;

            return (document = ((IAccumulatingIndex)this).DocumentAt(_bases[i] + ordinal)) is not null;
        }

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
    /// The union of the query terms' postings across every segment, each document once, in ascending
    /// ordinal order — which is corpus order, because the chain's ordinal ranges are contiguous and
    /// every document lives in exactly one of them.
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
            }

            Array.Sort(ordinals, 0, count);

            int previous = -1;

            for (int i = 0; i < count; i++)
            {
                // A document holding two query terms is in both lists and must be yielded once.
                if (ordinals[i] == previous)
                    continue;

                previous = ordinals[i];
                yield return ((IAccumulatingIndex)this).DocumentAt(ordinals[i])!;
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
            for (int ordinal = 0; ordinal < owner.Count; ordinal++)
                yield return ((IAccumulatingIndex)owner).DocumentAt(ordinal)!;
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
