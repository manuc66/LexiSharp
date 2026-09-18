using LexiSharp.Core;

namespace LexiSharp.Hybrid;

/// <summary>
/// Strategy that turns several ranked result lists — one per source engine — into a single
/// coherent, de-duplicated, ordered list.
/// </summary>
/// <remarks>
/// The whole point of a merger is to give the hybrid a <b>consistent global ordering</b>:
/// concatenating raw per-engine ranks is almost never the answer, because BM25/ts_rank/vector
/// similarity live on unrelated numeric scales. Implementations either re-score the union of
/// candidates with a single scorer (<see cref="RerankingResultMerger"/>) or blend the
/// per-engine scores (optionally weighted) after normalizing them (<see cref="WeightedScoreResultMerger"/>).
/// </remarks>
public interface IResultMerger
{
    /// <summary>Human readable name of the merge strategy, e.g. <c>"Rerank"</c>.</summary>
    string Name { get; }

    /// <summary>
    /// Merges per-engine results into one ordered list. Must not mutate the inputs.
    /// </summary>
    /// <param name="perEngineResults">One ordered list per source engine, already filtered by the engines.</param>
    /// <param name="query">The raw query text (re-tokenized internally if re-ranking).</param>
    IReadOnlyList<SearchResult> Merge(
        IReadOnlyList<IReadOnlyList<SearchResult>> perEngineResults,
        string query);
}