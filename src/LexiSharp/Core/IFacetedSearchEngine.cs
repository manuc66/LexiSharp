namespace LexiSharp.Core;

/// <summary>
/// Optional capability of an <see cref="ITextSearchEngine"/> that can return, alongside the
/// ranked results, facet counts over <see cref="SearchDocument.Fields"/>.
/// </summary>
/// <remarks>
/// Implementing this interface is opt-in — plain engines keep their single
/// <see cref="ITextSearchEngine.Search(string, SearchOptions?, CancellationToken)"/> contract. A <c>Search</c> call and a
/// <c>SearchWithFacets</c> call on the same engine and options must agree on
/// <see cref="FacetedSearchResult.Results"/> — the faceted variant only adds the buckets.
/// Implemented by the stock <c>RankedTextSearchEngine</c>.
/// </remarks>
public interface IFacetedSearchEngine : ITextSearchEngine
{
    /// <summary>
    /// Same search as <see cref="ITextSearchEngine.Search(string, SearchOptions?, CancellationToken)"/> plus one facet bucket per
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
    /// Same search as <see cref="ITextSearchEngine.Search(ReadOnlySpan{char}, SearchOptions?, CancellationToken)"/>
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

    /// <summary>
    /// Same search as <see cref="SearchWithFacets(string, SearchOptions?, IReadOnlyList{string}?)"/>,
    /// plus one numeric bucket per requested <see cref="NumericFacetRequest"/> — explicit ranges or
    /// a derived histogram — over the same match set.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A distinct name rather than a fourth parameter on <c>SearchWithFacets</c>: the existing
    /// member's signature is what third-party engines implement, and changing it would break every
    /// one of them for a capability they may not have. String facets are also unchanged by this
    /// call — a caller that asks for no numeric facet pays nothing for the capability existing.
    /// </para>
    /// <para>
    /// The match set is the string facets': every document that passes the metadata filters, the
    /// phrase gates and the score thresholds, independently of
    /// <see cref="SearchOptions.Offset"/>/<see cref="SearchOptions.Limit"/>, which only cut
    /// <see cref="FacetedSearchResult.Results"/> — and <see cref="FacetedSearchResult.Results"/> is
    /// identical to <see cref="ITextSearchEngine.Search(string, SearchOptions?, CancellationToken)"/>
    /// for the same arguments. A document missing a field does not count for it; a value that does
    /// not parse as a finite invariant-culture number is in no range and is reported in
    /// <see cref="NumericFacetBucket.UnparseableCount"/> instead. See
    /// <see cref="NumericFacetRequest"/> for the parse rule and the reason it differs from
    /// <see cref="MetadataFilter"/>'s ordinal fallback.
    /// </para>
    /// </remarks>
    /// <param name="query">Raw query text; it is tokenized internally.</param>
    /// <param name="options">Optional search options (<see cref="SearchOptions.Default"/> when null).</param>
    /// <param name="facetFields">String-facet fields; null/empty yields no string buckets.</param>
    /// <param name="numericFacets">
    /// Numeric facets to count; null/empty yields no numeric buckets. Null entries are dropped and
    /// bucket order follows the request order. Two requests for the same field produce two buckets:
    /// a range set and a histogram over one field are different questions, not a duplicate to merge.
    /// </param>
    /// <exception cref="NotSupportedException">
    /// The engine implements the string facets but not the numeric ones. The default implementation
    /// throws this rather than returning an empty list, which would be indistinguishable from a match
    /// set where every value failed to parse.
    /// </exception>
    FacetedSearchResult SearchWithNumericFacets(
        string query,
        SearchOptions? options = null,
        IReadOnlyList<string>? facetFields = null,
        IReadOnlyList<NumericFacetRequest>? numericFacets = null) =>
        throw new NotSupportedException(
            $"{GetType().Name} implements {nameof(IFacetedSearchEngine)} without numeric facets.");

    /// <summary>
    /// Same search as
    /// <see cref="SearchWithNumericFacets(string, SearchOptions?, IReadOnlyList{string}?, IReadOnlyList{NumericFacetRequest}?)"/>,
    /// without materializing the query as a string.
    /// </summary>
    /// <remarks>
    /// The default implementation copies the span into a string and forwards to the string overload;
    /// engines that can tokenize a span directly should override it.
    /// </remarks>
    /// <param name="query">Raw query text; it is tokenized internally.</param>
    /// <param name="options">Optional search options (<see cref="SearchOptions.Default"/> when null).</param>
    /// <param name="facetFields">String-facet fields; null/empty yields no string buckets.</param>
    /// <param name="numericFacets">
    /// Numeric facets to count; null/empty yields no numeric buckets. Null entries are dropped and
    /// bucket order follows the request order.
    /// </param>
    FacetedSearchResult SearchWithNumericFacets(
        ReadOnlySpan<char> query,
        SearchOptions? options = null,
        IReadOnlyList<string>? facetFields = null,
        IReadOnlyList<NumericFacetRequest>? numericFacets = null) =>
        SearchWithNumericFacets(query.ToString(), options, facetFields, numericFacets);
}
