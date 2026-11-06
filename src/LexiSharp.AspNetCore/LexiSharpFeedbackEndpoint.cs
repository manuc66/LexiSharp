using LexiSharp;
using LexiSharp.Ranking;
using Microsoft.AspNetCore.Http;

namespace LexiSharp.AspNetCore;

/// <summary>The payload of a feedback request: which document was chosen for which query.</summary>
/// <param name="Query">The query the choice was made for.</param>
/// <param name="DocumentId">The identifier of the chosen document.</param>
/// <param name="Reward">
/// Optional strength of the choice. Defaults to <c>1.0</c>, which is what a plain click contributes.
/// </param>
public sealed record LexiSharpFeedbackRequest(
    string Query,
    string DocumentId,
    double Reward = 1.0);

/// <summary>The payload of a failed feedback request.</summary>
/// <param name="Field">The field the problem is about.</param>
/// <param name="Message">Human-readable description of the problem.</param>
public sealed record LexiSharpFeedbackError(string Field, string Message);

/// <summary>
/// The testable heart of the feedback endpoint: an HTTP-agnostic function from an index and a
/// feedback request to an <see cref="IResult"/>. The minimal-API extension only wires the route to
/// this method, so the whole behavior is covered without hosting an application.
/// </summary>
public static class LexiSharpFeedbackEndpoint
{
    /// <summary>Records a choice against the index's feedback-aware engine.</summary>
    public static IResult Handle<TDocument>(
        LexiSharpIndex<TDocument> index,
        LexiSharpFeedbackRequest request)
        where TDocument : class
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Query))
            return Results.BadRequest(new LexiSharpFeedbackError("query", "The 'query' field is required."));

        if (string.IsNullOrWhiteSpace(request.DocumentId))
            return Results.BadRequest(new LexiSharpFeedbackError("documentId", "The 'documentId' field is required."));

        if (index.Engine is not FeedbackAwareTextSearchEngine feedbackEngine)
        {
            return Results.NotFound(new LexiSharpFeedbackError(
                "engine",
                "This index is not wrapped in a FeedbackAwareTextSearchEngine, so it has no feedback channel to learn from."));
        }

        if (!double.IsFinite(request.Reward) || request.Reward < 0)
            return Results.BadRequest(new LexiSharpFeedbackError("reward", "The 'reward' must be a finite, non-negative number."));

        feedbackEngine.Learn(request.Query, request.DocumentId, request.Reward);
        return Results.NoContent();
    }
}