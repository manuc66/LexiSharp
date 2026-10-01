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

    /// <summary>The next representable double above <paramref name="value"/>, one bit away.</summary>
    private static double NextAfter(double value) =>
        BitConverter.UInt64BitsToDouble(BitConverter.DoubleToUInt64Bits(value) + 1);

    private static GoldenBaseline BaselineOf(params BenchmarkConfigResult[] results) =>
        new()
        {
            TopK = 5,
            CorpusDocuments = 3,
            Entries = results
                .SelectMany(result => result.PerQuery.Select(query =>
                    new GoldenBaseline.Entry(
                        result.Name, query.QueryId, query.RetrievedIds, query.Metrics,
                        GoldenBaseline.FoldScores(query.RetrievedIds, query.RetrievedScores))))
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
    public void AScoreThatMovedInItsLastBitsIsAChangeEvenWhenNothingElseDid()
    {
        // The gap this column exists to close. The ranking is identical, the membership is
        // identical, and every metric is rounded to four decimals and compared to 5e-5 — so a
        // reassociated sum or a fused multiply-add, one unit in the last place wide, passes every
        // other check in the repository. The pinned nDCG figures would not see it either: their
        // tolerance is +/-0.002, about a hundred million times the width of the effect.
        var baseline = BaselineOf(Result("BM25", [3, 2, 1], ("q1", ["alpha", "beta"])));

        // Same ranking, same metrics, one bit different in one score. The nudge is done on the bit
        // pattern rather than written as 3 + 2^-52, which is not representable and would silently
        // fold back to 3 — a test that asserted the opposite of what it means.
        var nudged = new BenchmarkConfigResult("BM25", new BenchmarkMetrics(0.5, 0.5, 1.0, 0.5, 0.25, 0.4), 0, 0, 1)
        {
            PerQuery =
            [
                new BenchmarkQueryResult("q1", "text of q1", new BenchmarkMetrics(0.5, 0.5, 1.0, 0.5, 0.25, 0.4), ["alpha", "beta"], 1, [NextAfter(3.0), 2]),
            ],
        };

        var comparison = baseline.Compare([nudged]);

        Assert.False(comparison.IsClean);
        Assert.Single(comparison.Changes);
        Assert.Contains("scores moved", comparison.Changes[0].Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public void ATieSwappedOrderIsStillATieAndNotAScoreChange()
    {
        // The trap in folding the scores: two documents with equal scores that exchange places must
        // not read as a score change, or every legitimate tie in the corpus becomes a red build and
        // the report stops being read. Folding the (id, bits) pairs sorted by id is what makes the
        // fingerprint insensitive to the permutation while still sensitive to a score that moved.
        var baseline = BaselineOf(Result("BM25", [2, 2, 1], ("q1", ["alpha", "beta"])));

        var swapped = new BenchmarkConfigResult("BM25", new BenchmarkMetrics(0.5, 0.5, 1.0, 0.5, 0.25, 0.4), 0, 0, 1)
        {
            PerQuery =
            [
                new BenchmarkQueryResult("q1", "text of q1", new BenchmarkMetrics(0.5, 0.5, 1.0, 0.5, 0.25, 0.4), ["beta", "alpha"], 1, [2, 2]),
            ],
        };

        var comparison = baseline.Compare([swapped]);

        Assert.Single(comparison.TieReorders);
        Assert.Empty(comparison.Changes);
    }

    [Fact]
    public void ARealReorderingIsReportedAsAReorderingAndNotAsAScoreChange()
    {
        // The fingerprint is computed first but reported last, precisely so it does not replace a
        // more specific diagnosis. Here the scores really did change with the documents, and
        // "re-ordered X and Y without a tie" is the fact a reader needs.
        var baseline = BaselineOf(Result("BM25", [3, 2, 1], ("q1", ["alpha", "beta"])));

        var reordered = new BenchmarkConfigResult("BM25", new BenchmarkMetrics(0.5, 0.5, 1.0, 0.5, 0.25, 0.4), 0, 0, 1)
        {
            PerQuery =
            [
                new BenchmarkQueryResult("q1", "text of q1", new BenchmarkMetrics(0.5, 0.5, 1.0, 0.5, 0.25, 0.4), ["beta", "alpha"], 1, [3, 2]),
            ],
        };

        var comparison = baseline.Compare([reordered]);

        Assert.Single(comparison.Changes);
        Assert.Contains("without a tie", comparison.Changes[0].Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSameCorpusGivesTheSameFingerprintOnEveryRun()
    {
        // What makes the column a cross-machine check rather than a source of flaky builds: two
        // independent runs over the same documents and the same scores must fold to the same token.
        string first = GoldenBaseline.FoldScores(["alpha", "beta"], [3, 2]);
        string second = GoldenBaseline.FoldScores(["alpha", "beta"], [3, 2]);

        Assert.Equal(first, second);
        Assert.Equal(16, first.Length);
    }

    [Fact]
    public void TheFingerprintDependsOnTheBitsAndNotOnTheirOrder()
    {
        // Sorted by id, so a permutation of equal scores folds to the same token, and a score that
        // moved to a different document does not.
        Assert.Equal(
            GoldenBaseline.FoldScores(["alpha", "beta"], [2, 2]),
            GoldenBaseline.FoldScores(["beta", "alpha"], [2, 2]));

        Assert.NotEqual(
            GoldenBaseline.FoldScores(["alpha", "beta"], [3, 2]),
            GoldenBaseline.FoldScores(["alpha", "beta"], [2, 3]));
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
    public void TheReportListsChangesDesyncAndTies()
    {
        // The read order is changes first, then desync in both directions, then ties. The desync
        // entries carry synthetic "Changed" verdicts with a human explanation, so a reader scanning
        // the report sees one block of problems rather than three kinds of event to interpret. Each
        // verdict's fields are read here too, which is the surface the CLI prints from.
        var baseline = BaselineOf(
            Result("BM25", [3, 2, 1, 2, 1], ("q1", ["alpha", "beta"]), ("q2", ["beta", "gamma"])));
        var run = new BenchmarkConfigResult("BM25", new BenchmarkMetrics(0.5, 0.5, 1.0, 0.5, 0.25, 0.4), 0, 0, 2)
        {
            PerQuery =
            [
                // Same membership, distinct scores, reversed: a real re-ordering.
                new BenchmarkQueryResult("q1", "text of q1", new BenchmarkMetrics(0.5, 0.5, 1.0, 0.5, 0.25, 0.4), ["beta", "alpha"], 1, [2, 3]),
                // Present in the run, absent from the baseline.
                new BenchmarkQueryResult("q3", "text of q3", new BenchmarkMetrics(0.5, 0.5, 1.0, 0.5, 0.25, 0.4), ["alpha"], 1, [3]),
            ],
        };

        var comparison = baseline.Compare([run]);
        var report = comparison.Report.ToArray();

        Assert.Equal(3, report.Length);
        Assert.All(report, verdict => Assert.Equal(GoldenVerdict.Changed, verdict.Verdict));

        // The re-ordering belongs to a known configuration and keeps its real query id; the two
        // desyncs cannot know which configuration the orphan query belonged to, so they carry "?"
        // as the configuration and the combined key as the id.
        Assert.Equal("BM25", Assert.Single(report, verdict => verdict.QueryId == "q1").Configuration);
        Assert.Equal(2, Assert.Single(report, verdict => verdict.QueryId == "q1").Expected.Count);
        Assert.Equal(2, Assert.Single(report, verdict => verdict.QueryId == "q1").Actual.Count);

        Assert.Equal("?", Assert.Single(report, verdict => verdict.QueryId == "BM25 / q2").Configuration);
        Assert.Contains("absent from the run", Assert.Single(report, verdict => verdict.QueryId == "BM25 / q2").Detail!, StringComparison.Ordinal);
        Assert.Equal("?", Assert.Single(report, verdict => verdict.QueryId == "BM25 / q3").Configuration);
        Assert.Contains("absent from the baseline", Assert.Single(report, verdict => verdict.QueryId == "BM25 / q3").Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public void FoldScoresReturnsADashWhenThereIsNothingToFold()
    {
        // A no-score channel records "-" instead of a token: the fingerprint column is optional and
        // a baseline that predates it must parse.
        Assert.Equal("-", GoldenBaseline.FoldScores(["alpha", "beta"], null));
        Assert.Equal("-", GoldenBaseline.FoldScores(["alpha", "beta"], [3]));
    }

    [Fact]
    public void ADifferentPageSizeIsAChange()
    {
        var baseline = BaselineOf(Result("BM25", [3, 2, 1], ("q1", ["alpha", "beta"])));
        var shorter = new BenchmarkConfigResult("BM25", new BenchmarkMetrics(0.5, 0.5, 1.0, 0.5, 0.25, 0.4), 0, 0, 1)
        {
            PerQuery =
            [
                new BenchmarkQueryResult("q1", "text of q1", new BenchmarkMetrics(0.5, 0.5, 1.0, 0.5, 0.25, 0.4), ["alpha"], 1, [3]),
            ],
        };

        var comparison = baseline.Compare([shorter]);

        Assert.Single(comparison.Changes);
        Assert.Contains("page size", comparison.Changes[0].Detail!, StringComparison.Ordinal);
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
    public void AQueryLineBeforeAnyConfigurationHeaderIsRejected()
    {
        var text = "# version: 2\n# topK: 5\n# corpusDocuments: 3\nq1 | alpha | ndcg 1.0 map 1.0 mrr 1.0 r 1.0 p 1.0 f1 1.0\n";

        var error = Assert.Throws<FormatException>(() => GoldenBaseline.Parse(text));
        Assert.Contains("before any [configuration]", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AWrongNumberOfColumnsIsRejected()
    {
        var text = "# version: 2\n# topK: 5\n# corpusDocuments: 3\n\n[BM25]\nq1 | alpha\n";

        var error = Assert.Throws<FormatException>(() => GoldenBaseline.Parse(text));
        Assert.Contains("expected 'queryId | docIds | metrics", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnInvalidFingerprintIsRejected()
    {
        var text = "# version: 2\n# topK: 5\n# corpusDocuments: 3\n\n[BM25]\nq1 | alpha | ndcg 1.0 map 1.0 mrr 1.0 r 1.0 p 1.0 f1 1.0 | scores 12AB\n";

        var error = Assert.Throws<FormatException>(() => GoldenBaseline.Parse(text));
        Assert.Contains("16 hexadecimal digits", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ARecordedDashFingerprintRoundTripsAsADash()
    {
        // A version-2 line with "-" in the scores column — a run that recorded no scores — parses
        // to a "-" entry and survives a round trip. Null is reserved for baselines written before
        // version 2, which never carried the column at all; the dash is the column's own way of
        // saying there is nothing to fold.
        var text = "# version: 2\n# topK: 5\n# corpusDocuments: 3\n\n[BM25]\nq1 | alpha | ndcg 1.0 map 1.0 mrr 1.0 r 1.0 p 1.0 f1 1.0 | scores -\n";

        var baseline = GoldenBaseline.Parse(text);

        Assert.Equal("-", Assert.Single(baseline.Entries).ScoreFingerprint);
        Assert.Equal("-", Assert.Single(GoldenBaseline.Parse(baseline.ToText()).Entries).ScoreFingerprint);
    }

    [Fact]
    public void AMissingMetricIsRejected()
    {
        var text = "# version: 2\n# topK: 5\n# corpusDocuments: 3\n\n[BM25]\nq1 | alpha | ndcg 1.0 map 1.0 mrr 1.0 r 1.0 p 1.0\n";

        var error = Assert.Throws<FormatException>(() => GoldenBaseline.Parse(text));
        Assert.Contains("missing metric 'f1'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUpToDateFormatWithoutADepthHeaderIsRejected()
    {
        // A depth of zero is unusable whether it was written as "0" or never written at all, and a
        // format that predates the header is refused the same way.
        var text = "# version: 2\n# corpusDocuments: 3\n\n[BM25]\nq1 | alpha | ndcg 1.0 map 1.0 mrr 1.0 r 1.0 p 1.0 f1 1.0\n";

        var error = Assert.Throws<FormatException>(() => GoldenBaseline.Parse(text));
        Assert.Contains("topK", error.Message, StringComparison.Ordinal);
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
