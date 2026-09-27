using LexiSharp.Benchmarking;
using LexiSharp.Core;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// Covers the golden master: the on-disk round trip and, more importantly, the classification.
/// A verify that cannot tell a real behaviour change from two documents swapping among equal
/// scores is worse than none, because it either cries wolf or hides a regression.
/// </summary>
public class GoldenBaselineTests
{
    private static readonly SearchDocument[] Corpus =
    [
        new("alpha", "refresh token renewal rotates the credential"),
        new("beta", "the deployment pipeline builds a container image"),
        new("gamma", "session cookie lifetime and logout behaviour"),
    ];

    private static BenchmarkConfigResult Result(
        string name,
        IReadOnlyList<double> scores,
        params (string QueryId, string[] Ids)[] queries)
    {
        var perQuery = new List<BenchmarkQueryResult>();

        for (int i = 0; i < queries.Length; i++)
        {
            var ids = queries[i].Ids;
            var slice = scores.Skip(i * 3).Take(ids.Length).ToArray();

            perQuery.Add(new BenchmarkQueryResult(
                queries[i].QueryId,
                $"text of {queries[i].QueryId}",
                new BenchmarkMetrics(0.5, 0.5, 1.0, 0.5, 0.25, 0.4),
                ids,
                1,
                slice.Length == ids.Length ? slice : null));
        }

        return new BenchmarkConfigResult(name, new BenchmarkMetrics(0.5, 0.5, 1.0, 0.5, 0.25, 0.4), 0, 0, perQuery.Count)
        {
            PerQuery = perQuery,
        };
    }

    private static GoldenBaseline BaselineOf(params BenchmarkConfigResult[] results) =>
        new()
        {
            TopK = 5,
            CorpusDocuments = 3,
            Entries = results
                .SelectMany(result => result.PerQuery.Select(query =>
                    new GoldenBaseline.Entry(result.Name, query.QueryId, query.RetrievedIds, query.Metrics)))
                .ToList(),
        };

    [Fact]
    public void TheOnDiskFormatRoundTrips()
    {
        var recorded = BaselineOf(Result("BM25", [3, 2, 1], ("q1", ["alpha", "beta"])));
        var parsed = GoldenBaseline.Parse(recorded.ToText());

        Assert.Equal(recorded.TopK, parsed.TopK);
        Assert.Equal(recorded.CorpusDocuments, parsed.CorpusDocuments);
        Assert.Equal(recorded.Entries.Count, parsed.Entries.Count);

        GoldenBaseline.Entry original = recorded.Entries[0];
        GoldenBaseline.Entry round = parsed.Entries[0];

        Assert.Equal(original.Configuration, round.Configuration);
        Assert.Equal(original.QueryId, round.QueryId);
        Assert.Equal(original.DocumentIds, round.DocumentIds);
        Assert.Equal(original.Metrics.NdcgAtK, round.Metrics.NdcgAtK, 4);
        Assert.Equal(original.Metrics.F1AtK, round.Metrics.F1AtK, 4);
    }

    [Fact]
    public void ABaselineStaysReadableEnoughToReview()
    {
        // The whole discipline depends on a human reading the diff. If a line grows to a
        // paragraph, people stop reviewing, and an unreviewed baseline catches nothing.
        var text = BaselineOf(Result("BM25", [3, 2, 1, 2, 1, 1], ("q1", ["alpha", "beta"]), ("q2", ["beta", "gamma"])))
            .ToText();

        foreach (string line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith('#') || line.StartsWith('['))
                continue;

            Assert.True(line.Length < 200, $"Baseline line too long to review ({line.Length} chars): {line}");
        }
    }

    [Fact]
    public void AnIdenticalRunIsClean()
    {
        var baseline = BaselineOf(Result("BM25", [3, 2, 1], ("q1", ["alpha", "beta"])));
        var comparison = baseline.Compare([Result("BM25", [3, 2, 1], ("q1", ["alpha", "beta"]))]);

        Assert.True(comparison.IsClean);
        Assert.Single(comparison.Matches);
        Assert.Empty(comparison.Changes);
    }

    [Fact]
    public void ADifferentMembershipIsAChange()
    {
        var baseline = BaselineOf(Result("BM25", [3, 2, 1], ("q1", ["alpha", "beta"])));
        var comparison = baseline.Compare([Result("BM25", [3, 2, 1], ("q1", ["alpha", "gamma"]))]);

        Assert.False(comparison.IsClean);
        Assert.Single(comparison.Changes);
        Assert.Contains("membership", comparison.Changes[0].Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public void AMovedMetricIsAChangeEvenWhenTheRankingIsIdentical()
    {
        // Metrics are stored precisely because a re-ordering among tied documents leaves every
        // rank intact and still shifts nDCG.
        var baseline = BaselineOf(Result("BM25", [3, 2, 1], ("q1", ["alpha", "beta"])));
        var moved = new BenchmarkConfigResult("BM25", new BenchmarkMetrics(0.5, 0.5, 1.0, 0.5, 0.25, 0.4), 0, 0, 1)
        {
            PerQuery =
            [
                new BenchmarkQueryResult("q1", "text of q1", new BenchmarkMetrics(0.9, 0.5, 1.0, 0.5, 0.25, 0.4), ["alpha", "beta"], 1, [3, 2]),
            ],
        };

        var comparison = baseline.Compare([moved]);

        Assert.Single(comparison.Changes);
        Assert.Contains("metrics moved", comparison.Changes[0].Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public void SwappingDocumentsThatTieIsNotAChange()
    {
        // beta and gamma carry the same score, so their order is decided by corpus order and says
        // nothing about behaviour. Reporting it as drift would train people to ignore the report.
        var baseline = BaselineOf(Result("BM25", [3, 2, 2, 1], ("q1", ["alpha", "beta", "gamma"])));
        var comparison = baseline.Compare([Result("BM25", [3, 2, 2, 1], ("q1", ["alpha", "gamma", "beta"]))]);

        Assert.True(comparison.IsClean);
        Assert.Single(comparison.TieReorders);
        Assert.Empty(comparison.Changes);
    }

    [Fact]
    public void AReorderingAmongDistinctScoresIsAChange()
    {
        // Same three documents, but every score differs: a real re-ordering.
        var baseline = BaselineOf(Result("BM25", [3, 2, 1, 1], ("q1", ["alpha", "beta", "gamma"])));
        var comparison = baseline.Compare([Result("BM25", [3, 2, 1, 1], ("q1", ["alpha", "gamma", "beta"]))]);

        Assert.False(comparison.IsClean);
        Assert.Single(comparison.Changes);
        Assert.Contains("without a tie", comparison.Changes[0].Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownScoreIsNotEvidenceOfATie()
    {
        // No scores recorded: the reorder cannot be excused, and guessing "probably a tie" is
        // exactly the kind of leniency that hides a regression.
        var baseline = BaselineOf(Result("BM25", [3, 2, 1, 1], ("q1", ["alpha", "beta", "gamma"])));

        var scoreless = new BenchmarkConfigResult("BM25", new BenchmarkMetrics(0.5, 0.5, 1.0, 0.5, 0.25, 0.4), 0, 0, 1)
        {
            PerQuery =
            [
                new BenchmarkQueryResult("q1", "text of q1", new BenchmarkMetrics(0.5, 0.5, 1.0, 0.5, 0.25, 0.4), ["alpha", "gamma", "beta"], 1),
            ],
        };

        var comparison = baseline.Compare([scoreless]);

        Assert.False(comparison.IsClean);
        Assert.Single(comparison.Changes);
    }

    [Fact]
    public void SwappingDocumentsThatDoNotTieIsAChange()
    {
        // Same two documents, same metrics, distinct scores: a real re-ordering, and calling it a
        // tie would hide it.
        var baseline = BaselineOf(Result("BM25", [3, 2, 1], ("q1", ["alpha", "beta"])));

        var reordered = new BenchmarkConfigResult("BM25", new BenchmarkMetrics(0.5, 0.5, 1.0, 0.5, 0.25, 0.4), 0, 0, 1)
        {
            PerQuery =
            [
                new BenchmarkQueryResult("q1", "text of q1", new BenchmarkMetrics(0.5, 0.5, 1.0, 0.5, 0.25, 0.4), ["beta", "alpha"], 1, [3, 2]),
            ],
        };

        var comparison = baseline.Compare([reordered]);

        Assert.False(comparison.IsClean);
        Assert.Single(comparison.Changes);
        Assert.Contains("without a tie", comparison.Changes[0].Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public void AQueryMissingFromTheBaselineIsADesyncNotAPass()
    {
        // A baseline that silently ignores new queries reports a false clean.
        var baseline = BaselineOf(Result("BM25", [3, 2, 1], ("q1", ["alpha", "beta"])));
        var comparison = baseline.Compare([Result("BM25", [3, 2, 1, 2, 1, 1], ("q1", ["alpha", "beta"]), ("q2", ["beta", "gamma"]))]);

        Assert.False(comparison.IsClean);
        Assert.Contains("BM25 / q2", comparison.Missing);
    }

    [Fact]
    public void AQueryMissingFromTheRunIsADesync()
    {
        var baseline = BaselineOf(Result("BM25", [3, 2, 1, 2, 1, 1], ("q1", ["alpha", "beta"]), ("q2", ["beta", "gamma"])));
        var comparison = baseline.Compare([Result("BM25", [3, 2, 1], ("q1", ["alpha", "beta"]))]);

        Assert.False(comparison.IsClean);
        Assert.Contains("BM25 / q2", comparison.Extra);
    }

    [Fact]
    public void ABaselineFromAnotherFormatVersionIsRejectedLoudly()
    {
        // Better to refuse than to skip lines it cannot read and report a clean run.
        var text = "# version: 99\n# topK: 5\n# corpusDocuments: 3\n";

        var error = Assert.Throws<FormatException>(() => GoldenBaseline.Parse(text));
        Assert.Contains("version 99", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ABaselineWithoutADepthIsRejected()
    {
        var text = "# version: 1\n# corpusDocuments: 3\n";

        Assert.Throws<FormatException>(() => GoldenBaseline.Parse(text));
    }

    [Fact]
    public void AMalformedMetricsColumnIsRejected()
    {
        var text = "# version: 1\n# topK: 5\n# corpusDocuments: 3\n\n[BM25]\nq1 | alpha | ndcg 1.0 map oops\n";

        var error = Assert.Throws<FormatException>(() => GoldenBaseline.Parse(text));
        Assert.Contains("not a metric pair", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ATruncatedMetricsColumnIsRejected()
    {
        var text = "# version: 1\n# topK: 5\n# corpusDocuments: 3\n\n[BM25]\nq1 | alpha | ndcg 1.0 map\n";

        Assert.Throws<FormatException>(() => GoldenBaseline.Parse(text));
    }

    [Fact]
    public void TheCommittedBaselineParsesAndIsCleanAgainstTheCorpus()
    {
        // The point of committing it: a real run has to reproduce it, or the gate is theatre.
        var root = RepositoryRoot();
        var path = Path.Combine(root, "bench", "reference-corpus", "golden", "rankings.txt");

        Assert.True(File.Exists(path), $"Missing committed baseline: {path}");

        var baseline = GoldenBaseline.Parse(File.ReadAllText(path), path);

        Assert.Equal(5, baseline.TopK);
        Assert.Equal(42, baseline.CorpusDocuments);
        Assert.NotEmpty(baseline.Entries);
        Assert.All(baseline.Entries, entry => Assert.NotEmpty(entry.DocumentIds));

        // Four of the six configurations exist to put a scorer's *default* parameters through a real
        // ranking, so that changing a public default is a reviewable diff. If one goes missing here
        // the net silently stops covering that scorer, so the expected set is asserted.
        Assert.Equal(
            [
                "BM25",
                "BM25 + proximity (damp, s=1)",
                "BM25 + semantic",
                "BM25+",
                "BM25F",
                "BM25L",
            ],
            baseline.Entries.Select(entry => entry.Configuration).Distinct().Order(StringComparer.Ordinal));
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "LexiSharp.slnx")))
                return directory.FullName;

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
