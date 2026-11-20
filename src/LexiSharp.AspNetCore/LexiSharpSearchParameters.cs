namespace LexiSharp.AspNetCore;

using Microsoft.AspNetCore.Mvc;

/// <summary>
/// Query-string parameters of a <see cref="LexiSharpSearchEndpoint"/> request, bound by
/// ASP.NET Core model binding. Mirror the knobs of <see cref="LexiSharpQueryOptions"/>.
/// </summary>
public sealed record LexiSharpSearchParameters
{
    /// <summary>The raw search query. Maps to the <c>q</c> query-string parameter.</summary>
    [FromQuery(Name = "q")]
    public string Query { get; init; } = string.Empty;

    /// <summary>Maximum number of hits to return. Defaults to 10.</summary>
    public int Limit { get; init; } = 10;

    /// <summary>Number of top-ranked hits to skip, for deep pagination. Defaults to 0.</summary>
    public int Offset { get; init; }

    /// <summary>Discard hits with a score below this value. Defaults to <c>-∞</c>.</summary>
    public double MinimumScore { get; init; } = double.NegativeInfinity;

    /// <summary>Wrap matched terms in the hit text. Defaults to <c>false</c>.</summary>
    public bool Highlight { get; init; }

    /// <summary>
    /// Include corpus statistics for the query's terms in the response. Defaults to <c>false</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Maps to the <c>stats</c> query-string parameter. What it adds is one entry per distinct term
    /// with its document frequency, the corpus's document count, its share of the corpus and the
    /// scorer's inverse document frequency — the numbers a caller needs to tell a query that is too
    /// narrow from one that is too broad, which a page of hits cannot show because the page is
    /// capped by <see cref="Limit"/>.
    /// </para>
    /// <para>
    /// It costs a second parse of the query and one vocabulary walk per distinct term, and no
    /// scoring work. It does not change the ranking or the page.
    /// </para>
    /// <para>
    /// The query is described as written: an index built with
    /// <see cref="LexiSharpIndexOptions{TDocument}.EnableFuzzy"/> rewrites a search's terms into fuzzy
    /// atoms, and the statistics are of the words the caller typed rather than of the vocabulary
    /// neighbourhood they reach.
    /// </para>
    /// </remarks>
    public bool Stats { get; init; }

    /// <summary>
    /// Comma-separated names of <c>SearchDocument.Fields</c> to count over the whole match set.
    /// Defaults to <c>null</c> — no facet counts are produced.
    /// </summary>
    /// <remarks>
    /// Maps to the <c>facets</c> query-string parameter. Counts cover every document the query
    /// matched, not just the returned page, so a caller can see the shape of a result set it is
    /// paging through or about to narrow. An unknown field simply yields no bucket, and a value a
    /// matched document does not carry does not count for it — the same contract as
    /// <see cref="LexiSharpIndex{TDocument}.SearchWithFacets(string, LexiSharpQueryOptions?, IReadOnlyList{string}?)"/>.
    /// <para>
    /// Bounded by <see cref="LexiSharpSearchEndpoint.MaxFacetFields"/>: counting is one dictionary
    /// increment per matched document per field, so an unbounded list would let one request pay for
    /// an arbitrary amount of work per result.
    /// </para>
    /// </remarks>
    public string? Facets { get; init; }
}