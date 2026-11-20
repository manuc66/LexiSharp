using LexiSharp.Ranking;

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
    /// <summary>
    /// This query's nDCG over every ordering the scores leave admissible, or <c>null</c> when the
    /// exact value cannot be recovered from the results the run kept.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A single nDCG is well defined only when the scores are distinct. When two documents share a
    /// score the engine has no order to report, and the metric becomes a property of the tie-break:
    /// Ma and Cheng (RecSys 2026) show a compressed score turning the tie-break into the ranker, and
    /// Guo et al. (FRAME 2026) show the same run scoring <c>0.85</c> or <c>0.17</c> depending on it.
    /// This property carries the exact expectation under uniform random tie-breaking and the exact
    /// best and worst case, so a report can say how much of the number the tie-break owns.
    /// </para>
    /// <para>
    /// <b>Null means not computable, never zero.</b> A class that extends past the page cut cannot be
    /// completed from a page-limited retrieval: its size and the gains of its documents past the cut
    /// are missing, and an expectation computed over the visible part would be a confident wrong
    /// answer. <see cref="Discrimination"/> says that this happened
    /// (<see cref="DiscriminationVerdict.TiedAtCut"/>); the benchmark asks the engine for one result
    /// past the page so that the case is detected rather than hidden.
    /// </para>
    /// </remarks>
    public TieAwareMetric? TieAwareNdcg { get; init; }

    /// <summary>
    /// Whether the query's scores separate the documents it returned, or <c>null</c> when the result
    /// was not produced by a harness that measures it.
    /// </summary>
    /// <remarks>
    /// Needs no relevance judgement, so it is the half of the tie story that can be measured on live
    /// traffic. <see cref="TieAwareNdcg"/> is the half that needs qrels.
    /// </remarks>
    public DiscriminationProfile? Discrimination { get; init; }

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

        // Bitwise equality, not a tolerance -- and this is load-bearing, so it must not be "fixed".
        //
        // A caller uses this to decide whether a document that moved was allowed to move: a swap
        // between two equal scores is a tie and carries no information, while a swap between scores
        // that merely differ in the last bits is a real change in the ranking. An epsilon would
        // classify the second as the first, and the golden master would excuse re-orderings caused
        // by a genuine defect. That is not hypothetical: the unstable PMI sort fixed in e81c02c
        // perturbed the last bits of a score and produced exactly such a re-ordering, which the
        // harness correctly refused to excuse ("re-ordered X and Y without a tie"). An epsilon here
        // would have silently swallowed that bug.
        if (index > 0 && RetrievedScores[index - 1] == RetrievedScores[index]) // NOSONAR:S1244
            return true;

        return index < RetrievedScores.Count - 1
            && RetrievedScores[index] == RetrievedScores[index + 1]; // NOSONAR:S1244
    }
}
