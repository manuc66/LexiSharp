using System;
using System.Collections.Generic;
using LexiSharp.Core;

namespace LexiSharp.Indexing;

/// <summary>
/// A corpus with no documents, as the shape <see cref="SegmentWriter"/> writes from.
/// </summary>
/// <remarks>
/// A store that has never been written to still needs one segment to fold over, because a chain
/// refuses an empty list and every read path would otherwise need a special case for "no generations".
/// One zero-document segment is written once and folded over like any other.
/// </remarks>
internal sealed class EmptySegmentSource : ITextIndex, ISpanAccumulatingIndex, IVocabularyIndex
{
    public int Count => 0;

    public int VocabularySize => 0;

    public long CorpusTokenCount => 0;

    public double AverageDocumentLength => 0;

    public IReadOnlyCollection<SearchDocument> Documents => [];

    public int StatisticDocumentCount => 0;

    public bool Contains(string documentId) => false;

    public int DocumentFrequency(string term) => 0;

    public int CorpusFrequency(string term) => 0;

    public int TermFrequency(string documentId, string term) => 0;

    public int DocumentLength(string documentId) => 0;

    public IReadOnlyList<string> GetTerms(string documentId) => [];

    public IReadOnlyList<int> GetTermPositions(string documentId, string term) => [];

    public bool TryGetDocument(string documentId, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out SearchDocument? document)
    {
        document = null;

        return false;
    }

    public IReadOnlyCollection<string> Fields => [TextFields.Default];

    public IEnumerable<string> Vocabulary => [];

    int IAccumulatingIndex.OrdinalSpace => 0;

    SearchDocument? IAccumulatingIndex.DocumentAt(int ordinal) => null;

    int IAccumulatingIndex.OrdinalOf(string documentId) => -1;

    void IAccumulatingIndex.Accumulate<TWeight>(TWeight weight, ScoreAccumulator accumulator)
    {
    }

    void ITextIndex.Index(IEnumerable<SearchDocument> documents) =>
        throw new NotSupportedException("an empty source is written, not written to");

    void ITextIndex.Add(SearchDocument document) =>
        throw new NotSupportedException("an empty source is written, not written to");

    bool ITextIndex.Remove(string documentId) =>
        throw new NotSupportedException("an empty source is written, not written to");

    void ITextIndex.Clear() =>
        throw new NotSupportedException("an empty source is written, not written to");

    bool ISpanAccumulatingIndex.TryResolvePostings(ReadOnlySpan<char> term, bool withPositions, out PostingView postings)
    {
        postings = default;

        return false;
    }

    int ISpanAccumulatingIndex.SpanDocumentFrequency(ReadOnlySpan<char> term) => 0;

    void ISpanAccumulatingIndex.AccumulateResolved<TWeight>(in PostingView postings, TWeight weight, ScoreAccumulator accumulator)
    {
    }
}
