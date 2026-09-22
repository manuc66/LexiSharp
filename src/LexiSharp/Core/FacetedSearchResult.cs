namespace LexiSharp.Core;

/// <summary>
/// Ranked results plus one bucket per requested facet field.
/// </summary>
/// <param name="Results">The ranked, paginated page — identical to <c>Search</c> for the same arguments.</param>
/// <param name="Buckets">
/// One bucket per field that at least one counted document carries, in requested-field
/// order; fields absent from every counted document are omitted.
/// </param>
public sealed record FacetedSearchResult(
    IReadOnlyList<SearchResult> Results,
    IReadOnlyList<FacetBucket> Buckets);

/// <summary>Value counts for one facet field.</summary>
/// <param name="Field">The requested field name.</param>
/// <param name="Values">Distinct values by count (descending), then value (ascending, ordinal).</param>
public sealed record FacetBucket(
    string Field,
    IReadOnlyList<FacetValue> Values);

/// <summary>A distinct field value and how many counted documents carry it.</summary>
/// <param name="Value">The value as stored on the document (ordinal comparison).</param>
/// <param name="Count">Number of counted documents whose <see cref="SearchDocument.Fields"/> entry equals this value.</param>
public sealed record FacetValue(string Value, int Count);
