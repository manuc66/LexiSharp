namespace LexiSharp.Core;

/// <summary>
/// Capability for an index that tracks per-field statistics: how many times a term occurs in one
/// field of one document, how long each field is, and the corpus averages per field.
/// </summary>
/// <remarks>
/// <para>
/// Implemented by <see cref="LexiSharp.Indexing.InMemoryTextIndex"/> and forwarded by the index
/// decorators that wrap one. The field-aware scorer (<see cref="LexiSharp.Ranking.Bm25FScorer"/>)
/// and its parameter tuner detect it with pattern matching (<c>index is IFieldStatisticsIndex</c>)
/// and refuse a corpus that lacks it by name, rather than reading a silent zero — a field-weighted
/// ranking built over zeros would be wrong with no error to show for it.
/// </para>
/// <para>
/// A decorator index may implement this interface and still wrap an inner that lacks the
/// capability; the members then throw <see cref="NotSupportedException"/> naming the wrapped type,
/// the same per-call rule <see cref="RoutedSearchEngine"/> uses for its routed capabilities.
/// </para>
/// </remarks>
public interface IFieldStatisticsIndex : IReadOnlyTextIndex
{
    /// <summary>
    /// How many times <paramref name="term"/> occurs in one field of one document.
    /// </summary>
    /// <param name="documentId">A document currently held in the index.</param>
    /// <param name="field">A field name from <see cref="IReadOnlyTextIndex.Fields"/>.</param>
    /// <param name="term">A token as produced by the index's tokenizer.</param>
    int FieldTermFrequency(string documentId, string field, string term);

    /// <summary>
    /// Tokens in one field of one document; <c>0</c> when the document does not carry the field.
    /// </summary>
    /// <param name="documentId">A document currently held in the index.</param>
    /// <param name="field">A field name from <see cref="IReadOnlyTextIndex.Fields"/>.</param>
    int FieldLength(string documentId, string field);

    /// <summary>
    /// Mean tokens per document over the documents carrying <paramref name="field"/>, including
    /// those where it is present but empty. <c>0</c> when no document carries the field.
    /// </summary>
    /// <param name="field">A field name from <see cref="IReadOnlyTextIndex.Fields"/>.</param>
    double AverageFieldLength(string field);

    /// <summary>
    /// How many documents carry <paramref name="term"/> in <paramref name="field"/>. This counts
    /// the field's own documents, so it can be lower than <see cref="IReadOnlyTextIndex.DocumentFrequency"/>:
    /// a term present in both a title and a body contributes 1 to each field and 1 to the flat count.
    /// </summary>
    /// <param name="field">A field name from <see cref="IReadOnlyTextIndex.Fields"/>.</param>
    /// <param name="term">A token as produced by the index's tokenizer.</param>
    int FieldDocumentFrequency(string field, string term);
}