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
/// <param name="RetrievedScores">
/// Score of each retrieved document, positionally aligned with <paramref name="RetrievedIds"/>.
/// Carried so a comparison can tell a genuine re-ordering from two documents that merely tie —
/// the ranking breaks ties on corpus order, so swapping two equal scores is not a behaviour change
/// and reporting it as one would train people to ignore the report.
/// </param>
public sealed record BenchmarkQueryResult(
    string QueryId,
    string QueryText,
    BenchmarkMetrics Metrics,
    IReadOnlyList<string> RetrievedIds,
    int? FirstRelevantRank,
    IReadOnlyList<double>? RetrievedScores = null)
{
    /// <summary>True when at least one judged document made the retrieved page.</summary>
    public bool RetrievedRelevant => FirstRelevantRank.HasValue;

    /// <summary>
    /// Whether the document at <paramref name="index"/> (0-based) shares its score with either
    /// neighbour in this result. A document that moved while tied with its neighbour moved for a
    /// reason that is not a behaviour change: the ranking breaks ties on corpus order, so their
    /// relative position carries no information. False when the scores are unknown — an unknown
    /// score is not evidence of a tie.
    /// </summary>
    /// <param name="index">0-based position to test.</param>
    public bool TiesWithNeighbour(int index)
    {
        if (RetrievedScores is null || index < 0 || index >= RetrievedScores.Count)
            return false;

        if (index > 0 && RetrievedScores[index - 1] == RetrievedScores[index])
            return true;

        return index < RetrievedScores.Count - 1 && RetrievedScores[index] == RetrievedScores[index + 1];
    }
}
