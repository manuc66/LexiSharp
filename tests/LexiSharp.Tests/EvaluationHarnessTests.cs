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

        var (results, _, notes) = Evaluation.Run(
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

        var (results, _, notes) = Evaluation.Run(
            corpus, index, topK: 10, limit: null, Tokenizer.Default, tuned: false);

        Assert.DoesNotContain(results, result => result.Name.StartsWith("BM25F tuned", StringComparison.Ordinal));
        Assert.DoesNotContain(notes, note => note.Contains("WeightingHelped=", StringComparison.Ordinal));
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
