using LexiSharp.Benchmarking;
using LexiSharp.Core;
using LexiSharp.Indexing;
using LexiSharp.Ranking;
using Xunit;

// CA1861 ("prefer a static readonly field over a constant array argument") is suppressed on
// the lines below. Its premise is a call repeated with the same literal, allocating each
// time. These are one-shot fixtures, and the literal belongs beside the assertion that reads
// it -- hoisting it into a field saves nothing that is measured, and moves the data away
// from the test that fails on it.

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
        var query = new BenchmarkQuery("q1", "refresh token", new[] { "full", "partial" }); // NOSONAR:CA1861

        Assert.Equal(new[] { "full", "partial" }, query.RelevantDocumentIds); // NOSONAR:CA1861
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

        var binary = RetrievalMetrics.NdcgAtK(retrieved, new[] { "full", "partial", "mention" }, 10); // NOSONAR:CA1861
        var viaUnitGains = RetrievalMetrics.NdcgAtK(
            retrieved,
            new Dictionary<string, double> { ["full"] = 1, ["partial"] = 1, ["mention"] = 1 },
            10);

        Assert.Equal(binary, viaUnitGains, 12);
    }

    [Fact]
    public void AQueryBuiltFromIdsIsNotReportedAsGraded()
    {
        var query = new BenchmarkQuery("q1", "refresh token", new[] { "full", "partial" }); // NOSONAR:CA1861

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
            new BenchmarkQuery("q1", "refresh token", new[] { "full", "full" })); // NOSONAR:CA1861
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

        var binary = new BenchmarkQuery("q1", "refresh token", new[] { "full", "partial", "mention" }); // NOSONAR:CA1861

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
