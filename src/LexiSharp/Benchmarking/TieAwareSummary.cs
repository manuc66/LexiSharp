using LexiSharp.Core;

namespace LexiSharp.Benchmarking;

/// <summary>
/// What a configuration's per-query tie measurements add up to: how often the score failed to
/// separate the documents, and how much of the reported nDCG the tie-break owns.
/// </summary>
/// <param name="Queries">Judged queries the configuration ran.</param>
/// <param name="Measured">
/// Queries whose tie-aware nDCG could be computed exactly. A query whose top score class reaches
/// past the page cut is excluded, because the missing part of the class makes the expectation
/// unrecoverable — that is the <see cref="TiedAtCut"/> count.
/// </param>
/// <param name="MeanExpectedNdcg">Mean expectation over <paramref name="Measured"/>; 0 when none.</param>
/// <param name="MeanNdcgOverMeasured">
/// Mean plain nDCG over the same <paramref name="Measured"/> queries, so the two means share a
/// denominator. Comparing the expectation with the table's mean would compare 21 queries with 22
/// whenever one query's tie reached past the cut.
/// </param>
/// <param name="MeanWidth">Mean interval width over <paramref name="Measured"/>; 0 when none.</param>
/// <param name="MaxWidth">Widest interval seen — the worst single query.</param>
/// <param name="Determined">Queries whose page and order were decided by distinct scores.</param>
/// <param name="TiedWithinPage">Queries whose page order, but not its membership, was arbitrary.</param>
/// <param name="TiedAtCut">Queries whose page membership was arbitrary (a tie crossed the cut).</param>
/// <param name="CutUnobserved">Queries whose page ended at the cut, leaving a tie past it unobservable.</param>
/// <param name="NoDiscrimination">Queries whose every document shared one score.</param>
/// <param name="SingleResult">Queries that returned exactly one document.</param>
/// <param name="NoResults">Queries that returned nothing.</param>
/// <remarks>
/// The counts are over every query that carries a <see cref="DiscriminationProfile"/>, which is
/// every judged query the harness scored. They are deliberately reported separately from the metric
/// means: a configuration can be numerically identical and still differ in how much of its ordering
/// was arbitrary, and averaging those away is what the tie-aware protocol exists to stop.
/// </remarks>
public sealed record TieAwareSummary(
    int Queries,
    int Measured,
    int Determined,
    int TiedWithinPage,
    int TiedAtCut,
    int CutUnobserved,
    int NoDiscrimination,
    int SingleResult,
    int NoResults,
    double MeanExpectedNdcg,
    double MeanNdcgOverMeasured,
    double MeanWidth,
    double MaxWidth)
{
    /// <summary>Queries whose page membership and order were decided by the scores alone.</summary>
    public int Clean => Determined;

    /// <summary>Queries whose ordering, membership, or both were left to the tie-break.</summary>
    public int Affected => TiedWithinPage + TiedAtCut + NoDiscrimination;

    /// <summary>
    /// Summarises a configuration's per-query results. Results without a profile are ignored, so a
    /// run produced outside the harness reports zero measurements rather than a fabricated one.
    /// </summary>
    public static TieAwareSummary Of(IEnumerable<BenchmarkQueryResult> perQuery)
    {
        ArgumentNullException.ThrowIfNull(perQuery);

        int queries = 0, measured = 0, determined = 0, withinPage = 0, atCut = 0;
        int unobserved = 0, noDiscrimination = 0, single = 0, none = 0;
        double expectedSum = 0, ndcgSum = 0, widthSum = 0, maxWidth = 0;

        foreach (var result in perQuery)
        {
            queries++;

            if (result.Discrimination is { } profile)
            {
                switch (profile.Verdict)
                {
                    case DiscriminationVerdict.Determined: determined++; break;
                    case DiscriminationVerdict.TiedWithinPage: withinPage++; break;
                    case DiscriminationVerdict.TiedAtCut: atCut++; break;
                    case DiscriminationVerdict.CutUnobserved: unobserved++; break;
                    case DiscriminationVerdict.NoDiscrimination: noDiscrimination++; break;
                    case DiscriminationVerdict.SingleResult: single++; break;
                    default: none++; break;
                }
            }

            if (result.TieAwareNdcg is { } ndcg)
            {
                measured++;
                expectedSum += ndcg.Expected;
                ndcgSum += result.Metrics.NdcgAtK;
                widthSum += ndcg.Width;
                if (ndcg.Width > maxWidth) maxWidth = ndcg.Width;
            }
        }

        return new TieAwareSummary(
            queries,
            measured,
            determined,
            withinPage,
            atCut,
            unobserved,
            noDiscrimination,
            single,
            none,
            measured == 0 ? 0 : expectedSum / measured,
            measured == 0 ? 0 : ndcgSum / measured,
            measured == 0 ? 0 : widthSum / measured,
            maxWidth);
    }
}
