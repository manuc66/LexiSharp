using System;
using System.Collections.Generic;
using System.Linq;
using LexiSharp.Eval;
using LexiSharp.Indexing;
using LexiSharp.Linguistics;
using LexiSharp.Ranking;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// The BEIR harness's own switches: what a run adds, and what it deliberately leaves out. These are
/// the rows a reader compares against, so a flag that silently does nothing is worse than a missing
/// row — the table would look complete.
/// </summary>
public class EvaluationHarnessTests
{
    [Fact]
    public void TheBm25FTunedRowIsOptInAndSaysWhetherWeightingHelped()
    {
        BeirCorpus corpus = Corpus();
        var index = new InMemoryTextIndex();
        index.Index(Evaluation.BuildDocuments(corpus));

        var (results, _, notes, _) = Evaluation.Run(
            corpus, index, topK: 10, limit: null, Tokenizer.Default,
            tuned: false, tuneBm25F: true);

        Assert.Contains(results, result => result.Name.StartsWith("BM25F tuned", StringComparison.Ordinal));

        // WeightingHelped is the answer to "does the weight help on this corpus", and it is the whole
        // reason to run 29 evaluations instead of the three fixed weights.
        Assert.Contains(notes, note => note.Contains("WeightingHelped=", StringComparison.Ordinal));
    }

    [Fact]
    public void ADefaultRunPaysNothingForIt()
    {
        // The pinned reference replays the default table, so a row nobody asked for is time every
        // gate pays for. Adding it needs the flag.
        BeirCorpus corpus = Corpus();
        var index = new InMemoryTextIndex();
        index.Index(Evaluation.BuildDocuments(corpus));

        var (results, _, notes, _) = Evaluation.Run(
            corpus, index, topK: 10, limit: null, Tokenizer.Default, tuned: false);

        Assert.DoesNotContain(results, result => result.Name.StartsWith("BM25F tuned", StringComparison.Ordinal));
        Assert.DoesNotContain(notes, note => note.Contains("WeightingHelped=", StringComparison.Ordinal));
    }

    [Fact]
    public void TheHeldOutSplitIsStableDisjointAndCoversEveryQuery()
    {
        string[] ids = [.. Enumerable.Range(0, 500).Select(index => $"q{index}")];

        (List<string> fit, List<string> score) = HeldOutSplit.Partition(ids, id => id, 0.4);

        // Reversing the input is the file-system/order-independence check: the partition is a
        // property of each id's hash, not of where the query sat in the corpus.
        (List<string> reversedFit, List<string> reversedScore) =
            HeldOutSplit.Partition(ids.Reverse(), id => id, 0.4);

        Assert.Equal(fit.OrderBy(id => id, StringComparer.Ordinal), reversedFit.OrderBy(id => id, StringComparer.Ordinal));
        Assert.Equal(score.OrderBy(id => id, StringComparer.Ordinal), reversedScore.OrderBy(id => id, StringComparer.Ordinal));

        Assert.Empty(fit.Intersect(score, StringComparer.Ordinal));
        Assert.Equal(
            ids.OrderBy(id => id, StringComparer.Ordinal),
            fit.Concat(score).OrderBy(id => id, StringComparer.Ordinal));

        Assert.NotEmpty(fit);
        Assert.NotEmpty(score);
        Assert.All(fit, id => Assert.False(HeldOutSplit.IsHeldOut(id, 0.4)));
        Assert.All(score, id => Assert.True(HeldOutSplit.IsHeldOut(id, 0.4)));
    }

    /// <summary>
    /// The buckets are pinned so a refactor cannot move the split without failing: the rule is part of
    /// what a recorded command means, and a silently different partition is a different measurement.
    /// </summary>
    [Theory]
    [InlineData("q1", 561687)]
    [InlineData("q2", 189898)]
    [InlineData("0", 116079)]
    [InlineData("1406", 35850)]
    public void TheHashBucketsArePinned(string queryId, int bucket) =>
        Assert.Equal(bucket, HeldOutSplit.Bucket(queryId));

    [Fact]
    public void AHeldOutRunFitsOnTheFitSetAndScoresTheScoreSet()
    {
        BeirCorpus corpus = Corpus();
        var index = new InMemoryTextIndex();
        index.Index(Evaluation.BuildDocuments(corpus));

        var (results, _, _, heldOut) = Evaluation.Run(
            corpus, index, topK: 10, limit: null, Tokenizer.Default, heldOutFraction: 0.5);

        Assert.NotNull(heldOut);
        Assert.Null(heldOut.NotRunReason);
        Assert.Equal(2, heldOut.TotalQueries);
        Assert.Equal(1, heldOut.FitQueries);
        Assert.Equal(1, heldOut.ScoreQueries);

        // The fixed reference is measured on the score set too, or a held-out tuned figure has
        // nothing on the same queries to be compared with.
        Assert.NotNull(heldOut.UntunedBaseline);
        Assert.Equal(1, heldOut.UntunedBaseline.Queries);

        // q2 hashes below 0.5 and q1 above it, so the two queries land on opposite sides; a test whose
        // ids happened to share a side would be asserting a table over an empty set.
        Assert.Contains(heldOut.Rows, row => row.Name.StartsWith("BM25 tuned", StringComparison.Ordinal));
        Assert.All(heldOut.Rows, row =>
        {
            Assert.Equal(1, row.Fit.Queries);
            Assert.Equal(1, row.Score.Queries);
        });

        // The tuned rows in the main table are the fit-set figures, or the two tables would be
        // reporting the same number under two labels.
        Assert.All(
            results.Where(result => result.Name.Contains("tuned", StringComparison.Ordinal)),
            result => Assert.Equal(1, result.Queries));
    }

    [Fact]
    public void ADefaultRunFitsOnTheWholeQuerySetAndReportsNoSplit()
    {
        BeirCorpus corpus = Corpus();
        var index = new InMemoryTextIndex();
        index.Index(Evaluation.BuildDocuments(corpus));

        var (results, _, _, heldOut) = Evaluation.Run(
            corpus, index, topK: 10, limit: null, Tokenizer.Default);

        Assert.Null(heldOut);
        Assert.All(
            results.Where(result => result.Name.Contains("tuned", StringComparison.Ordinal)),
            result => Assert.Equal(2, result.Queries));
    }

    [Fact]
    public void AHeldOutRunWithNothingToFitReportsWhyItDidNotRun()
    {
        BeirCorpus corpus = Corpus();
        var index = new InMemoryTextIndex();
        index.Index(Evaluation.BuildDocuments(corpus));

        var (_, _, _, heldOut) = Evaluation.Run(
            corpus, index, topK: 10, limit: null, Tokenizer.Default, tuned: false, heldOutFraction: 0.5);

        // The block is present with a stated cause, so a missing held-out table is never read as
        // "the split found no gap".
        Assert.NotNull(heldOut);
        Assert.Empty(heldOut.Rows);
        Assert.NotNull(heldOut.NotRunReason);
        Assert.Contains("no tuned row was fitted", heldOut.NotRunReason, StringComparison.Ordinal);
    }

    private static BeirCorpus Corpus() => new()
    {
        Name = "test",
        Documents =
        [
            new BeirDocument("d1", "Bile salts", "bile salts emulsify dietary fat"),
            new BeirDocument("d2", "Vesicle fusion", "vesicle fusion releases neurotransmitters"),
            new BeirDocument("d3", "Bile composition", "bile acids and cholesterol"),
            new BeirDocument("d4", "Neurons", "synaptic vesicles fuse with the membrane"),
        ],
        Queries = [new BeirQuery("q1", "bile salts"), new BeirQuery("q2", "vesicle fusion")],
        TestRelevance = new Dictionary<string, IReadOnlyDictionary<string, double>>(StringComparer.Ordinal)
        {
            ["q1"] = new Dictionary<string, double> { ["d1"] = 1, ["d3"] = 1 },
            ["q2"] = new Dictionary<string, double> { ["d2"] = 1, ["d4"] = 1 },
        },
    };
}
