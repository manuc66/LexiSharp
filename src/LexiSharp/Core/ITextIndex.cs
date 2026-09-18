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
}