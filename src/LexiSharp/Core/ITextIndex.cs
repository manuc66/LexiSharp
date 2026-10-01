using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace LexiSharp.Core;

/// <summary>
/// An index of tokenized documents exposing the statistics needed by text scorers
/// and classifiers (term frequency, document frequency, document length, ...).
/// </summary>
public interface ITextIndex
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

    /// <summary>Indexes a batch of documents, replacing any previously indexed content.</summary>
    void Index(IEnumerable<SearchDocument> documents);

    /// <summary>Adds a single document to the index. Replacing an existing id is allowed.</summary>
    void Add(SearchDocument document);

    /// <summary>Removes the document with the given id, if present.</summary>
    bool Remove(string documentId);

    /// <summary>Drops every document and statistic from the index.</summary>
    void Clear();

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
    /// <see cref="ITextIndex"/> implementation gets statistics for free.
    /// </remarks>
    TextIndexStatistics GetStatistics() => TextIndexStatistics.From(this);

    /// <summary>
    /// Names of the fields this index can answer per-field questions about. Always contains
    /// <see cref="TextFields.Default"/>; a single-field index returns only that.
    /// </summary>
    IReadOnlyCollection<string> Fields => [TextFields.Default];

    /// <summary>
    /// Whether the index tracks per-field statistics, i.e. whether the per-field members below
    /// are supported. False means the index has a single field and cannot weight fields.
    /// </summary>
    /// <remarks>
    /// A field-aware scorer must check this before calling the per-field members: the default
    /// implementations throw rather than return a plausible-looking zero, because a silent zero
    /// would make a field-weighted ranking wrong with no error to show for it.
    /// </remarks>
    bool HasFieldStatistics => false;

    /// <summary>
    /// How many times <paramref name="term"/> occurs in one field of one document.
    /// </summary>
    /// <param name="documentId">A document currently held in the index.</param>
    /// <param name="field">A field name from <see cref="Fields"/>.</param>
    /// <param name="term">A token as produced by the index's tokenizer.</param>
    /// <exception cref="NotSupportedException">The index has no per-field statistics.</exception>
    int FieldTermFrequency(string documentId, string field, string term) =>
        throw NoFieldStatistics();

    /// <summary>
    /// Tokens in one field of one document; <c>0</c> when the document does not carry the field.
    /// </summary>
    /// <param name="documentId">A document currently held in the index.</param>
    /// <param name="field">A field name from <see cref="Fields"/>.</param>
    /// <exception cref="NotSupportedException">The index has no per-field statistics.</exception>
    int FieldLength(string documentId, string field) =>
        throw NoFieldStatistics();

    /// <summary>
    /// Mean tokens per document over the documents carrying <paramref name="field"/>, including
    /// those where it is present but empty. <c>0</c> when no document carries the field.
    /// </summary>
    /// <param name="field">A field name from <see cref="Fields"/>.</param>
    /// <exception cref="NotSupportedException">The index has no per-field statistics.</exception>
    double AverageFieldLength(string field) =>
        throw NoFieldStatistics();

    /// <summary>
    /// How many documents carry <paramref name="term"/> in <paramref name="field"/>. This counts
    /// the field's own documents, so it can be lower than <see cref="DocumentFrequency"/>: a term
    /// present in both a title and a body contributes 1 to each field and 1 to the flat count.
    /// </summary>
    /// <param name="field">A field name from <see cref="Fields"/>.</param>
    /// <param name="term">A token as produced by the index's tokenizer.</param>
    /// <exception cref="NotSupportedException">The index has no per-field statistics.</exception>
    int FieldDocumentFrequency(string field, string term) =>
        throw NoFieldStatistics();

    /// <summary>
    /// The error a default per-field member raises, naming the index so the caller learns which
    /// implementation lacks the capability rather than seeing a bare "not supported".
    /// </summary>
    private NotSupportedException NoFieldStatistics() => new(
        $"{GetType().Name} tracks no per-field statistics, so no field but the default one can be " +
        $"queried. Index {nameof(SearchDocument)}.{nameof(SearchDocument.TextFields)} to get them.");
}