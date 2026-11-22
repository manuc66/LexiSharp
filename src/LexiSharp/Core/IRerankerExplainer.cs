namespace LexiSharp.Core;

/// <summary>
/// Optional capability of an <see cref="IReranker"/> that can justify its re-ordering: it produces
/// a <see cref="RerankExplanation"/> decomposing the contribution of every signal it used.
/// </summary>
/// <remarks>
/// Implementing this interface is opt-in. Use pattern matching (<c>reranker is IRerankerExplainer</c>)
/// or <see cref="RerankerExtensions.ExplainRerank"/>, which returns <c>null</c> when the reranker
/// cannot explain itself. The explanation is computed lazily, so the hot path of
/// <see cref="IReranker.Rerank"/> stays allocation-free.
/// </remarks>
public interface IRerankerExplainer : IReranker
{
    /// <summary>
    /// Explains how <paramref name="documentId"/> scored in the rerank of <paramref name="candidates"/>
    /// for <paramref name="query"/>.
    /// </summary>
    RerankExplanation? ExplainRerank(
        string query,
        IReadOnlyList<SearchResult> candidates,
        string documentId);
}

/// <summary>Convenience extensions for <see cref="IRerankerExplainer"/>.</summary>
public static class RerankerExtensions
{
    /// <summary>
    /// Returns an explanation when <paramref name="reranker"/> implements
    /// <see cref="IRerankerExplainer"/>, otherwise <c>null</c>.
    /// </summary>
    public static RerankExplanation? ExplainRerank(
        this IReranker reranker,
        string query,
        IReadOnlyList<SearchResult> candidates,
        string documentId)
        => reranker is IRerankerExplainer explainer
            ? explainer.ExplainRerank(query, candidates, documentId)
            : null;
}