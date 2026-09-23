using LexiSharp.Core;
using LexiSharp.Linguistics;

namespace LexiSharp;

/// <summary>
/// One search hit over a user document, as returned by <see cref="LexiSharpIndex{TDocument}"/>.
/// </summary>
/// <param name="DocumentId">Identifier of the matched document, as configured on the index.</param>
/// <param name="Score">Relevance score produced by the underlying scorer. Higher is better.</param>
/// <param name="Document">The original, unconverted caller document that matched.</param>
/// <param name="HighlightedText">
/// The indexed text with query terms wrapped in highlight tags, when highlighting was
/// requested on the query; <c>null</c> otherwise, or when the configured tokenizer does
/// not expose span information (<see cref="ISpanTokenizer"/>).
/// </param>
public sealed record LexiSharpHit<TDocument>(
    string DocumentId,
    double Score,
    TDocument Document,
    string? HighlightedText = null)
    where TDocument : class;

/// <summary>
/// Ranked hits plus one facet bucket per requested field, as returned by
/// <see cref="LexiSharpIndex{TDocument}.SearchWithFacets"/>.
/// </summary>
/// <param name="Results">The ranked, paginated page.</param>
/// <param name="Buckets">
/// Value counts over the whole match set, independent of the page window; empty when no
/// facet field was requested or no counted document carries any.
/// </param>
public sealed record LexiSharpFacetedResult<TDocument>(
    IReadOnlyList<LexiSharpHit<TDocument>> Results,
    IReadOnlyList<FacetBucket> Buckets)
    where TDocument : class;