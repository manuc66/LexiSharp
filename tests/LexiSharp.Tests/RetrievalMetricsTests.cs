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
    public void Metrics_ValidateArguments()
    {
        Assert.Throws<ArgumentNullException>(() => RetrievalMetrics.PrecisionAtK(null!, Relevant, 3));
        Assert.Throws<ArgumentNullException>(() => RetrievalMetrics.RecallAtK(Retrieved, null!, 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => RetrievalMetrics.F1AtK(Retrieved, Relevant, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => RetrievalMetrics.NdcgAtK(Retrieved, Relevant, -1));
    }
}