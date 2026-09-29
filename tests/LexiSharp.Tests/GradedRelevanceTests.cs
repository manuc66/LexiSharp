using LexiSharp.Benchmarking;
using LexiSharp.Core;
using LexiSharp.Indexing;
using LexiSharp.Ranking;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// Covers graded relevance in <see cref="BenchmarkQuery"/> and its effect on
/// <see cref="CorpusBenchmark"/>. The two harnesses disagreed: the BEIR one read qrel grades and
/// the CLI read-then-discarded them, so the same qrels file meant two different things depending
/// on which tool loaded it.
/// </summary>
public class GradedRelevanceTests
{
    private static readonly SearchDocument[] Corpus =
    [
        new("full", "session renewal rotates the refresh token before it expires"),
        new("partial", "the refresh token has a finite lifetime and can expire"),
        new("mention", "tokens are issued by the authorization server during a session"),
        new("offtopic", "the deployment pipeline builds a container image for each commit"),
    ];

    [Fact]
    public void GradedRelevanceProjectsToABinaryViewOfEveryJudgedDocument()
    {
        var query = new BenchmarkQuery("q1", "refresh token", new Dictionary<string, double>
        {
            ["full"] = 3,
            ["partial"] = 2,
            ["mention"] = 1,
        });

        Assert.Equal(3, query.RelevantDocumentIds.Count);
        Assert.Contains("full", query.RelevantDocumentIds);
        Assert.Contains("partial", query.RelevantDocumentIds);
        Assert.Contains("mention", query.RelevantDocumentIds);
        Assert.DoesNotContain("offtopic", query.RelevantDocumentIds);
    }

    [Fact]
    public void ABinaryQueryJudgesEveryDocumentAsRelevant()
    {
        var query = new BenchmarkQuery("q1", "refresh token", new[] { "full", "partial" });

        Assert.Equal(new[] { "full", "partial" }, query.RelevantDocumentIds);
    }

    [Theory]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void GradedJudgmentsRejectImpossibleGains(double gain)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BenchmarkQuery(
            "q1",
            "refresh token",
            new Dictionary<string, double> { ["full"] = gain }));
    }

    [Fact]
    public void NdcgIsUnchangedByThisRepresentation()
    {
        // A binary query is now a graded one with unit gains, and nDCG goes through the graded
        // overload for both. 2^1 - 1 == 1, so the graded formula must degenerate to the binary one
        // exactly - this is what keeps every previously reported binary nDCG valid.
        var retrieved = new[] { "partial", "offtopic", "full", "mention" };

        var binary = RetrievalMetrics.NdcgAtK(retrieved, new[] { "full", "partial", "mention" }, 10);
        var viaUnitGains = RetrievalMetrics.NdcgAtK(
            retrieved,
            new Dictionary<string, double> { ["full"] = 1, ["partial"] = 1, ["mention"] = 1 },
            10);

        Assert.Equal(binary, viaUnitGains, 12);
    }

    [Fact]
    public void LinearGainIsTheTreCevalConvention()
    {
        // trec_eval's m_ndcg.c: "Gain values are set to the appropriate relevance level by
        // default", i.e. gain = rel, which is what pytrec_eval's ndcg_cut — and therefore the BEIR
        // paper's Table 2 — computes. One relevant document of level 2 at rank 1, one of level 1
        // at rank 2, k = 2.
        //
        //   linear      DCG = 2/log2(2) + 1/log2(3) = 2 + 0.63093 = 2.63093
        //               IDCG = 2/log2(2) + 1/log2(3) = 2.63093   -> 1.0
        //   exponential DCG = 3/log2(2) + 1/log2(3) = 3 + 0.63093 = 3.63093
        //               IDCG = 3/log2(2) + 1/log2(3) = 3.63093   -> 1.0
        //
        // Both are 1 here, so the conventions need a case where they actually differ: the level-2
        // document retrieved *second*.
        var graded = new Dictionary<string, double> { ["strong"] = 2, ["weak"] = 1 };
        var strongLast = new[] { "weak", "strong" };

        var linear = RetrievalMetrics.NdcgAtK(strongLast, graded, 2, NdcgGain.Linear);
        var exponential = RetrievalMetrics.NdcgAtK(strongLast, graded, 2, NdcgGain.Exponential);

        // linear      DCG = 1/log2(2) + 2/log2(3) = 1 + 1.26186 = 2.26186, over IDCG 2.63093
        Assert.Equal(2.26186 / 2.63093, linear, 6);

        // exponential DCG = 1/log2(2) + 3/log2(3) = 1 + 1.89279 = 2.89279, over IDCG 3.63093
        Assert.Equal(2.89279 / 3.63093, exponential, 6);

        // The exponential convention pays the stronger document more, so ranking it later costs
        // more under it. That is the whole of the difference between the two numbers.
        Assert.True(exponential < linear, $"{exponential} should be below {linear}");
    }

    [Fact]
    public void TheGainConventionOnlyMattersAboveLevelOne()
    {
        // 2^1 - 1 == 1, so on binary relevance the two conventions are the same number. This is why
        // the published SciFact and ArguAna figures are convention-independent and the NFCorpus one
        // is not.
        var binary = new[] { "a", "b", "c" };
        var grades = new Dictionary<string, double> { ["a"] = 1, ["c"] = 1 };

        Assert.Equal(
            RetrievalMetrics.NdcgAtK(binary, grades, 3, NdcgGain.Linear),
            RetrievalMetrics.NdcgAtK(binary, grades, 3, NdcgGain.Exponential),
            12);

        // The no-gain overload is the exponential one, and stays that way: a recorded baseline
        // must not change meaning because a second convention was added next to it.
        Assert.Equal(
            RetrievalMetrics.NdcgAtK(binary, new Dictionary<string, double> { ["a"] = 2 }, 3),
            RetrievalMetrics.NdcgAtK(binary, new Dictionary<string, double> { ["a"] = 2 }, 3, NdcgGain.Exponential),
            12);
    }

    [Fact]
    public void AQueryBuiltFromIdsIsNotReportedAsGraded()
    {
        var query = new BenchmarkQuery("q1", "refresh token", new[] { "full", "partial" });

        Assert.False(query.IsGraded);
        Assert.Equal(2, query.GradedRelevance.Count);
        Assert.All(query.GradedRelevance.Values, gain => Assert.Equal(1.0, gain));
    }

    [Fact]
    public void AQueryWithLevelsIsReportedAsGraded()
    {
        var query = new BenchmarkQuery("q1", "refresh token", new Dictionary<string, double>
        {
            ["full"] = 3,
            ["partial"] = 2,
        });

        Assert.True(query.IsGraded);
    }

    [Fact]
    public void ADuplicatedJudgmentIsRejected()
    {
        // Silently collapsing it would change the judged count and every average derived from it.
        Assert.Throws<ArgumentException>(() =>
            new BenchmarkQuery("q1", "refresh token", new[] { "full", "full" }));
    }

    [Fact]
    public void GradedAndBinaryQueriesOfTheSameJudgmentsShareEveryOtherMetric()
    {
        // Only nDCG may differ. If recall, MAP or MRR move, the derived binary view is wrong.
        var configs = new[] { BenchmarkConfig.Bm25() };
        var options = new BenchmarkOptions { TopK = 4 };

        var graded = new BenchmarkQuery("q1", "refresh token", new Dictionary<string, double>
        {
            ["full"] = 3,
            ["partial"] = 2,
            ["mention"] = 1,
        });

        var binary = new BenchmarkQuery("q1", "refresh token", new[] { "full", "partial", "mention" });

        var withGrades = CorpusBenchmark.Run(Corpus, [graded], configs, options)[0];
        var withoutGrades = CorpusBenchmark.Run(Corpus, [binary], configs, options)[0];

        Assert.Equal(withoutGrades.Metrics.RecallAtK, withGrades.Metrics.RecallAtK, 10);
        Assert.Equal(withoutGrades.Metrics.MapAtK, withGrades.Metrics.MapAtK, 10);
        Assert.Equal(withoutGrades.Metrics.MrrAtK, withGrades.Metrics.MrrAtK, 10);
        Assert.Equal(withoutGrades.Metrics.PrecisionAtK, withGrades.Metrics.PrecisionAtK, 10);
        Assert.Equal(withoutGrades.Metrics.F1AtK, withGrades.Metrics.F1AtK, 10);

        // ...and nDCG is where the levels actually pay off.
        Assert.NotEqual(withoutGrades.Metrics.NdcgAtK, withGrades.Metrics.NdcgAtK);
    }

    [Fact]
    public void GradedRelevanceRewardsPuttingTheStronglyRelevantDocumentFirst()
    {
        // The whole point of grades: the two runs judge the same documents, but one puts the
        // fully-answering document first and the other puts it last. Binary nDCG cannot tell them
        // apart (both are perfect recall); a graded nDCG can.
        var documents = new[]
        {
            new SearchDocument("strong", "refresh token renewal rotates the credential before expiry"),
            new SearchDocument("weak", "the session cookie is cleared when the browser closes"),
        };

        var engine = new RankedTextSearchEngine(new InMemoryTextIndex(), new Bm25Scorer());
        engine.Index(documents);

        var query = new BenchmarkQuery("q1", "refresh token", new Dictionary<string, double>
        {
            ["strong"] = 3,
            ["weak"] = 1,
        });

        var strongFirst = new[] { "strong", "weak" };
        var strongLast = new[] { "weak", "strong" };

        var gradedGood = RetrievalMetrics.NdcgAtK(strongFirst, query.GradedRelevance!, 10);
        var gradedBad = RetrievalMetrics.NdcgAtK(strongLast, query.GradedRelevance!, 10);

        var binaryGood = RetrievalMetrics.NdcgAtK(strongFirst, query.RelevantDocumentIds, 10);
        var binaryBad = RetrievalMetrics.NdcgAtK(strongLast, query.RelevantDocumentIds, 10);

        Assert.True(gradedGood > gradedBad, $"graded {gradedGood} should beat {gradedBad}");
        Assert.Equal(binaryGood, binaryBad, 10);
    }
}
