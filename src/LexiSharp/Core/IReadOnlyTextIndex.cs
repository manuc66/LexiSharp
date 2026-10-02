using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace LexiSharp.Core;

/// <summary>
/// The read-only view of an <see cref="ITextIndex"/>: the corpus statistics and per-document
/// lookups a scorer, a reranker or an extractor needs, with none of the mutation members.
/// </summary>
/// <remarks>
/// <para>
/// This is what the scoring seams receive (<see cref="ITextScorer.Score"/>,
/// <see cref="IScoreExplainer.Explain"/> and an <c>IQueryPlannableScorer</c>'s plan), so
/// the contract a scorer is handed proves it can only read. The write members
/// (<c>Index</c>/<c>Add</c>/<c>Remove</c>/<c>Clear</c>) live only on <see cref="ITextIndex"/>,
/// which a caller holds when it owns the corpus; an engine that forwards writes keeps that wider
/// type and narrows only at the seam into the scorer.
/// </para>
/// <para>
/// Capabilities are separate interfaces built on this one (<see cref="ICandidateIndex"/>,
/// <see cref="IVocabularyIndex"/>, <see cref="IFieldStatisticsIndex"/>), detected with pattern
/// matching — an index that cannot answer a question cheaply simply does not implement the
/// interface.
/// </para>
/// </remarks>
public interface IReadOnlyTextIndex
{
    /// <summary>All documents currently held in the index.</summary>
    IReadOnlyCollection<SearchDocument> Documents { get; }

    /// <summary>Number of indexed documents.</summary>
    int Count { get; }

    /// <summary>Mean number of tokens per document.</summary>
    double AverageDocumentLength { get; }

    /// <summary>
    /// The document count the corpus statistics are taken over — the <c>N</c> a scorer's inverse
    /// document frequency is built from.
    /// </summary>
    /// <remarks>
    /// <see cref="Count"/> by default. An index whose <c>AverageDocumentLength</c> divides by fewer
    /// documents than that returns the smaller number here, because the two are one number and not
    /// two: taking <see cref="Count"/> for the idf while dividing the average by a smaller count is a
    /// statistic no implementation uses.
    /// <para>
    /// It matters only for a corpus holding a document with no term in it — 1 of 8,674 on BEIR
    /// ArguAna — and the effect is about one part in thirty thousand on a score, which moves no
    /// ranking and does move the low bits. Exposed through the length divisor rather than as a setting
    /// of its own so that one choice describes the whole statistic instead of two halves of it.
    /// </para>
    /// </remarks>
    int StatisticDocumentCount => Count;

    /// <summary>Number of distinct terms known to the corpus vocabulary.</summary>
    int VocabularySize { get; }

    /// <summary>Total number of tokens across all documents (collection size).</summary>
    long CorpusTokenCount { get; }

    /// <summary>Returns true if a document with the given id exists.</summary>
    bool Contains(string documentId);

    /// <summary>Tokens associated with a document, in document order.</summary>
    IReadOnlyList<string> GetTerms(string documentId);

    /// <summary>Position of a term inside a document (0-based). Empty when the term is absent.</summary>
    IReadOnlyList<int> GetTermPositions(string documentId, string term);

    /// <summary>Number of documents containing the term.</summary>
    int DocumentFrequency(string term);

    /// <summary>Total number of occurrences of the term across the whole corpus.</summary>
    int CorpusFrequency(string term);

    /// <summary>How many times the term appears in the document (0 when absent).</summary>
    int TermFrequency(string documentId, string term);

    /// <summary>Total number of tokens in the document.</summary>
    int DocumentLength(string documentId);

    /// <summary>Attempts to read the original document; returns false when unknown.</summary>
    bool TryGetDocument(string documentId, [NotNullWhen(true)] out SearchDocument? document);

    /// <summary>
    /// Snapshots the corpus statistics of the index.
    /// </summary>
    /// <remarks>
    /// The default implementation derives everything from the other members, so any
    /// <see cref="IReadOnlyTextIndex"/> implementation gets statistics for free.
    /// </remarks>
    TextIndexStatistics GetStatistics() => TextIndexStatistics.From(this);

    /// <summary>
    /// Names of the fields this index can answer per-field questions about. Always contains
    /// <see cref="TextFields.Default"/>; a single-field index returns only that.
    /// </summary>
    IReadOnlyCollection<string> Fields => [TextFields.Default];
}