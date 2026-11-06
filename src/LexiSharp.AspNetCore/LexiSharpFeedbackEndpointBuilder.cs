using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace LexiSharp.AspNetCore;

/// <summary>Minimal-API wiring for the <see cref="LexiSharpFeedbackEndpoint"/> handler.</summary>
public static class LexiSharpFeedbackEndpointBuilder
{
    /// <summary>
    /// Maps a <c>POST</c> feedback route backed by a <see cref="LexiSharpIndex{TDocument}"/>
    /// resolved from the application services. The index must be wrapped in a
    /// <see cref="LexiSharp.Ranking.FeedbackAwareTextSearchEngine"/> for the route to do anything;
    /// otherwise it answers <c>404</c>.
    /// </summary>
    public static IEndpointConventionBuilder MapLexiSharpFeedback<TDocument>(
        this IEndpointRouteBuilder endpoints,
        string pattern = "/search/feedback")
        where TDocument : class
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);

        return endpoints.MapPost(
            pattern,
            (LexiSharpIndex<TDocument> index, LexiSharpFeedbackRequest request) =>
                LexiSharpFeedbackEndpoint.Handle(index, request));
    }
}