using LexiSharp.Core;

namespace LexiSharp.Hybrid;

/// <summary>
/// A result merger that can use each source engine's query context when the hybrid has one.
/// </summary>
/// <remarks>
/// <para>
/// The inherited <see cref="IResultMerger.Merge"/> remains the contract for a caller that has no
/// contexts. This overload is what lets a score-blending merger divide by a query bound instead of a
/// page maximum without changing the public merge signature every existing merger implements.
/// </para>
/// <para>
/// The contexts are aligned with the result lists and may contain <c>null</c> entries. A merger that
/// does not implement this interface is called through <see cref="IResultMerger.Merge"/> and behaves
/// exactly as before, which is why adding the capability changes no existing pipeline.
/// </para>
/// </remarks>
public interface IContextualResultMerger : IResultMerger
{
    /// <summary>
    /// Whether this merger will use a context when one is supplied.
    /// </summary>
    /// <remarks>
    /// A hybrid reads this before asking its engines for a bound, because asking costs a query
    /// tokenization per engine. A merger whose normalizer measures the list — the default
    /// <see cref="MaxScoreNormalizer"/>, for one — answers <c>false</c> and pays nothing, which is
    /// what keeps an existing weighted pipeline's per-query work unchanged.
    /// </remarks>
    bool UsesContexts { get; }

    /// <summary>
    /// Merges per-engine results, using each engine's context where one was supplied.
    /// </summary>
    /// <param name="perEngineResults">One ordered list per source engine, already filtered by the engines.</param>
    /// <param name="query">The raw query text.</param>
    /// <param name="contexts">
    /// One context per engine, in the same order, or <c>null</c> when no engine reported one. An entry
    /// may itself be <c>null</c> for an engine that could not bound the query.
    /// </param>
    IReadOnlyList<SearchResult> Merge(
        IReadOnlyList<IReadOnlyList<SearchResult>> perEngineResults,
        string query,
        IReadOnlyList<ScoreNormalizationContext?>? contexts);
}

/// <summary>
/// A merge explainer that can report the arithmetic a contextual merge actually used.
/// </summary>
/// <remarks>
/// Without this overload a trace would explain the fallback mapping while the merge used the query
/// bound, which is the one thing an explanation exists to prevent. A merger that does not implement it
/// keeps the historical explanation.
/// </remarks>
public interface IContextualMergeExplainer : IMergeExplainer
{
    /// <summary>
    /// The weights and normalisation a contextual merge applies, in the same engine order.
    /// </summary>
    /// <param name="perEngineResults">One ordered list per source engine.</param>
    /// <param name="contexts">One context per engine, in the same order, or <c>null</c>.</param>
    MergeExplanation Explain(
        IReadOnlyList<IReadOnlyList<SearchResult>> perEngineResults,
        IReadOnlyList<ScoreNormalizationContext?>? contexts);
}
