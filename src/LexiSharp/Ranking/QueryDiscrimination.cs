using LexiSharp.Core;

namespace LexiSharp.Ranking;

/// <summary>
/// Whether a query's <em>scores</em> separate the documents it returned, decided from the scores
/// alone and with no relevance judgement.
/// </summary>
/// <remarks>
/// <para>
/// The sibling axis of "can this query and this document meet at all": this one asks « once they
/// meet, does the score tell them apart ». They fail independently — a query can match forty
/// documents and still rank none of them, which is the case a page of results hides and a mean
/// metric averages away.
/// </para>
/// <para>
/// The order is the order of increasing information, and <see cref="QueryDiscrimination"/> tests it
/// in that order: no result at all, then one document (nothing to rank), then a single score for
/// every document (nothing was ranked), then a tie at the page cut (the page's membership is a
/// choice), then a tie inside the page (the page's order is a choice).
/// </para>
/// <para>
/// <b>Exact score equality, not a tolerance.</b> Two scores are tied when the scorer produced the
/// same <see cref="double"/>, because that is the only equality the next stage will see. A tolerance
/// would invent ties the sorter does not have, and a tie reported where the engine orders
/// deterministically is a false alarm on every query.
/// </para>
/// </remarks>
public enum DiscriminationVerdict
{
    /// <summary>The query returned nothing: a recall failure, not a ranking one.</summary>
    NoResults,

    /// <summary>One document. There is no pair to order, so nothing can be arbitrary.</summary>
    SingleResult,

    /// <summary>
    /// Every returned document carries the same score. The whole order was decided by the tie-break,
    /// so the scorer contributed none of it — the prohibited-only and boolean paths reach this by
    /// construction.
    /// </summary>
    NoDiscrimination,

    /// <summary>
    /// A tie crosses the page boundary: which documents make the page is arbitrary, even when the
    /// scores inside it are distinct.
    /// </summary>
    TiedAtCut,

    /// <summary>
    /// The page holds a tie but the boundary does not, so which documents are on the page is
    /// determined and their order is not.
    /// </summary>
    TiedWithinPage,

    /// <summary>
    /// The page ends exactly at the cut and holds no tie, so a tie past the cut cannot be ruled out.
    /// Reporting <see cref="Determined"/> here would claim knowledge the input does not carry.
    /// </summary>
    CutUnobserved,

    /// <summary>Every score in the page is distinct: the page and its order were decided by the scores.</summary>
    Determined,
}

/// <summary>One query's score structure, measured once and read by every consumer.</summary>
/// <param name="QueryId">The query this profile describes.</param>
/// <param name="Hits">How many documents the query returned.</param>
/// <param name="DistinctScores">How many different scores those documents carry.</param>
/// <param name="LargestClass">
/// The size of the largest set of documents sharing one score. A large class deep in the ranking is
/// an index or query-breadth signal; a large class at the top is the same signal where it matters.
/// </param>
/// <param name="DistinctInPage">How many different scores the page itself carries.</param>
/// <param name="CutStraddled">Whether one score reaches both sides of the page boundary.</param>
/// <param name="PageTied">Whether the page contains a score more than once.</param>
/// <param name="TruncatedAtCut">
/// Whether the list ended exactly at the cut, which is the one case where a class past the cut is
/// invisible. False when the caller passed more than the page or fewer results than it asked for.
/// </param>
/// <param name="ScoreRange">Top score minus bottom score over the whole result list; zero when empty.</param>
/// <param name="Verdict">The strongest statement the facts support, in the order they are tested.</param>
/// <remarks>
/// The two questions a caller actually asks are kept apart on purpose. A metric that ignores order
/// inside the page — precision, recall, hit rate — is determined whenever the membership is, so
/// <see cref="PageMembershipDetermined"/> is the guard for those; a graded metric such as nDCG is
/// determined only when <see cref="PageOrderDetermined"/> holds as well. Collapsing the two into one
/// boolean would let a caller report a stable number it cannot defend.
/// </remarks>
public sealed record DiscriminationProfile(
    string QueryId,
    int Hits,
    int DistinctScores,
    int LargestClass,
    int DistinctInPage,
    bool CutStraddled,
    bool PageTied,
    bool TruncatedAtCut,
    double ScoreRange,
    DiscriminationVerdict Verdict)
{
    /// <summary>Which documents are on the page is not a choice.</summary>
    public bool PageMembershipDetermined => !CutStraddled && !TruncatedAtCut;

    /// <summary>Which order the page is shown in is not a choice.</summary>
    public bool PageOrderDetermined => !PageTied && !CutStraddled;

    /// <summary>
    /// A one-line reason for the verdict, in the same voice as the rest of the library: what was
    /// measured, not what it might mean.
    /// </summary>
    public string Explain() => Verdict switch
    {
        DiscriminationVerdict.NoResults =>
            "no document matched; this is a recall failure, not a ranking one",
        DiscriminationVerdict.SingleResult =>
            "one document returned; there is nothing to order",
        DiscriminationVerdict.NoDiscrimination =>
            $"{Hits} documents share a single score; their order is entirely the tie-break's",
        DiscriminationVerdict.TiedAtCut =>
            "a tie crosses the page cut; which documents make the page is arbitrary",
        DiscriminationVerdict.TiedWithinPage =>
            "the page holds a tie; which order it is shown in is arbitrary",
        DiscriminationVerdict.CutUnobserved =>
            "the page ends at the cut; a tie past it cannot be ruled out",
        _ => "every score in the page is distinct; the page and its order are decided by the scores",
    };
}

/// <summary>
/// Measures whether a result list discriminates, from the scores alone.
/// </summary>
/// <remarks>
/// <para>
/// This is the prerequisite of any tie-aware metric: before computing what a ranking is worth under
/// uniform random tie-breaking — the protocol of Ma &amp; Cheng, RecSys 2026, and of Guo et al.,
/// FRAME 2026 — a caller has to know whether ties exist and where. Both papers found the same
/// failure: a compressed score (an extra sigmoid before BPR; a constant score in a sampled
/// evaluation) turns the tie-break into the ranker. The measurement here is the diagnostic half of
/// that finding, and it needs no qrels, so it can run on live traffic.
/// </para>
/// <para>
/// The tie-aware metrics themselves are not computed here. They need the relevance grades, and
/// <see cref="RetrievalMetrics.TieAwareNdcgAtK(IReadOnlyList{SearchResult}, IReadOnlyDictionary{string, double}, int, NdcgGain)"/>
/// is where they live. This type stops at the fact the metric rests on.
/// </para>
/// <para>
/// Cost is one pass and one dictionary over the returned list, which the caller already holds: no
/// index, no second search, no allocation proportional to the corpus.
/// </para>
/// </remarks>
public static class QueryDiscrimination
{
    /// <summary>
    /// Profiles one query's result list at a page depth.
    /// </summary>
    /// <param name="queryId">The query the list answers.</param>
    /// <param name="ranked">
    /// The ranked results, best first. Not re-sorted: a tie is read where the engine put it.
    /// <para>
    /// <b>Pass the full list, or one result past the page.</b> A class that extends past the cut is
    /// what <see cref="DiscriminationVerdict.TiedAtCut"/> is about, and a list that ends exactly at
    /// the cut cannot show it — the profile then reports <see cref="DiscriminationVerdict.CutUnobserved"/>
    /// rather than claiming a determination it cannot support.
    /// </para>
    /// </param>
    /// <param name="cutoff">
    /// The page depth the caller will show or measure — the same number the search was asked for.
    /// A tie that straddles this position is the one that changes what a page of this depth contains.
    /// </param>
    /// <param name="complete">
    /// Whether <paramref name="ranked"/> is the whole result set rather than a prefix of it. Only the
    /// caller knows: a list of exactly <paramref name="cutoff"/> items is complete when the index held
    /// no more, and truncated when the engine stopped at the page. Defaults to <c>false</c>, the
    /// conservative reading — an unobserved tie is reported as unobserved rather than as absent.
    /// </param>
    public static DiscriminationProfile Measure(
        string queryId,
        IReadOnlyList<SearchResult> ranked,
        int cutoff,
        bool complete = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queryId);
        ArgumentNullException.ThrowIfNull(ranked);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cutoff);

        if (ranked.Count == 0)
            return new DiscriminationProfile(
                queryId, 0, 0, 0, 0, false, false, false, 0, DiscriminationVerdict.NoResults);

        int pageEnd = Math.Min(cutoff, ranked.Count);

        // How many documents carry each score, over the whole list. Exact double equality: the same
        // comparison the next sort would make.
        var classSizes = new Dictionary<double, int>(ranked.Count);
        double lowest = ranked[0].Score;
        double highest = ranked[0].Score;

        for (int i = 0; i < ranked.Count; i++)
        {
            double score = ranked[i].Score;
            classSizes[score] = classSizes.TryGetValue(score, out int size) ? size + 1 : 1;

            if (score < lowest) lowest = score;
            if (score > highest) highest = score;
        }

        // How many of each score's documents fell inside the page. A score whose page count is
        // smaller than its total count has documents on both sides of the cut.
        var inPage = new Dictionary<double, int>(pageEnd);
        for (int i = 0; i < pageEnd; i++)
        {
            double score = ranked[i].Score;
            inPage[score] = inPage.TryGetValue(score, out int seen) ? seen + 1 : 1;
        }

        int largestClass = 0;
        bool cutStraddled = false;
        bool pageTied = false;

        foreach ((double score, int pageCount) in inPage)
        {
            int total = classSizes[score];
            if (total > largestClass) largestClass = total;
            if (total > pageCount) cutStraddled = true;
            if (pageCount > 1) pageTied = true;
        }

        // The largest class can sit entirely past the page; the verdict must still see it.
        foreach (int total in classSizes.Values)
        {
            if (total > largestClass) largestClass = total;
        }

        // A list that ends exactly at the cut cannot say whether a class continues past it, unless
        // the caller says the engine had nothing more to give.
        bool truncatedAtCut = ranked.Count == cutoff && !complete;

        DiscriminationVerdict verdict =
            ranked.Count == 1 ? DiscriminationVerdict.SingleResult
            : classSizes.Count == 1 ? DiscriminationVerdict.NoDiscrimination
            : cutStraddled ? DiscriminationVerdict.TiedAtCut
            : pageTied ? DiscriminationVerdict.TiedWithinPage
            : truncatedAtCut ? DiscriminationVerdict.CutUnobserved
            : DiscriminationVerdict.Determined;

        return new DiscriminationProfile(
            queryId,
            ranked.Count,
            classSizes.Count,
            largestClass,
            inPage.Count,
            cutStraddled,
            pageTied,
            truncatedAtCut,
            highest - lowest,
            verdict);
    }
}
