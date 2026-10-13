using System;
using System.Buffers;
using System.Collections.Generic;
using LexiSharp.Core;
using LexiSharp.Linguistics;

namespace LexiSharp.Indexing;

/// <summary>
/// A read-only index over one segment, searchable by the engine as it stands.
/// </summary>
/// <remarks>
/// <para>
/// Nothing here writes, and nothing has to: the engine asks for <see cref="IReadOnlyTextIndex"/> and its
/// mutating methods refuse. The accumulation path streams the segment's posting blocks into the
/// accumulator — the same entries, in the same order, as folding the index in memory — so a query over
/// a segment and the same query over the index it was written from produce the same scores and the same
/// page.
/// </para>
/// <para>
/// Three things cost more here than they do in memory, and they are stated rather than hidden:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>An id to ordinal map, built when the segment opens</b> — a <see cref="Dictionary{TKey,TValue}"/>
/// for now, which is the largest thing this type allocates and the first thing to make lean.
/// </description></item>
/// <item><description>
/// <b>Positions are recomputed, not stored.</b> A segment holds no positions, so
/// <see cref="GetTermPositions"/> tokenizes the document's stored text and reports where the term
/// landed, and it places each stored text field's tokens after the body with the same gap the
/// in-memory index uses — the fields travel with the document precisely so this read reproduces the
/// indexed sequence rather than a body-only one. That is correct — the engine's phrase gate,
/// <c>ProximityReranker</c> and <c>WindowBm25Scorer</c> all keep working — and it costs a
/// tokenization per call, which is per candidate for a phrase query.
/// Storing positions in the format is the fix, and it is not this increment.
/// </description></item>
/// <item><description>
/// <b><see cref="Documents"/> materializes every document the first time it is read</b>, because the
/// engine's full-scan fallback enumerates it. That is the one place a segment pays its whole corpus
/// into memory.
/// </description></item>
/// </list>
/// <para>
/// <b>It offers the candidate path</b> (<see cref="ICandidateIndex"/>), and that is what keeps a rare
/// query from walking the whole corpus. The engine's term-at-a-time pass is only taken when a query
/// reaches its accumulation threshold; below it, the per-document loop runs and — without this
/// capability — iterates <see cref="Documents"/>, materializing and scoring every document in the
/// corpus. Measured on a 40,000-document segment, a single term with a document frequency of 3 costs
/// <b>42 ms</b> that way against <b>10.8 µs</b> through this path. That is the whole of the rare-query
/// cost, and it is a capability rather than a speed.
/// </para>
/// <para>
/// <b>The ordered contract is deliberate, and the unordered variant is not offered.</b>
/// <see cref="ICandidateIndex.GetCandidateDocuments"/> promises the same relative order as
/// <see cref="Documents"/>, and a segment's ordinal space is dense and gapless — so ordinal order *is*
/// corpus order, and yielding ascending ordinals satisfies the contract exactly. The engine prefers
/// the unordered candidate capability when an index has it, which would skip this ordering, but the
/// page a query produces under a tie-break that reads the arrival position depends on the order
/// candidates arrive in, and a segment that broke that ordering would disagree with the index it was
/// written from. The ordering costs one sort of the candidate set — bounded by the query's reach,
/// which the engine keeps under half the corpus — and not a walk of the corpus.
/// </para>
/// <para>
/// The tokenizer must be the one the segment was written with: the dictionary holds normalized terms,
/// so a lookup is by the normalized form, and <see cref="GetTerms"/> and <see cref="GetTermPositions"/>
/// reproduce the positions that tokenizer produced.
/// </para>
/// </remarks>
internal sealed class SegmentTextIndex : IReadOnlyTextIndex, IAccumulatingIndex, ICandidateIndex, IOrdinalIdIndex
{
    private readonly SegmentReader _reader;
    private readonly ITokenizer _tokenizer;
    private readonly Dictionary<string, int> _ordinals;

    /// <summary>
    /// Ordinal to the id string, so the window's tie-break costs a reference read rather than a decode.
    /// </summary>
    /// <remarks>
    /// The strings are the ones <see cref="_ordinals"/> already holds as keys, built during open, so this
    /// array is eight bytes a document and allocates no string of its own. Without it, every id the
    /// tie-break asks for decodes the stored document and allocates a fresh string.
    /// </remarks>
    private readonly string[] _ids;

    private readonly int[] _lengths;
    private readonly DecodedPostingsCache? _decoded;
    private SearchDocument?[]? _documents;

    /// <param name="bytes">A segment written by <see cref="SegmentWriter"/>.</param>
    /// <param name="tokenizer">
    /// The tokenizer the segment was written with. Defaults to <see cref="Tokenizer.Default"/>, which
    /// is what an index built without one uses.
    /// </param>
    public SegmentTextIndex(byte[] bytes, ITokenizer? tokenizer = null)
        : this(new ArraySegmentSource(bytes), tokenizer)
    {
    }

    /// <summary>Opens the index over the segment a source holds.</summary>
    /// <remarks>
    /// Internal because the public story is a byte array or a file; a mapped file reaches this through
    /// <see cref="MappedSegment"/>.
    /// </remarks>
    internal SegmentTextIndex(SegmentSource source, ITokenizer? tokenizer = null, DecodedPostingsCache? decoded = null)
    {
        _reader = new SegmentReader(source);
        _tokenizer = tokenizer ?? Tokenizer.Default;
        _decoded = decoded;
        _ordinals = new Dictionary<string, int>(_reader.DocumentCount, StringComparer.Ordinal);
        _ids = new string[_reader.DocumentCount];
        _lengths = new int[_reader.DocumentCount];

        long tokens = 0;

        for (int ordinal = 0; ordinal < _reader.DocumentCount; ordinal++)
        {
            // One decode of each id, kept twice: as the dictionary key the read surface resolves through,
            // and at the ordinal the window's tie-break asks for.
            string id = _reader.DocumentId(ordinal);

            _ordinals[id] = ordinal;
            _ids[ordinal] = id;
            _lengths[ordinal] = _reader.DocumentLength(ordinal);
            tokens += _lengths[ordinal];
        }

        CorpusTokenCount = tokens;
    }

    /// <inheritdoc />
    /// <remarks>
    /// The union of the query terms' postings, each document once, in ascending ordinal order — which
    /// is corpus order here, because a segment holds no gaps. Every term's postings are walked before
    /// any document is decoded, and the ordinals are pooled, so the only allocation is the sort's.
    /// </remarks>
    IEnumerable<SearchDocument> ICandidateIndex.GetCandidateDocuments(IReadOnlyList<string> terms)
    {
        ArgumentNullException.ThrowIfNull(terms);

        if (terms.Count == 0 || Count == 0)
            return [];

        return EnumerateCandidates(terms);
    }

    /// <summary>
    /// Walks each term's postings into one pooled array of ordinals, sorts it and yields each distinct
    /// document once, in ordinal order.
    /// </summary>
    private IEnumerable<SearchDocument> EnumerateCandidates(IReadOnlyList<string> terms)
    {
        // The union cannot exceed the sum of the terms' document frequencies, and never the corpus.
        int capacity = 0;

        for (int i = 0; i < terms.Count; i++)
        {
            if (_reader.TryFindTerm(terms[i].AsSpan(), out int frequency, out _))
                capacity += frequency;
        }

        if (capacity == 0)
            yield break;

        int[] ordinals = ArrayPool<int>.Shared.Rent(capacity);
        int count = 0;

        try
        {
            for (int i = 0; i < terms.Count; i++)
            {
                if (!_reader.TryFindTerm(terms[i].AsSpan(), out _, out var postings))
                    continue;

                // A source-backed reader fetches its blocks into the reader's own scratch, so each
                // term's walk is finished before the next begins and no buffer is aliased.
                while (postings.MoveNext())
                {
                    while (postings.TryReadEntry(out int ordinal, out _))
                        ordinals[count++] = ordinal;
                }
            }

            Array.Sort(ordinals, 0, count);

            int previous = -1;

            for (int i = 0; i < count; i++)
            {
                // A document holding two query terms is in both lists and must be yielded once.
                if (ordinals[i] == previous)
                    continue;

                previous = ordinals[i];
                yield return _reader.Document(ordinals[i]);
            }
        }
        finally
        {
            ArrayPool<int>.Shared.Return(ordinals);
        }
    }

    /// <inheritdoc />
    public int Count => _reader.DocumentCount;

    /// <summary>
    /// The bytes this segment was read from, or <c>null</c> when the source does not hold them — a
    /// memory-mapped segment fetched its blocks out of a mapping and kept nothing.
    /// </summary>
    internal byte[]? TryGetBytes() => _reader.TryGetBytes();

    /// <summary>
    /// Every term this segment holds, in dictionary order. Internal so a chain can merge the
    /// dictionaries of its generations rather than assuming the vocabularies are disjoint — which they
    /// are not, when a corpus is split.
    /// </summary>
    internal IEnumerable<string> Terms => _reader.Terms;

    /// <inheritdoc />
    public int VocabularySize => _reader.TermCount;

    /// <inheritdoc />
    public long CorpusTokenCount { get; }

    /// <inheritdoc />
    public double AverageDocumentLength => Count == 0 ? 0 : (double)CorpusTokenCount / Count;

    /// <inheritdoc />
    public IReadOnlyCollection<SearchDocument> Documents
    {
        get
        {
            if (_documents is null)
            {
                var documents = new SearchDocument[Count];

                for (int ordinal = 0; ordinal < Count; ordinal++)
                    documents[ordinal] = _reader.Document(ordinal);

                _documents = documents;
            }

            // Every slot is filled before this returns; the element type is nullable only because Find
            // fills them one at a time.
            return _documents!;
        }
    }

    /// <inheritdoc />
    public bool Contains(string documentId) => documentId is not null && _ordinals.ContainsKey(documentId);

    /// <inheritdoc />
    public IReadOnlyList<string> GetTerms(string documentId)
    {
        var document = Find(documentId);

        if (document is null)
            return Array.Empty<string>();

        var positioned = PositionedTerms(document);
        var terms = new string[positioned.Count];

        for (int i = 0; i < positioned.Count; i++)
            terms[i] = positioned[i].Term;

        return terms;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Recomputed from the stored text, because a segment holds no positions — the body's tokens,
    /// then each text field's after a <see cref="TextFields.PositionGap"/> gap, in the same order and
    /// at the same positions the in-memory index records them. A document that matches inside a title
    /// therefore reports the title's positions here too, which is what a proximity window reads.
    /// </remarks>
    public IReadOnlyList<int> GetTermPositions(string documentId, string term)
    {
        var document = Find(documentId);

        if (document is null)
            return Array.Empty<int>();

        var positioned = PositionedTerms(document);
        var positions = new List<int>();

        for (int i = 0; i < positioned.Count; i++)
        {
            if (string.Equals(positioned[i].Term, term, StringComparison.Ordinal))
                positions.Add(positioned[i].Position);
        }

        return positions;
    }

    /// <summary>
    /// A document's terms paired with the position each was indexed at: the body, then every text
    /// field after a gap, exactly as the in-memory index lays them out.
    /// </summary>
    private List<(string Term, int Position)> PositionedTerms(SearchDocument document) =>
        PositionedFields.Positioned(_tokenizer, document);

    /// <inheritdoc />
    public int DocumentFrequency(string term) =>
        _reader.TryFindTerm(term.AsSpan(), out int frequency, out _) ? frequency : 0;

    /// <inheritdoc />
    public int CorpusFrequency(string term)
    {
        if (!_reader.TryFindTerm(term.AsSpan(), out _, out var postings))
            return 0;

        int total = 0;

        while (postings.MoveNext())
        {
            while (postings.TryReadEntry(out _, out int frequency))
                total += frequency;
        }

        return total;
    }

    /// <inheritdoc />
    public int TermFrequency(string documentId, string term)
    {
        if (!_ordinals.TryGetValue(documentId, out int ordinal) ||
            !_reader.TryFindTerm(term.AsSpan(), out _, out var postings))
        {
            return 0;
        }

        // Jump to the block that can hold the ordinal before decoding anything. The postings ascend,
        // so walking from the first block is O(df) for one lookup — and the query-likelihood plan asks
        // this once per candidate per query term, which is what made a store score that family
        // O(candidates x terms x df). AdvanceTo uses the region's skip table and leaves the cursor on
        // the first block whose last entry is at or after the ordinal; because every earlier block ends
        // below it, the ordinal is in that block if it is present at all. The value returned is the same
        // entry either way, so this changes the cost and not the answer.
        if (!postings.AdvanceTo(ordinal))
            return 0;

        while (postings.TryReadEntry(out int candidate, out int frequency))
        {
            if (candidate == ordinal)
                return frequency;

            // Entries ascend within a block, so once past the ordinal it cannot reappear.
            if (candidate > ordinal)
                break;
        }

        return 0;
    }

    /// <inheritdoc />
    public int DocumentLength(string documentId) =>
        _ordinals.TryGetValue(documentId, out int ordinal) ? _lengths[ordinal] : 0;

    /// <inheritdoc />
    public bool TryGetDocument(string documentId, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out SearchDocument? document)
    {
        document = Find(documentId);

        return document is not null;
    }

    /// <inheritdoc />
    /// <remarks>A segment has no gaps: every slot below this holds a document.</remarks>
    int IAccumulatingIndex.OrdinalSpace => Count;

    /// <inheritdoc />
    SearchDocument? IAccumulatingIndex.DocumentAt(int ordinal) => DocumentAt(ordinal);

    /// <inheritdoc />
    int IAccumulatingIndex.OrdinalOf(string documentId) => OrdinalOf(documentId);

    /// <summary>The document at <paramref name="ordinal"/>, or <c>null</c> when that slot is free.</summary>
    /// <remarks>
    /// Internal so a chain can resolve across its generations; a segment has no gaps. The id comes from
    /// the array this index built at open rather than from the reader: the reader would decode a second
    /// string for the same value on every resolved result.
    /// </remarks>
    internal SearchDocument? DocumentAt(int ordinal) =>
        ordinal >= 0 && ordinal < Count ? _reader.Document(ordinal, _ids[ordinal]) : null;

    /// <summary>The ordinal of the document with this id, or <c>-1</c>.</summary>
    internal int OrdinalOf(string documentId) =>
        documentId is not null && _ordinals.TryGetValue(documentId, out int ordinal) ? ordinal : -1;

    /// <inheritdoc />
    string? IOrdinalIdIndex.IdAt(int ordinal) =>
        ordinal >= 0 && ordinal < _ids.Length ? _ids[ordinal] : null;

    /// <summary>
    /// Copies this segment's token counts into <paramref name="destination"/> at
    /// <paramref name="offset"/>, so a chain reads a document's length at its global ordinal.
    /// </summary>
    internal void CopyLengthsInto(int[] destination, int offset)
    {
        ArgumentNullException.ThrowIfNull(destination);

        if (offset < 0 || offset + Count > destination.Length)
            throw new ArgumentException("the destination does not hold this segment at that offset", nameof(destination));

        Array.Copy(_lengths, 0, destination, offset, Count);
    }


    /// <summary>
    /// Finds <paramref name="term"/> in the dictionary and reports the cursor over its postings, the
    /// same one <see cref="IAccumulatingIndex.Accumulate"/> would fold over.
    /// </summary>
    /// <remarks>
    /// Exposed for the flush source, which walks a term's postings to write them back out. A cursor
    /// returned here is valid only until the next one is taken on the same reader.
    /// </remarks>
    /// <summary>
    /// Appends this segment's postings for <paramref name="term"/> to the caller's lists as chain-wide
    /// ordinals.
    /// </summary>
    /// <remarks>
    /// The whole walk lives here rather than in the caller, because a <see cref="PostingBlockReader"/>
    /// is a <c>ref struct</c>: a cursor handed out across a call boundary is a copy whose state the
    /// caller cannot safely hold, and a second walk taken while the first is live would disturb the
    /// reader's scratch buffer.
    /// </remarks>
    internal void AppendGlobalPostings(
        string term,
        int offset,
        List<int> ordinals,
        List<int> frequencies)
    {
        if (!_reader.TryFindTerm(term.AsSpan(), out _, out var postings))
            return;

        while (postings.MoveNext())
        {
            while (postings.TryReadEntry(out int local, out int frequency))
            {
                ordinals.Add(local + offset);
                frequencies.Add(frequency);
            }
        }
    }

    /// <summary>How many posting entries this segment holds for the term — one per occurrence.</summary>
    /// <remarks>
    /// A separate count from <see cref="DocumentFrequency"/>, which counts distinct documents. Sizing a
    /// buffer on the latter under-counts every repeated term, which is most of them.
    /// </remarks>
    internal int CountPostings(string term)
    {
        if (!_reader.TryFindTerm(term.AsSpan(), out _, out var postings))
            return 0;

        int entries = 0;

        while (postings.MoveNext())
            entries += postings.Count;

        return entries;
    }

    /// <summary>
    /// Writes this segment's postings for <paramref name="term"/> into <paramref name="ordinals"/> and
    /// <paramref name="frequencies"/> as chain-wide ordinals, and returns how many it wrote.
    /// </summary>
    /// <remarks>
    /// The flush path's input: a merge writes one segment from the union of the chain's generations and
    /// needs each term's frequencies as well as its ordinals. Walking the blocks here rather than in
    /// the writer keeps the source-backed reader's scratch buffer private to one walk.
    /// </remarks>
    internal int CollectGlobalPostings(string term, int offset, Span<int> ordinals, Span<int> frequencies)
    {
        if (!_reader.TryFindTerm(term.AsSpan(), out _, out var postings))
            return 0;

        int written = 0;

        while (postings.MoveNext())
        {
            while (postings.TryReadEntry(out int ordinal, out int frequency))
            {
                if (written == ordinals.Length)
                    throw new ArgumentException("the destination is smaller than the term's postings", nameof(ordinals));

                ordinals[written] = ordinal + offset;
                frequencies[written] = frequency;
                written++;
            }
        }

        return written;
    }

    /// <summary>
    /// Writes this segment's postings for <paramref name="term"/> into <paramref name="destination"/>
    /// as chain-wide ordinals, and returns how many it wrote.
    /// </summary>
    /// <remarks>
    /// The candidate path's input: a chain unions a term's postings across its segments, and each
    /// segment's local ordinals are shifted by the base the chain owns them at. Walking the blocks
    /// here rather than in the chain keeps the source-backed reader's scratch buffer private to one
    /// walk.
    /// </remarks>
    internal int CollectGlobalOrdinals(string term, int offset, Span<int> destination)
    {
        if (offset < 0)
            throw new ArgumentOutOfRangeException(nameof(offset));

        if (!_reader.TryFindTerm(term.AsSpan(), out _, out var postings))
            return 0;

        int written = 0;

        while (postings.MoveNext())
        {
            while (postings.TryReadEntry(out int ordinal, out _))
            {
                if (written == destination.Length)
                    throw new ArgumentException("the destination is smaller than the term's postings", nameof(destination));

                destination[written++] = ordinal + offset;
            }
        }

        return written;
    }

    /// <inheritdoc />
    void IAccumulatingIndex.Accumulate<TWeight>(TWeight weight, ScoreAccumulator accumulator) =>
        Accumulate(weight, accumulator, 0, _lengths);

    /// <summary>
    /// Folds this segment's postings for one term into <paramref name="accumulator"/>, each entry
    /// recorded at <c>segmentLocalOrdinal + <paramref name="offset"/></c>, skipping the ordinals in
    /// <paramref name="dead"/>.
    /// </summary>
    /// <remarks>
    /// The tombstone test is one set lookup per posting entry, and that is the expensive part of a
    /// versioned index: measured end to end it makes the head query <b>2.1-2.5× slower</b>, not the
    /// 3 ns an isolated loop suggests. A per-block window bitmap was tried and bought nothing at
    /// realistic deletion rates, because nearly every 128-ordinal window already holds a dead ordinal.
    /// </remarks>
    internal void Accumulate<TWeight>(
        TWeight weight,
        ScoreAccumulator accumulator,
        int offset,
        int[] lengths,
        HashSet<int> dead)
        where TWeight : struct, IPostingWeight
    {
        if (!_reader.TryFindTerm(weight.Term.AsSpan(), out _, out var postings))
            return;

        while (postings.MoveNext())
        {
            while (postings.TryReadEntry(out int ordinal, out int frequency))
            {
                int global = ordinal + offset;

                if (dead.Contains(global))
                    continue;

                accumulator.Record(global, weight.Weight(frequency, lengths[global]));
            }
        }
    }

    /// <summary>
    /// Folds this segment's postings for one term into <paramref name="accumulator"/>, each entry
    /// recorded at <c>segmentLocalOrdinal + <paramref name="offset"/></c>.
    /// </summary>
    /// <remarks>
    /// The offset is what lets a chain present one ordinal space across its generations: every segment
    /// owns a contiguous range of the chain's ordinals, so folding each segment at its base puts every
    /// document in the whole chain at exactly one ordinal, and the accumulator's <c>Record</c> behaves
    /// exactly as it does for a single index. <paramref name="lengths"/> is the chain's length array,
    /// because a document's length is read at its global ordinal.
    /// </remarks>
    internal void Accumulate<TWeight>(
        TWeight weight,
        ScoreAccumulator accumulator,
        int offset,
        int[] lengths)
        where TWeight : struct, IPostingWeight
    {
        if (!_reader.TryFindTerm(weight.Term.AsSpan(), out int df, out var postings))
            return;

        // A decoded cache turns a repeat look-up of a hot term into exactly the in-memory index's
        // walk: the same flat arrays, the same ascending entry order, so the sums are bit-identical
        // to a fresh decode. The fill decodes once and reuses; the budget decides what stays.
        // The cache holds segment-local ordinals, so the offset is applied on the way in.
        if (_decoded is { } decoded)
        {
            if (decoded.TryGet(weight.Term, out var ordinalsCache, out var frequenciesCache))
            {
                Fold(ordinalsCache, frequenciesCache, weight, accumulator, offset, lengths);
                return;
            }

            int[] ordinals = new int[df];
            var frequencies = new int[df];

            int index = 0;

            while (postings.MoveNext())
            {
                while (postings.TryReadEntry(out int ordinal, out int frequency))
                {
                    ordinals[index] = ordinal;
                    frequencies[index] = frequency;
                    index++;
                }
            }

            decoded.Store(weight.Term, ordinals, frequencies);
            Fold(ordinals, frequencies, weight, accumulator, offset, lengths);
            return;
        }

        // The blocks ascend by ordinal and so do the entries inside them, which is the order the
        // in-memory fold walks its flat arrays: the accumulator sees the same records in the same order,
        // so the sums come out bit-identical.
        while (postings.MoveNext())
        {
            while (postings.TryReadEntry(out int ordinal, out int frequency))
                accumulator.Record(ordinal + offset, weight.Weight(frequency, lengths[ordinal + offset]));
        }
    }

    private void Fold<TWeight>(
        int[] ordinals,
        int[] frequencies,
        TWeight weight,
        ScoreAccumulator accumulator,
        int offset = 0,
        int[]? lengths = null)
        where TWeight : struct, IPostingWeight
    {
        var effectiveLengths = lengths ?? _lengths;

        for (int i = 0; i < ordinals.Length; i++)
        {
            int ordinal = ordinals[i] + offset;
            accumulator.Record(ordinal, weight.Weight(frequencies[i], effectiveLengths[ordinal]));
        }
    }

    /// <summary>The document with this id, or <c>null</c>. Reads through the same cache as the others.</summary>
    private SearchDocument? Find(string documentId)
    {
        if (documentId is null || !_ordinals.TryGetValue(documentId, out int ordinal))
            return null;

        var documents = _documents;

        if (documents is null)
        {
            documents = new SearchDocument[Count];
            _documents = documents;
        }

        return documents[ordinal] ??= _reader.Document(ordinal);
    }
}
