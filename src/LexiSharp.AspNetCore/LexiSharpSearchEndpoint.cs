using LexiSharp;
using Microsoft.AspNetCore.Http;

namespace LexiSharp.AspNetCore;

/// <summary>One hit of a <see cref="LexiSharpSearchResponse{TDocument}"/>.</summary>
/// <param name="DocumentId">Identifier of the matched document.</param>
/// <param name="Score">Relevance score produced by the underlying scorer. Higher is better.</param>
/// <param name="Document">The original caller document that matched.</param>
/// <param name="HighlightedText">
/// The indexed text with query terms wrapped in highlight tags when <c>highlight</c> was
/// requested; <c>null</c> otherwise.
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
public sealed record LexiSharpSearchResponse<TDocument>(
    string Query,
    IReadOnlyList<LexiSharpSearchHit<TDocument>> Hits)
    where TDocument : class;

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

        var queryOptions = new LexiSharpQueryOptions(
            Limit: parameters.Limit,
            MinimumScore: parameters.MinimumScore,
            Offset: parameters.Offset,
            Highlight: parameters.Highlight);

        IReadOnlyList<LexiSharpHit<TDocument>> hits;

        try
        {
            hits = index.Search(parameters.Query, queryOptions, cancellationToken);
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
                hit.HighlightedText)).ToList());

        return Results.Ok(response);
    }
}