using LexiSharp.Ranking;
using Xunit;

namespace LexiSharp.Tests;

public class RetrievalMetricsTests
{
    private static readonly string[] Retrieved = ["a", "b", "c", "d", "e"];
    private static readonly string[] Relevant = ["b", "d", "f"];

    [Fact]
    public void Precision_CountsUnfilledSlotsAsMisses()
    {
        Assert.Equal(2.0 / 5, RetrievalMetrics.PrecisionAtK(Retrieved, Relevant, 5), 12);
        Assert.Equal(0.5, RetrievalMetrics.PrecisionAtK(Retrieved, Relevant, 2), 12);
        Assert.Equal(0, RetrievalMetrics.PrecisionAtK(Array.Empty<string>(), Relevant, 5), 12);
    }

    [Fact]
    public void Recall_IsRelativeToTheRelevantSet()
    {
        Assert.Equal(2.0 / 3, RetrievalMetrics.RecallAtK(Retrieved, Relevant, 5), 12);
        Assert.Equal(1.0 / 3, RetrievalMetrics.RecallAtK(Retrieved, Relevant, 2), 12);
        Assert.Equal(1, RetrievalMetrics.RecallAtK(Retrieved, ["b", "d"], 5), 12);
        Assert.Equal(0, RetrievalMetrics.RecallAtK(Retrieved, Array.Empty<string>(), 5), 12);
    }

    [Fact]
    public void F1_IsTheHarmonicMeanOfPrecisionAndRecall()
    {
        double precision = RetrievalMetrics.PrecisionAtK(Retrieved, Relevant, 5);
        double recall = RetrievalMetrics.RecallAtK(Retrieved, Relevant, 5);

        Assert.Equal(2.0 * precision * recall / (precision + recall), RetrievalMetrics.F1AtK(Retrieved, Relevant, 5), 12);
        Assert.Equal(0, RetrievalMetrics.F1AtK(Array.Empty<string>(), Relevant, 5), 12);
    }

    [Fact]
    public void Ndcg_DiscountsLaterRelevantPositionsAgainstTheIdealRanking()
    {
        // DCG = 1/log2(3) + 1/log2(5); IDCG = 1/log2(2) + 1/log2(3) + 1/log2(4).
        double dcg = 1.0 / Math.Log2(3) + 1.0 / Math.Log2(5);
        double idcg = 1.0 / Math.Log2(2) + 1.0 / Math.Log2(3) + 1.0 / Math.Log2(4);

        Assert.Equal(dcg / idcg, RetrievalMetrics.NdcgAtK(Retrieved, Relevant, 5), 12);

        // Truncated at 2, only "b" is retrieved: DCG = 1/log2(3), IDCG over 2 positions.
        double dcg2 = 1.0 / Math.Log2(3);
        double idcg2 = 1.0 / Math.Log2(2) + 1.0 / Math.Log2(3);

        Assert.Equal(dcg2 / idcg2, RetrievalMetrics.NdcgAtK(Retrieved, Relevant, 2), 12);
    }

    [Fact]
    public void PerfectRanking_ScoresOne()
    {
        Assert.Equal(1, RetrievalMetrics.PrecisionAtK(Relevant, Relevant, 3), 12);
        Assert.Equal(1, RetrievalMetrics.RecallAtK(Relevant, Relevant, 3), 12);
        Assert.Equal(1, RetrievalMetrics.F1AtK(Relevant, Relevant, 3), 12);
        Assert.Equal(1, RetrievalMetrics.NdcgAtK(Relevant, Relevant, 3), 12);
    }

    [Fact]
    public void Metrics_TruncateBeyondK()
    {
        // "f" would be relevant but sits beyond k.
        var retrieved = new[] { "a", "b", "f" };

        Assert.Equal(0.5, RetrievalMetrics.PrecisionAtK(retrieved, Relevant, 2), 12);
        Assert.Equal(1.0 / 3, RetrievalMetrics.RecallAtK(retrieved, Relevant, 2), 12);
        Assert.Equal(
            1.0 / Math.Log2(3) / (1.0 / Math.Log2(2) + 1.0 / Math.Log2(3)),
            RetrievalMetrics.NdcgAtK(retrieved, Relevant, 2), 12); // only "b" within k=2
    }

    [Fact]
    public void ReciprocalRank_IsOneOverTheFirstRelevantPosition()
    {
        // First relevant ("b") at rank 2.
        Assert.Equal(0.5, RetrievalMetrics.ReciprocalRankAtK(Retrieved, Relevant, 5), 12);
        Assert.Equal(0.5, RetrievalMetrics.ReciprocalRankAtK(Retrieved, Relevant, 2), 12);
    }

    [Fact]
    public void ReciprocalRank_ScoresZero_WhenNothingRelevantWithinK()
    {
        Assert.Equal(0, RetrievalMetrics.ReciprocalRankAtK(Retrieved, Relevant, 1), 12);
        Assert.Equal(0, RetrievalMetrics.ReciprocalRankAtK(Retrieved, Array.Empty<string>(), 5), 12);
    }

    [Fact]
    public void AveragePrecision_SumsPrecisionsAtRelevantHits()
    {
        // Hits at ranks 2 and 4 with precisions 1/2 and 2/4, normalized by min(3 relevant, 5).
        double expected = (0.5 + 0.5) / 3;

        Assert.Equal(expected, RetrievalMetrics.AveragePrecisionAtK(Retrieved, Relevant, 5), 12);
    }

    [Fact]
    public void AveragePrecision_PerfectRanking_ScoresOne()
    {
        Assert.Equal(1, RetrievalMetrics.AveragePrecisionAtK(Relevant, Relevant, 3), 12);
    }

    [Fact]
    public void AveragePrecision_RelevantBeyondKDoNotCount()
    {
        var retrieved = new[] { "a", "b" };

        // Only "b" hits within k=2: precision 1/2, normalized by min(3, 2) = 2.
        Assert.Equal(0.5 / 2, RetrievalMetrics.AveragePrecisionAtK(retrieved, Relevant, 2), 12);
    }

    [Fact]
    public void GradedNdcg_RewardsStronglyRelevantDocuments()
    {
        var graded = new Dictionary<string, double> { ["a"] = 3, ["b"] = 1, ["z"] = 2 };

        // DCG = (2^3−1)/log2(2) + (2^1−1)/log2(3); ideal gains [3,2,1] over 3 positions.
        double dcg = (Math.Pow(2, 3) - 1) / Math.Log2(2) + (Math.Pow(2, 1) - 1) / Math.Log2(3);
        double idcg = (Math.Pow(2, 3) - 1) / Math.Log2(2) + (Math.Pow(2, 2) - 1) / Math.Log2(3)
                      + (Math.Pow(2, 1) - 1) / Math.Log2(4);

        Assert.Equal(dcg / idcg, RetrievalMetrics.NdcgAtK(Retrieved, graded, 3), 12);
    }

    [Fact]
    public void GradedNdcg_PerfectOrdering_ScoresOne()
    {
        var graded = new Dictionary<string, double> { ["a"] = 3, ["b"] = 2, ["c"] = 1 };

        Assert.Equal(1, RetrievalMetrics.NdcgAtK(Retrieved, graded, 3), 12);
    }

    [Fact]
    public void GradedNdcg_UnknownDocumentsCountAsZero()
    {
        // Only "z" is graded (2): the retrieved list scores nothing.
        var graded = new Dictionary<string, double> { ["z"] = 2 };

        Assert.Equal(0, RetrievalMetrics.NdcgAtK(Retrieved, graded, 3), 12);
    }

    [Fact]
    public void GradedNdcg_EmptyOrInvalidGains()
    {
        Assert.Equal(0, RetrievalMetrics.NdcgAtK(Retrieved, new Dictionary<string, double>(), 3), 12);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => RetrievalMetrics.NdcgAtK(Retrieved, new Dictionary<string, double> { ["a"] = -1 }, 3));
    }

    [Fact]
    public void NewMetrics_ValidateArguments()
    {
        Assert.Equal(0, RetrievalMetrics.AveragePrecisionAtK(Retrieved, Array.Empty<string>(), 5), 12);
        Assert.Throws<ArgumentOutOfRangeException>(() => RetrievalMetrics.ReciprocalRankAtK(Retrieved, Relevant, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => RetrievalMetrics.AveragePrecisionAtK(Retrieved, Relevant, 0));
        Assert.Throws<ArgumentNullException>(() => RetrievalMetrics.ReciprocalRankAtK(null!, Relevant, 3));
        Assert.Throws<ArgumentNullException>(() => RetrievalMetrics.NdcgAtK(Retrieved, (IReadOnlyDictionary<string, double>)null!, 3));
    }

    [Fact]
    public void Metrics_ValidateArguments()
    {
        Assert.Throws<ArgumentNullException>(() => RetrievalMetrics.PrecisionAtK(null!, Relevant, 3));
        Assert.Throws<ArgumentNullException>(() => RetrievalMetrics.RecallAtK(Retrieved, null!, 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => RetrievalMetrics.F1AtK(Retrieved, Relevant, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => RetrievalMetrics.NdcgAtK(Retrieved, Relevant, -1));
    }

    [Fact]
    public void Ndcg_DuplicatedRelevantIds_NeverScoreAboveOne()
    {
        // An engine returning the same relevant document twice must not inflate DCG above IDCG.
        var retrieved = new[] { "a", "a", "b" };
        var relevant = new[] { "a", "b" };

        Assert.Equal(1, RetrievalMetrics.NdcgAtK(retrieved, relevant, 3), 12);
    }

    [Fact]
    public void AveragePrecision_DuplicatedRelevantIds_ScoreOnce()
    {
        var retrieved = new[] { "a", "a" };

        Assert.Equal(1, RetrievalMetrics.AveragePrecisionAtK(retrieved, new[] { "a" }, 5), 12);
    }

    [Fact]
    public void Metrics_DuplicatesBehaveLikeFirstOccurrenceOnly()
    {
        var withDups = new[] { "a", "a", "b", "d", "b" };
        var distinct = new[] { "a", "b", "d" };
        var relevant = new[] { "a", "b", "c" };
        var graded = new Dictionary<string, double> { ["a"] = 2, ["b"] = 1, ["d"] = 3 };

        Assert.Equal(
            RetrievalMetrics.NdcgAtK(distinct, relevant, 5),
            RetrievalMetrics.NdcgAtK(withDups, relevant, 5), 12);
        Assert.Equal(
            RetrievalMetrics.NdcgAtK(distinct, graded, 5),
            RetrievalMetrics.NdcgAtK(withDups, graded, 5), 12);
        Assert.Equal(
            RetrievalMetrics.ReciprocalRankAtK(distinct, relevant, 5),
            RetrievalMetrics.ReciprocalRankAtK(withDups, relevant, 5), 12);
        Assert.Equal(
            RetrievalMetrics.AveragePrecisionAtK(distinct, relevant, 5),
            RetrievalMetrics.AveragePrecisionAtK(withDups, relevant, 5), 12);
    }

    [Fact]
    public void GradedNdcg_RejectsInfiniteGains()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => RetrievalMetrics.NdcgAtK(Retrieved, new Dictionary<string, double> { ["a"] = double.PositiveInfinity }, 3));
    }
}