using LexiSharp.Core;

namespace LexiSharp.Hybrid;

/// <summary>
/// A merger that can state the arithmetic behind the list it returned, without keeping any state
/// between searches.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="IResultMerger.Merge"/> returns scores but not how they were produced, so a weighted
/// blend is visible in the results and not checkable from them. This interface is the extra reading:
/// the weights and divisors that took the per-engine scores to the merged ones.
/// </para>
/// <para>
/// <b>Explain recomputes; it does not remember.</b> A merger is shared across concurrent searches,
/// so a "last explanation" field would hand one search another's numbers under load. Taking the same
/// input <see cref="IResultMerger.Merge"/> took, and deriving the same values from it, is what makes
/// an explanation safe to ask for on a shared instance — and it costs one pass over the candidate
/// lists, paid only by a caller that asks.
/// </para>
/// <para>
/// A rank-only merger (reciprocal rank fusion, for one) does not implement this: it reads order and
/// never multiplies a score, so there are no weights or divisors to report and reporting zeros would
/// be a more confident lie than an absent interface.
/// </para>
/// </remarks>
public interface IMergeExplainer
{
    /// <summary>
    /// The weights and normalisation a merge over <paramref name="perEngineResults"/> applies, in
    /// the same engine order.
    /// </summary>
    /// <param name="perEngineResults">
    /// One ordered list per source engine, the same input <see cref="IResultMerger.Merge"/> takes.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// The configured weights do not cover this many engines — the same condition
    /// <see cref="IResultMerger.Merge"/> refuses, refused here so an explanation can never be
    /// computed for a merge that would not have run.
    /// </exception>
    MergeExplanation Explain(IReadOnlyList<IReadOnlyList<SearchResult>> perEngineResults);
}
