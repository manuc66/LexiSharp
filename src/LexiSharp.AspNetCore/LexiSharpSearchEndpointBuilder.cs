using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace LexiSharp.AspNetCore;

/// <summary>
/// Minimal-API wiring for the <see cref="LexiSharpSearchEndpoint"/> handler.
/// </summary>
public static class LexiSharpSearchEndpointBuilder
{
    /// <summary>
    /// Maps a <c>GET</c> search route backed by a <see cref="LexiSharpIndex{TDocument}"/>
    /// resolved from the application services.
    /// </summary>
    /// <remarks>
    /// Register the index in DI first, for example
    /// <c>builder.Services.AddSingleton(new LexiSharpIndex&lt;MyDocument&gt;(o =&gt; { ... }));</c>.
    /// Query-string parameters mirror <see cref="LexiSharpSearchParameters"/>: <c>q</c>,
    /// <c>limit</c>, <c>offset</c>, <c>minimumScore</c> and <c>highlight</c>.
    /// </remarks>
    /// <param name="endpoints">The route builder of the application or a convention group.</param>
    /// <param name="pattern">Route pattern, default <c>/search</c>.</param>
    public static IEndpointConventionBuilder MapLexiSharpSearch<TDocument>(
        this IEndpointRouteBuilder endpoints,
        string pattern = "/search")
        where TDocument : class
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);

        return endpoints.MapGet(
            pattern,
            (
                LexiSharpIndex<TDocument> index,
                [FromQuery(Name = "q")] string q,
                [FromQuery] int limit = 10,
                [FromQuery] int offset = 0,
                [FromQuery] double minimumScore = double.NegativeInfinity,
                [FromQuery] bool highlight = false) =>
                LexiSharpSearchEndpoint.Handle(
                    index,
                    new LexiSharpSearchParameters
                    {
                        Query = q ?? string.Empty,
                        Limit = limit,
                        Offset = offset,
                        MinimumScore = minimumScore,
                        Highlight = highlight,
                    }));
    }
}