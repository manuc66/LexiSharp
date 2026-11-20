using System.Text.Json.Serialization;
using LexiSharp;
using LexiSharp.Core;
using LexiSharp.Highlighting;
using Microsoft.AspNetCore.Http;

namespace LexiSharp.AspNetCore;

/// <summary>One hit of a <see cref="LexiSharpSearchResponse{TDocument}"/>.</summary>
/// <param name="DocumentId">Identifier of the matched document.</param>
/// <param name="Score">Relevance score produced by the underlying scorer. Higher is better.</param>
/// <param name="Document">The original caller document that matched.</param>
/// <param name="HighlightedText">
/// The indexed text with query terms wrapped in highlight tags when <c>highlight</c> was
/// requested; <c>null</c> otherwise. The document's own characters are HTML-encoded, so the value
/// can be inserted into an element's content as it is.
/// </param>
public sealed record LexiSharpSearchHit<TDocument>(
    string DocumentId,
    double Score,
    TDocument Document,
    string? HighlightedText)
    where TDocument : class;

/// <summary>The payload of a successful search response.</summary>
/// <param name="Query">The query that was searched, echoed back to the caller.</param>
/// <param name="Hits">The ranked, paginated page.</param>
/// <remarks>
/// The two optional parts are <c>null</c> unless the request asked for them, and are omitted from
/// the JSON when they are — a response to a request that asked for neither is byte-identical to the
/// one this endpoint returned before they existed.
/// </remarks>
public sealed record LexiSharpSearchResponse<TDocument>(
    string Query,
    IReadOnlyList<LexiSharpSearchHit<TDocument>> Hits)
    where TDocument : class
{
    /// <summary>
    /// Corpus statistics for the query's terms, or <c>null</c> when <c>stats</c> was not requested.
    /// </summary>
    /// <remarks>
    /// One entry per distinct term, in reading order: its document frequency, the corpus's document
    /// count, its share of the corpus and the scorer's inverse document frequency (<c>null</c> when
    /// the scorer does not report one). It is what tells a caller that a query returning two hits was
    /// narrow because its terms occur in two documents, rather than because it was badly ranked.
    /// </remarks>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<QueryTermStatistic>? Terms { get; init; }

    /// <summary>
    /// One bucket per requested facet field over the whole match set, or <c>null</c> when no
    /// <c>facets</c> field was requested.
    /// </summary>
    /// <remarks>
    /// Counts are independent of the page: they cover every document the query matched, which is the
    /// distribution a caller pages through or narrows with. An unknown field yields no bucket rather
    /// than an error, so a client can ask for the same field set on every request.
    /// </remarks>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<FacetBucket>? Facets { get; init; }
}

/// <summary>The payload of a failed search response.</summary>
/// <param name="Field">The parameter the problem is about (e.g. <c>q</c>).</param>
/// <param name="Message">Human-readable description of the problem.</param>
public sealed record LexiSharpSearchError(string Field, string Message);

/// <summary>
/// The testable heart of the search endpoint: an HTTP-agnostic function from an index and
/// request parameters to an <see cref="IResult"/>. The minimal-API extension only wires the
/// route to this method, so the whole behavior is covered without hosting an application.
/// </summary>
public static class LexiSharpSearchEndpoint
{
    /// <summary>
    /// Largest page this endpoint will build. A request asking for more is refused with <c>400</c>
    /// rather than truncated.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why a bound, and why on the request.</b> <c>Limit</c> is not just how many hits are
    /// serialized: the ranking is cut from a working window of <c>Offset + Limit</c> entries, so a
    /// page of <c>int.MaxValue</c> asks the engine to hold every document in the corpus. The window
    /// is an insertion-sorted list, so filling it costs time proportional to the window per
    /// candidate that enters it — which is why the cost of one request tracks the corpus rather than
    /// the page. Measured on a 16 000-document corpus: <c>limit=10</c> took 1–3 ms, and
    /// <c>offset=15 990&amp;limit=10</c> took 1.9 s while returning a page of ten; at 200 000
    /// documents <c>limit=int.MaxValue</c> did not finish in two minutes.
    /// </para>
    /// <para>
    /// A hundred is a page a browser can show and a client can read; a caller that needs more is
    /// paginating, which is what <see cref="MaxOffset"/> is for. The bound is refused rather than
    /// clamped because a clamped page is a page the caller did not ask for, and it would be
    /// indistinguishable from a shorter corpus.
    /// </para>
    /// </remarks>
    public const int MaxLimit = 100;

    /// <summary>
    /// Largest <c>offset</c> this endpoint will honor. A request asking for more is refused with
    /// <c>400</c>.
    /// </summary>
    /// <remarks>
    /// The window is <c>Offset + Limit</c>, so a large offset costs what a large limit does even
    /// when the page that comes back is empty: measured here, <c>offset=2147483647&amp;limit=10</c>
    /// on 20 000 documents returned 28 bytes of JSON after 3.1 s of work. Ten thousand hits deep is
    /// already past what any search UI shows, and the corpus itself bounds the useful range.
    /// </remarks>
    public const int MaxOffset = 10_000;

    /// <summary>
    /// Largest number of facet fields one request may count. A request naming more is refused with
    /// <c>400</c>.
    /// </summary>
    /// <remarks>
    /// Counting is one dictionary increment per matched document per field, so the cost of a facet
    /// request is the match set times the number of fields: a list of ten thousand fields is a
    /// request to make one query do ten thousand passes' worth of work. Eight is more fields than a
    /// search UI filters by, and a caller that needs more can split the request.
    /// </remarks>
    public const int MaxFacetFields = 8;

    /// <summary>
    /// How this endpoint highlights: the document's own characters are HTML-encoded around the
    /// <c>&lt;em&gt;</c> the highlighter inserts.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The core's default is <see cref="HighlightOptions.EscapeHtml"/> off, and that default is right
    /// for the callers it was written for — a console, a log, a desktop control, where the text is
    /// never interpreted. This endpoint's is not one of them: it answers a web client, and
    /// <c>highlightedText</c> exists to be rendered. A document is data the service holds — a corpus
    /// file, a scraped page, a user's comment — so <c>&lt;img src=x onerror=...&gt;</c> inside it
    /// reached the page as an element, and no option on the route could say otherwise.
    /// </para>
    /// <para>
    /// The encoding is not a query parameter, deliberately: a caller does not get to choose whether
    /// one document's characters are interpreted, any more than it chooses the page size.
    /// </para>
    /// </remarks>
    private static readonly HighlightOptions EscapedHighlight = new() { EscapeHtml = true };

    /// <summary>
    /// Splits the <c>facets</c> parameter into field names: comma-separated, trimmed, empty entries
    /// dropped, duplicates collapsed, first-occurrence order.
    /// </summary>
    /// <remarks>
    /// Stops one past <paramref name="limit"/> rather than parsing the whole string: the caller only
    /// has to know whether the request exceeded the bound, and a request naming ten thousand fields
    /// should not cost ten thousand substrings to refuse.
    /// </remarks>
    private static IReadOnlyList<string> ParseFacetFields(string? facets, int limit)
    {
        if (string.IsNullOrWhiteSpace(facets))
            return Array.Empty<string>();

        var fields = new List<string>();

        foreach (string part in facets.Split(','))
        {
            string field = part.Trim();

            if (field.Length == 0 || fields.Contains(field, StringComparer.Ordinal))
                continue;

            fields.Add(field);

            if (fields.Count > limit)
                break;
        }

        return fields;
    }

    /// <summary>Runs a search on the index and builds the HTTP response.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="index"/> or <paramref name="parameters"/> is null.</exception>
    public static IResult Handle<TDocument>(
        LexiSharpIndex<TDocument> index,
        LexiSharpSearchParameters parameters)
        where TDocument : class =>
        Handle(index, parameters, CancellationToken.None);

    /// <summary>
    /// Runs a search on the index and builds the HTTP response, stopping when
    /// <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    /// <remarks>
    /// The mapped route passes <c>HttpContext.RequestAborted</c> here: a client that disconnects
    /// should not leave the ranking running, and the ranking is the expensive part of a request —
    /// the page is cut from a working window and every candidate may pay a vocabulary walk.
    /// <para>
    /// An overload rather than a defaulted parameter, for the same reason as
    /// <see cref="LexiSharpIndex{TDocument}.Search(string, LexiSharpQueryOptions?, CancellationToken)"/>:
    /// a defaulted parameter changes the signature and breaks callers compiled against it.
    /// </para>
    /// </remarks>
    /// <param name="index">The index to search.</param>
    /// <param name="parameters">The request parameters, already bound from the query string.</param>
    /// <param name="cancellationToken">Token observed by the search.</param>
    /// <exception cref="ArgumentNullException"><paramref name="index"/> or <paramref name="parameters"/> is null.</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    public static IResult Handle<TDocument>(
        LexiSharpIndex<TDocument> index,
        LexiSharpSearchParameters parameters,
        CancellationToken cancellationToken)
        where TDocument : class
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(parameters);

        if (string.IsNullOrWhiteSpace(parameters.Query))
            return Results.BadRequest(new LexiSharpSearchError("q", "The 'q' query parameter is required."));

        if (parameters.Limit > MaxLimit)
        {
            return Results.BadRequest(new LexiSharpSearchError(
                "limit",
                $"The 'limit' parameter is at most {MaxLimit}; {parameters.Limit} was requested."));
        }

        if (parameters.Offset > MaxOffset)
        {
            return Results.BadRequest(new LexiSharpSearchError(
                "offset",
                $"The 'offset' parameter is at most {MaxOffset}; {parameters.Offset} was requested."));
        }

        IReadOnlyList<string> facetFields = ParseFacetFields(parameters.Facets, MaxFacetFields);

        if (facetFields.Count > MaxFacetFields)
        {
            return Results.BadRequest(new LexiSharpSearchError(
                "facets",
                $"The 'facets' parameter names more than {MaxFacetFields} fields."));
        }

        var queryOptions = new LexiSharpQueryOptions(
            Limit: parameters.Limit,
            MinimumScore: parameters.MinimumScore,
            Offset: parameters.Offset,
            Highlight: parameters.Highlight)
        {
            HighlightOptions = EscapedHighlight,
        };

        IReadOnlyList<LexiSharpHit<TDocument>> hits;
        IReadOnlyList<FacetBucket>? facets = null;
        IReadOnlyList<QueryTermStatistic>? terms = null;

        // `facets=` with nothing usable in it is still a request for facets: the answer is an empty
        // list, not an omitted property, because an agent that reads `facets` absent concludes it
        // never asked and will ask again.
        if (parameters.Facets is not null && facetFields.Count == 0)
            facets = Array.Empty<FacetBucket>();

        try
        {
            if (facetFields.Count > 0)
            {
                // The faceted call is the search plus the counts: it returns the same ranked page,
                // so asking for buckets does not change what a caller receives besides `facets`.
                var faceted = index.SearchWithFacets(parameters.Query, queryOptions, facetFields, cancellationToken);
                hits = faceted.Results;
                facets = faceted.Buckets;
            }
            else
            {
                hits = index.Search(parameters.Query, queryOptions, cancellationToken);
            }

            // After the search, because a query the engine refuses is refused once and reported as
            // such; describing it first would turn the same refusal into an unlabelled failure.
            if (parameters.Stats)
                terms = index.DescribeQuery(parameters.Query);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            // The engine refuses a query it will not run — too many expansion atoms, syntax this
            // index cannot honor — and each refusal is about the request, not about this server.
            // Mapping it to 400 keeps the endpoint's contract: a 5xx says retry, and retrying the
            // same query cannot work.
            return Results.BadRequest(new LexiSharpSearchError("q", exception.Message));
        }

        var response = new LexiSharpSearchResponse<TDocument>(
            parameters.Query,
            hits.Select(hit => new LexiSharpSearchHit<TDocument>(
                hit.DocumentId,
                hit.Score,
                hit.Document,
                hit.HighlightedText)).ToList())
        {
            Terms = terms,
            Facets = facets,
        };

        return Results.Ok(response);
    }
}