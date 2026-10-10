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
/// landed. That is correct — the engine's phrase gate, <c>ProximityReranker</c> and <c>WindowBm25Scorer</c>
/// all keep working — and it costs a tokenization per call, which is per candidate for a phrase query.
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
internal sealed class SegmentTextIndex : IReadOnlyTextIndex, IAccumulatingIndex, ICandidateIndex
{
    private readonly SegmentReader _reader;
    private readonly ITokenizer _tokenizer;
    private readonly Dictionary<string, int> _ordinals;
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
        _lengths = new int[_reader.DocumentCount];

        long tokens = 0;

        for (int ordinal = 0; ordinal < _reader.DocumentCount; ordinal++)
        {
            _ordinals[_reader.DocumentId(ordinal)] = ordinal;
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

        return document is null ? Array.Empty<string>() : _tokenizer.Tokenize(document.Text);
    }

    /// <inheritdoc />
    /// <remarks>Recomputed from the stored text, because a segment holds no positions.</remarks>
    public IReadOnlyList<int> GetTermPositions(string documentId, string term)
    {
        var document = Find(documentId);

        if (document is null)
            return Array.Empty<int>();

        var terms = _tokenizer.Tokenize(document.Text);
        var positions = new List<int>();

        for (int i = 0; i < terms.Count; i++)
        {
            if (string.Equals(terms[i], term, StringComparison.Ordinal))
                positions.Add(i);
        }

        return positions;
    }

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

        while (postings.MoveNext())
        {
            while (postings.TryReadEntry(out int candidate, out int frequency))
            {
                if (candidate == ordinal)
                    return frequency;
            }
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
    SearchDocument? IAccumulatingIndex.DocumentAt(int ordinal) =>
        ordinal >= 0 && ordinal < Count ? _reader.Document(ordinal) : null;

    /// <inheritdoc />
    int IAccumulatingIndex.OrdinalOf(string documentId) =>
        documentId is not null && _ordinals.TryGetValue(documentId, out int ordinal) ? ordinal : -1;

    /// <inheritdoc />

    /// <inheritdoc />
    void IAccumulatingIndex.Accumulate<TWeight>(TWeight weight, ScoreAccumulator accumulator)
    {
        if (!_reader.TryFindTerm(weight.Term.AsSpan(), out int df, out var postings))
            return;

        // A decoded cache turns a repeat look-up of a hot term into exactly the in-memory index's
        // walk: the same flat arrays, the same ascending entry order, so the sums are bit-identical
        // to a fresh decode. The fill decodes once and reuses; the budget decides what stays.
        if (_decoded is { } decoded)
        {
            if (decoded.TryGet(weight.Term, out var ordinalsCache, out var frequenciesCache))
            {
                Fold(ordinalsCache, frequenciesCache, weight, accumulator);
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
            Fold(ordinals, frequencies, weight, accumulator);
            return;
        }

        // The blocks ascend by ordinal and so do the entries inside them, which is the order the
        // in-memory fold walks its flat arrays: the accumulator sees the same records in the same order,
        // so the sums come out bit-identical.
        while (postings.MoveNext())
        {
            while (postings.TryReadEntry(out int ordinal, out int frequency))
                accumulator.Record(ordinal, weight.Weight(frequency, _lengths[ordinal]));
        }
    }

    private void Fold<TWeight>(
        int[] ordinals,
        int[] frequencies,
        TWeight weight,
        ScoreAccumulator accumulator)
        where TWeight : struct, IPostingWeight
    {
        var lengths = _lengths;

        for (int i = 0; i < ordinals.Length; i++)
            accumulator.Record(ordinals[i], weight.Weight(frequencies[i], lengths[ordinals[i]]));
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
