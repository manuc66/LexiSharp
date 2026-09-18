namespace LexiSharp.Core;

/// <summary>
/// A read-only snapshot of the corpus statistics of an <see cref="ITextIndex"/> — useful for
/// observability, dashboards and corpus introspection.
/// </summary>
/// <param name="DocumentCount">Number of indexed documents.</param>
/// <param name="TermCount">Number of distinct terms in the corpus vocabulary.</param>
/// <param name="TokenCount">Total number of tokens across all documents (collection size).</param>
/// <param name="AverageDocumentLength">Mean number of tokens per document (0 for an empty index).</param>
/// <param name="VocabularyRichness">
/// Distinct terms per document (<see cref="TermCount"/> / <see cref="DocumentCount"/>): how fast
/// the vocabulary grows with each added document. Low values signal a repetitive corpus, high
/// values a varied one. 0 for an empty index.
/// </param>
public sealed record TextIndexStatistics(
    int DocumentCount,
    int TermCount,
    long TokenCount,
    double AverageDocumentLength,
    double VocabularyRichness)
{
    /// <summary>
    /// Derives the statistics from any <see cref="ITextIndex"/> implementation.
    /// </summary>
    public static TextIndexStatistics From(ITextIndex index)
    {
        ArgumentNullException.ThrowIfNull(index);

        return new TextIndexStatistics(
            index.Count,
            index.VocabularySize,
            index.CorpusTokenCount,
            index.AverageDocumentLength,
            index.Count == 0 ? 0 : index.VocabularySize / (double)index.Count);
    }
}