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

        // Bounded by the ordinal space the fold addresses, not by the live document count: a document
        // removed from the overlay frees its ordinal but the space keeps it, so the live count is
        // smaller than the range a valid ordinal can land in.
        int space = chain.OrdinalSpace;

        _renumber = new int[space];
        _lengths = new int[space];

        // The live documents, in chain order, renumbered densely. Tombstoned and freed ordinals map to
        // -1 and are skipped everywhere below.
        _byLocal = new SearchDocument?[space];
        int local = 0;

        for (int ordinal = 0; ordinal < space; ordinal++)
        {
            var document = chain.DocumentAt(ordinal);

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

                documents[_renumber[ordinal]] = _chain.DocumentAt(ordinal)!;
            }

            return documents;
        }
    }

    public bool Contains(string documentId) => _chain.Contains(documentId);

    public int DocumentFrequency(string term) => _chain.DocumentFrequency(term);

    public int CorpusFrequency(string term) => _chain.CorpusFrequency(term);

    /// <summary>
    /// Capacity for one term's postings, summed across the chain's sources.
    /// </summary>
    /// <remarks>
    /// Summed as <b>occurrences</b>, not document frequencies: a segment's df counts distinct
    /// documents while its postings hold one entry per occurrence, and a term appearing twice in a
    /// document is two entries. This is measured by walking the postings, and the walk happens here
    /// rather than in the caller so that the writer's own cursor over a term's region is not disturbed
    /// by a second walk of the same segment.
    /// </remarks>
    private int PostingCapacity(string term)
    {
        int capacity = 0;

        for (int s = 0; s < _chain.SegmentCount; s++)
            capacity += _chain.SegmentsForTest[s].CountPostings(term);

        capacity += _chain.OverlayPostingCount(term);

        return capacity;
    }

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
        int chainOrdinal = _chain.OrdinalOf(documentId);

        return chainOrdinal < 0 ? -1 : _renumber[chainOrdinal];
    }

    void IAccumulatingIndex.Accumulate<TWeight>(TWeight weight, ScoreAccumulator accumulator) =>
        throw new NotSupportedException("a flush source is written, not searched");

    /// <summary>
    /// Resolves a term to its postings in the dense space, across every generation and the overlay.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One walk per source, in generation order then the overlay — the same order the fold sees them,
    /// so the frequencies are the numbers the read path summed. The walk appends to a list rather than
    /// into a pre-sized array: the size a term's postings will take is not known without walking them,
    /// and a second walk to find out would disturb the reader's scratch.
    /// </para>
    /// <para>
    /// A <see cref="SegmentReader"/> that cannot view its bytes fetches blocks into one scratch buffer,
    /// so two walks of the same segment interleaved overwrite each other. Every walk here is finished
    /// before the next begins.
    /// </para>
    /// </remarks>
    bool ISpanAccumulatingIndex.TryResolvePostings(ReadOnlySpan<char> term, bool withPositions, out PostingView postings)
    {
        var encoded = term.ToString();
        var ordinals = new List<int>();
        var frequencies = new List<int>();
        var positions = withPositions ? new List<int>() : null;

        for (int s = 0; s < _chain.SegmentCount; s++)
            _chain.SegmentsForTest[s].AppendGlobalPostings(
                encoded,
                _chain.BaseOf(s),
                ordinals,
                frequencies,
                positions);

        // The overlay's postings are held in memory, so they cost no fetch and no walk of a region.
        _chain.CollectOverlayPostings(encoded, ordinals, frequencies, positions);

        // Renumber into the dense space, dropping the tombstoned and the freed, and aggregate the
        // occurrences a document holds into one entry: the layout stores one (ordinal, frequency) pair
        // per document with an ascending, strictly distinct ordinal, and a document holding a term three
        // times is one posting with a frequency of 3 — not three postings.
        var compacted = new List<int>();
        var compactedFrequencies = new List<int>();
        var compactedPositions = withPositions ? new List<int>() : null;
        int positionAt = 0;

        for (int i = 0; i < ordinals.Count; i++)
        {
            int renumbered = _renumber[ordinals[i]];
            int frequency = frequencies[i];
            int nextPositionAt = positionAt + frequency;

            if (renumbered < 0)
            {
                positionAt = nextPositionAt;

                continue;
            }

            int local = renumbered;

            // The sources are walked in ascending ordinal order and each document's occurrences land
            // consecutively, so the last entry written is this document's when the ordinals repeat.
            if (compacted.Count > 0 && compacted[^1] == local)
            {
                compactedFrequencies[^1] += frequency;

                if (compactedPositions is not null)
                    for (int k = positionAt; k < nextPositionAt; k++)
                        compactedPositions.Add(positions![k]);

                positionAt = nextPositionAt;

                continue;
            }

            compacted.Add(local);
            compactedFrequencies.Add(frequency);

            if (compactedPositions is not null)
                for (int k = positionAt; k < nextPositionAt; k++)
                    compactedPositions.Add(positions![k]);

            positionAt = nextPositionAt;
        }

        if (compacted.Count == 0)
        {
            // Every document carrying the term is gone. The term still belongs in the dictionary —
            // dropping it would change the vocabulary — so it is written with an empty posting list.
            postings = new PostingView([], [], _lengths);

            return true;
        }

        postings = new PostingView(
            System.Linq.Enumerable.ToArray(compacted),
            System.Linq.Enumerable.ToArray(compactedFrequencies),
            _lengths,
            compactedPositions is null ? null : System.Linq.Enumerable.ToArray(compactedPositions));

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
