namespace LexiSharp.Core;

/// <summary>
/// Capability for a scorer that can score a query term from one field's statistics instead of the
/// whole document's: the arithmetic behind <c>title:term</c>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ITextScorer.Score"/> has no field in its signature, and adding one would break every
/// existing scorer implementation; this is the separate seam a field-aware scorer opts into.
/// <see cref="LexiSharp.Ranking.RankedTextSearchEngine"/> declares
/// <see cref="QueryFeatures.FieldScopes"/> only when its scorer implements this interface and its
/// index implements <see cref="IFieldStatisticsIndex"/>, and refuses a field-scoped query
/// otherwise.
/// </para>
/// <para>
/// A scoped term's score is added to the unscoped score of the same query; the two are separate
/// sums, so a query can mix <c>title:foo</c> with a free <c>bar</c>.
/// </para>
/// </remarks>
public interface IFieldScopedScorer : ITextScorer
{
    /// <summary>
    /// The score of a document for the scoped part of a query. Each term is evaluated from its
    /// own field's term frequency, length and document frequency, and contributes zero when that
    /// field does not carry it.
    /// </summary>
    /// <param name="documentId">Id of the document to score.</param>
    /// <param name="scopedTerms">
    /// The field-scoped scoring terms, in query order. Prohibited atoms are gates and are not
    /// passed here.
    /// </param>
    /// <param name="index">The shared index, which must implement <see cref="IFieldStatisticsIndex"/>.</param>
    double ScoreScoped(
        string documentId,
        IReadOnlyList<FieldScopedTerm> scopedTerms,
        IReadOnlyTextIndex index);
}
