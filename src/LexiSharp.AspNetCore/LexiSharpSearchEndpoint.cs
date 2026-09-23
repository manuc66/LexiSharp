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
    /// <summary>Runs a search on the index and builds the HTTP response.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="index"/> or <paramref name="parameters"/> is null.</exception>
    public static IResult Handle<TDocument>(
        LexiSharpIndex<TDocument> index,
        LexiSharpSearchParameters parameters)
        where TDocument : class
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(parameters);

        if (string.IsNullOrWhiteSpace(parameters.Query))
            return Results.BadRequest(new LexiSharpSearchError("q", "The 'q' query parameter is required."));

        var queryOptions = new LexiSharpQueryOptions(
            Limit: parameters.Limit,
            MinimumScore: parameters.MinimumScore,
            Offset: parameters.Offset,
            Highlight: parameters.Highlight);

        var hits = index.Search(parameters.Query, queryOptions);

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