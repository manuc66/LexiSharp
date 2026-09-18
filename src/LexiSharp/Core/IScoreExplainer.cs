namespace LexiSharp.Core;

/// <summary>
/// Optional capability of an <see cref="ITextScorer"/> that can justify its scores:
/// it produces a <see cref="ScoreExplanation"/> decomposing the contribution of every
/// matched query term.
/// </summary>
/// <remarks>
/// Implementing this interface is opt-in — plain scorers keep their single
/// <see cref="ITextScorer.Score"/> contract. Use pattern matching
/// (<c>scorer is IScoreExplainer</c>) or <c>RankedTextSearchEngine.Explain</c>, which
/// returns <c>null</c> when the active scorer cannot explain itself.
/// </remarks>
public interface IScoreExplainer : ITextScorer
{
    /// <summary>
    /// Explains how the document scored for the given tokenized query.
    /// </summary>
    /// <param name="documentId">Id of the document to explain.</param>
    /// <param name="queryTerms">Terms of the query, already tokenized.</param>
    /// <param name="index">The shared index holding corpus statistics.</param>
    ScoreExplanation Explain(string documentId, IReadOnlyList<string> queryTerms, ITextIndex index);
}