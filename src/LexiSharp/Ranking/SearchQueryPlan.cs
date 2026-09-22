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