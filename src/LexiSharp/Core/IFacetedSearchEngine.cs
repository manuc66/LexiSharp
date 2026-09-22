namespace LexiSharp.Core;

/// <summary>
/// Optional capability of an <see cref="ITextSearchEngine"/> that can return, alongside the
/// ranked results, facet counts over <see cref="SearchDocument.Fields"/>.
/// </summary>
/// <remarks>
/// Implementing this interface is opt-in — plain engines keep their single
/// <see cref="ITextSearchEngine.Search(string, SearchOptions)"/> contract. A <c>Search</c> call and a
/// <c>SearchWithFacets</c> call on the same engine and options must agree on
/// <see cref="FacetedSearchResult.Results"/> — the faceted variant only adds the buckets.
/// Implemented by the stock <c>RankedTextSearchEngine</c>.
/// </remarks>
public interface IFacetedSearchEngine : ITextSearchEngine
{
    /// <summary>
    /// Same search as <see cref="ITextSearchEngine.Search(string, SearchOptions)"/> plus one facet bucket per
    /// requested field of <see cref="SearchDocument.Fields"/>.
    /// </summary>
    /// <remarks>
    /// Counts cover every document that passes the metadata filters, the phrase gates and the
    /// score thresholds — the whole match set — independently of
    /// <see cref="SearchOptions.Offset"/>/<see cref="SearchOptions.Limit"/>, which only cut
    /// <see cref="FacetedSearchResult.Results"/>. A document missing a field does not count
    /// for it (<see cref="SearchDocument.Category"/> is never faceted), and fields no counted
    /// document carries are omitted from the buckets. Values compare ordinally; within a
    /// bucket, values sort by count descending then value ascending (ordinal).
    /// </remarks>
    /// <param name="query">Raw query text; it is tokenized internally.</param>
    /// <param name="options">Optional search options (<see cref="SearchOptions.Default"/> when null).</param>
    /// <param name="facetFields">
    /// Fields to count; null/empty yields no buckets. Null or empty entries and duplicates
    /// are dropped; bucket order follows the first occurrence of each distinct field.
    /// </param>
    FacetedSearchResult SearchWithFacets(
        string query,
        SearchOptions? options = null,
        IReadOnlyList<string>? facetFields = null);

    /// <summary>
    /// Same search as <see cref="ITextSearchEngine.Search(ReadOnlySpan{char}, SearchOptions?)"/>
    /// plus facet buckets, without materializing the query as a string.
    /// </summary>
    /// <remarks>
    /// The default implementation copies the span into a string and forwards to
    /// <see cref="SearchWithFacets(string, SearchOptions?, IReadOnlyList{string}?)"/>; engines
    /// that can tokenize a span directly should override it.
    /// </remarks>
    /// <param name="query">Raw query text; it is tokenized internally.</param>
    /// <param name="options">Optional search options (<see cref="SearchOptions.Default"/> when null).</param>
    /// <param name="facetFields">
    /// Fields to count; null/empty yields no buckets. Null or empty entries and duplicates
    /// are dropped; bucket order follows the first occurrence of each distinct field.
    /// </param>
    FacetedSearchResult SearchWithFacets(
        ReadOnlySpan<char> query,
        SearchOptions? options = null,
        IReadOnlyList<string>? facetFields = null) =>
        SearchWithFacets(query.ToString(), options, facetFields);
}
