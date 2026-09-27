namespace LexiSharp.Benchmarking;

/// <summary>
/// The outcome of benchmarking one query: its own metrics, the ids the engine returned, and
/// where the first judged document landed.
/// </summary>
/// <param name="QueryId">The query's id, joining back to <see cref="BenchmarkQuery.Id"/>.</param>
/// <param name="QueryText">The raw query.</param>
/// <param name="Metrics">This query's metrics alone — not averaged over anything.</param>
/// <param name="RetrievedIds">Document ids in the order the engine returned them.</param>
/// <param name="FirstRelevantRank">
/// 1-based position of the best-ranked judged document, or <c>null</c> when none of the judged
/// documents made the retrieved page. This is the single most useful fact when a metric moves: it
/// says whether a document was found late, found late <em>and</em> outranked, or never found at
/// all, which are three different bugs.
/// </param>
public sealed record BenchmarkQueryResult(
    string QueryId,
    string QueryText,
    BenchmarkMetrics Metrics,
    IReadOnlyList<string> RetrievedIds,
    int? FirstRelevantRank)
{
    /// <summary>True when at least one judged document made the retrieved page.</summary>
    public bool RetrievedRelevant => FirstRelevantRank.HasValue;
}
