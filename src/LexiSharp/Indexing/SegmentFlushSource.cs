using System;
using System.Buffers;
using System.Collections.Generic;
using LexiSharp.Core;

namespace LexiSharp.Indexing;

/// <summary>
/// A chain's live corpus, renumbered densely, as the index <see cref="SegmentWriter"/> writes from.
/// </summary>
/// <remarks>
/// <para>
/// A segment cannot hold a gap — <see cref="SegmentWriter"/> refuses a corpus with a free ordinal slot —
/// so writing one from a chain is a compaction rather than a copy. Tombstoned documents drop out, the
/// live ones are renumbered <c>0 … count-1</c>, and the ordinals rearrange underneath them. That is why
/// a commit is the one point in a chain's life at which every ordinal changes, and it is a step the
/// caller schedules rather than something a query does.
/// </para>
/// <para>
/// Everything a write needs is already measured in this file's prototype: a segment write at 40,000
/// documents costs <b>0.6-1.2 s and ~2.9 GB allocated</b>, against a 12.7 MB file. That is the number a
/// caller commits for, and it is why a commit is a batch operation.
/// </para>
/// <para>
/// The vocabulary is the union of the chain's, in code-point order, because that is the order the
/// format's dictionary is read in. A term held by two segments is one entry.
/// </para>
/// </remarks>
internal sealed class SegmentFlushSource : ITextIndex, ISpanAccumulatingIndex, IVocabularyIndex
{
    private readonly SegmentChain _chain;

    /// <summary>Dense local ordinal → the chain-wide ordinal that owns it, or -1 for a dead slot.</summary>
    private readonly int[] _renumber;

    private readonly int _count;
    private readonly int[] _lengths;
    private readonly string[] _terms;
    private readonly SearchDocument?[] _byLocal;

    public SegmentFlushSource(SegmentChain chain)
    {
        ArgumentNullException.ThrowIfNull(chain);

        _chain = chain;
        _renumber = new int[chain.StatisticDocumentCount];
        _lengths = new int[chain.StatisticDocumentCount];

        // The live documents, in chain order, renumbered densely. Tombstoned and freed ordinals map to
        // -1 and are skipped everywhere below.
        _byLocal = new SearchDocument?[chain.StatisticDocumentCount];
        int local = 0;

        for (int ordinal = 0; ordinal < chain.StatisticDocumentCount; ordinal++)
        {
            var document = ((IAccumulatingIndex)chain).DocumentAt(ordinal);

            if (document is null)
            {
                _renumber[ordinal] = -1;

                continue;
            }

            _renumber[ordinal] = local;
            _byLocal[local] = document;
            _lengths[local] = chain.DocumentLength(document.Id);
            local++;
        }

        _count = local;
        _terms = chain.CollectDistinctTerms();
    }

    public int Count => _count;

    public int VocabularySize => _terms.Length;

    public long CorpusTokenCount
    {
        get
        {
            long tokens = 0;

            for (int ordinal = 0; ordinal < _count; ordinal++)
                tokens += _lengths[ordinal];

            return tokens;
        }
    }

    public double AverageDocumentLength => _count == 0 ? 0 : (double)CorpusTokenCount / _count;

    public IEnumerable<string> Vocabulary => _terms;

    public IReadOnlyCollection<SearchDocument> Documents
    {
        get
        {
            var documents = new SearchDocument[_count];

            for (int ordinal = 0; ordinal < _renumber.Length; ordinal++)
            {
                if (_renumber[ordinal] < 0)
                    continue;

                documents[_renumber[ordinal]] = ((IAccumulatingIndex)_chain).DocumentAt(ordinal)!;
            }

            return documents;
        }
    }

    public bool Contains(string documentId) => _chain.Contains(documentId);

    public int DocumentFrequency(string term) => _chain.DocumentFrequency(term);

    public int CorpusFrequency(string term) => _chain.CorpusFrequency(term);

    public int TermFrequency(string documentId, string term) => _chain.TermFrequency(documentId, term);

    public int DocumentLength(string documentId) => _chain.DocumentLength(documentId);

    public IReadOnlyList<string> GetTerms(string documentId) => _chain.GetTerms(documentId);

    public IReadOnlyList<int> GetTermPositions(string documentId, string term) =>
        _chain.GetTermPositions(documentId, term);

    public bool TryGetDocument(string documentId, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out SearchDocument? document) =>
        _chain.TryGetDocument(documentId, out document);

    public TextIndexStatistics GetStatistics() => TextIndexStatistics.From(this);

    public IReadOnlyCollection<string> Fields => [TextFields.Default];

    int IAccumulatingIndex.OrdinalSpace => _count;

    SearchDocument? IAccumulatingIndex.DocumentAt(int ordinal) =>
        ordinal >= 0 && ordinal < _count ? _byLocal[ordinal] : null;

    int IAccumulatingIndex.OrdinalOf(string documentId)
    {
        int chainOrdinal = ((IAccumulatingIndex)_chain).OrdinalOf(documentId);

        return chainOrdinal < 0 ? -1 : _renumber[chainOrdinal];
    }

    void IAccumulatingIndex.Accumulate<TWeight>(TWeight weight, ScoreAccumulator accumulator) =>
        throw new NotSupportedException("a flush source is written, not searched");

    bool ISpanAccumulatingIndex.TryResolvePostings(ReadOnlySpan<char> term, out PostingView postings)
    {
        var encoded = term.ToString();
        int df = _chain.DocumentFrequency(encoded);

        if (df == 0)
        {
            postings = default;

            return false;
        }

        var ordinals = new int[df];
        var frequencies = new int[df];
        int written = 0;

        // Every generation, in order, then the overlay — the same order the fold sees them, so the
        // frequencies are the same numbers the read path summed.
        for (int s = 0; s < _chain.SegmentCount; s++)
            written += _chain.SegmentsForTest[s].CollectGlobalPostings(
                encoded,
                _chain.BaseOf(s),
                ordinals.AsSpan(written),
                frequencies.AsSpan(written));

        written += _chain.CollectOverlayPostings(encoded, ordinals.AsSpan(written), frequencies.AsSpan(written));

        // Renumber into the dense space, dropping the tombstoned and the freed.
        var compacted = new int[written];
        var compactedFrequencies = new int[written];
        int kept = 0;

        for (int i = 0; i < written; i++)
        {
            int local = _renumber[ordinals[i]];

            if (local < 0)
                continue;

            compacted[kept] = local;
            compactedFrequencies[kept] = frequencies[i];
            kept++;
        }

        postings = new PostingView(
            compacted.AsSpan(0, kept).ToArray(),
            compactedFrequencies.AsSpan(0, kept).ToArray(),
            _lengths);

        return true;
    }

    void ITextIndex.Index(IEnumerable<SearchDocument> documents) =>
        throw new NotSupportedException("a flush source is written, not written to");

    void ITextIndex.Add(SearchDocument document) =>
        throw new NotSupportedException("a flush source is written, not written to");

    bool ITextIndex.Remove(string documentId) =>
        throw new NotSupportedException("a flush source is written, not written to");

    void ITextIndex.Clear() =>
        throw new NotSupportedException("a flush source is written, not written to");

    int ISpanAccumulatingIndex.SpanDocumentFrequency(ReadOnlySpan<char> term) =>
        _chain.DocumentFrequency(term.ToString());

    void ISpanAccumulatingIndex.AccumulateResolved<TWeight>(in PostingView postings, TWeight weight, ScoreAccumulator accumulator) =>
        throw new NotSupportedException("a flush source is written, not searched");
}
