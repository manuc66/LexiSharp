using LexiSharp.Core;

namespace LexiSharp.Ranking;

/// <summary>
/// A query-bound, per-corpus preparation performed once by an <see cref="ITextScorer"/> so
/// that corpus statistics that do not vary per document (document frequencies, idf weights,
/// collection probabilities, average document length, ...) are looked up a single time per
/// search instead of once per candidate document.
/// </summary>
/// <remarks>
/// Scoring still reads per-document statistics (term frequency, document length) from the
/// index on every call; only query- and corpus-level constants are hoisted. A plan must
/// produce exactly the same score as the plain <see cref="ITextScorer.Score"/> for the same
/// inputs, and it is safe to create one plan per query and reuse it across documents.
/// </remarks>
internal interface ISearchQueryPlan
{
    /// <summary>Scores a single document against the query the plan was prepared for.</summary>
    double Score(string documentId);
}

/// <summary>
/// Capability for a scorer that can precompute its query-level constants (see
/// <see cref="ISearchQueryPlan"/>), letting the search engine avoid redundant corpus lookups
/// per candidate document.
/// </summary>
internal interface IQueryPlannableScorer : ITextScorer
{
    /// <summary>Builds a reusable plan for the given tokenized query against the corpus.</summary>
    ISearchQueryPlan CreatePlan(IReadOnlyList<string> queryTerms, ITextIndex index);
}

/// <summary>
/// A plan whose score decomposes into a per-term contribution that depends only on the
/// document's term frequency and length, so the whole query can be scored in one term-at-a-time
/// pass over the inverted lists (see <see cref="IAccumulatingIndex"/>).
/// </summary>
/// <remarks>
/// <para>
/// The engine prefers this path and falls back to <see cref="ISearchQueryPlan.Score"/> per
/// document when the index cannot offer it, when a positional or metadata gate has to run first,
/// or when the plan declines (an empty corpus, a zero average length). Both paths must produce the
/// same doubles, not merely the same ranking: <c>QueryPlanParityTests</c> pins that, and
/// <c>TopRankedWindow</c> breaks ties on <c>==</c>, so a last-bit difference would reorder the
/// page.
/// </para>
/// <para>
/// A scorer opts in by having its contribution depend on nothing but (term, tf, length). The
/// query-wide constants — idf, <c>k1</c>, <c>b</c>, <c>δ</c>, the average document length — are
/// captured in the per-term weight. A scorer whose score also depends on documents the term is
/// <i>absent</i> from (query likelihood's <c>log P(t|d)</c> smoothing) or on per-field geometry
/// (BM25F) cannot, and stays on the per-document path.
/// </para>
/// </remarks>
internal interface IAccumulatingQueryPlan : ISearchQueryPlan
{
    /// <summary>
    /// Folds every query term into <paramref name="accumulator"/>, in query order, so each
    /// document's score is summed in the same order the per-document loop would have summed it.
    /// </summary>
    /// <returns>
    /// <c>false</c> when the plan cannot run (no corpus, no average length); the caller then falls
    /// back to <see cref="ISearchQueryPlan.Score"/>. <c>true</c> on success, with the accumulator
    /// reset to empty first.
    /// </returns>
    bool TryAccumulate(IAccumulatingIndex index, ScoreAccumulator accumulator);

    /// <summary>
    /// The last step on a score the accumulator produced, after the accumulation loop is finished.
    /// </summary>
    /// <remarks>
    /// It exists because the accumulation path sums a document's postings without ever calling
    /// <see cref="ISearchQueryPlan.Score"/>, so anything that belongs at the end of a score and not at
    /// the end of a sum has to be reachable from here. A scorer whose score is narrowed to a single
    /// precision at the end needs it; one that does not has nothing to do.
    /// <para>
    /// The default returns the sum untouched, so an implementation that has no such step needs nothing.
    /// </para>
    /// </remarks>
    double Finalise(double accumulated) => accumulated;
}