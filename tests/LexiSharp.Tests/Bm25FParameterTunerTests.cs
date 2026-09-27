using System.Diagnostics.CodeAnalysis;
using LexiSharp.Core;
using LexiSharp.Indexing;
using LexiSharp.Ranking;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// Covers <see cref="Bm25FParameterTuner"/>. The property that matters most is not that it finds a
/// good configuration but that it can report that <b>no</b> weighting beats leaving fields neutral —
/// which is the answer the BEIR measurements suggested was the likely one.
/// </summary>
public class Bm25FParameterTunerTests
{
    /// <summary>
    /// Six documents where the discriminating term sits in the title of one document and in the body
    /// of another, over bodies of similar length. Enough shape for a weighting to matter.
    /// </summary>
    private static InMemoryTextIndex Corpus()
    {
        var index = new InMemoryTextIndex();

        index.Index(new[]
        {
            Document("d1", "a guide to ranking documents by their statistics and terms",
                "obsidian"),
            Document("d2", "notes about the ranking of search results in practice",
                "ranking"),
            Document("d3", "how term statistics drive the ranking of any result list",
                "ranking"),
            Document("d4", "an unrelated note about cooking pasta and olive oil",
                "cooking"),
            Document("d5", "term frequency and document frequency explained carefully",
                "ranking"),
            Document("d6", "a second unrelated note about gardening tools and soil",
                "gardening"),
        });

        return index;

        static SearchDocument Document(string id, string body, string title) =>
            new(id, body, TextFields: new Dictionary<string, string> { ["title"] = title });
    }

    private static IReadOnlyList<Bm25ValidationQuery> Queries() =>
    [
        new("ranking", ["d1", "d2", "d3"]),
        new("term statistics", ["d1", "d5"]),
        new("cooking", ["d4"]),
        new("gardening", ["d6"]),
    ];

    [Fact]
    public void ItFindsAScorerThatSatisfiesTheValidationSet()
    {
        var result = new Bm25FParameterTuner(Corpus(), Queries()).Tune(
            k1Values: [0.9, 1.2, 1.5],
            bValues: [0.5, 0.75],
            weightedFields: ["title"],
            weightValues: [1.0, 2.0],
            topK: 3,
            metric: TuningMetric.Recall);

        Assert.InRange(result.MetricScore, 0.0, 1.0);
        Assert.True(result.MetricScore > 0, "the corpus is easy enough that tuning should find a real score");

        // The grid is the honest cost record: 6 (k1 x b) + 2 weights = 8 configurations.
        Assert.Equal(8, result.EvaluatedConfigurations);
        Assert.Equal(8, result.Grid.Count);
    }

    [Fact]
    public void TheFirstStageSearchesK1AndBWithNeutralFields()
    {
        var result = new Bm25FParameterTuner(Corpus(), Queries()).Tune(
            k1Values: [0.9, 1.5],
            bValues: [0.5, 0.75],
            weightedFields: ["title"],
            weightValues: [1.0, 2.0],
            metric: TuningMetric.Recall);

        var firstStage = result.Grid.Where(point => point.FieldWeights is null).ToList();

        // 2 x 2 unweighted points, then 2 weighted ones at the stage-1 winner's (k1, b).
        Assert.Equal(4, firstStage.Count);

        double bestUnweighted = firstStage.Max(point => point.MetricScore);
        Assert.Equal(bestUnweighted, result.UnweightedMetricScore, 12);

        foreach (var point in result.Grid.Where(p => p.FieldWeights is not null))
        {
            Assert.Equal(
                firstStage.OrderByDescending(p => p.MetricScore).First().K1,
                point.K1,
                12);
        }
    }

    [Fact]
    public void WeightingThatDoesNotHelpIsReportedAsNotHelping()
    {
        // A corpus where the title carries nothing the query needs: weighting can only add noise.
        var index = new InMemoryTextIndex();

        index.Index(new[]
        {
            new SearchDocument("d1", "ranking documents by their statistics",
                TextFields: new Dictionary<string, string> { ["title"] = "notes" }),
            new SearchDocument("d2", "the ranking of search results in practice",
                TextFields: new Dictionary<string, string> { ["title"] = "notes" }),
            new SearchDocument("d3", "term statistics and document frequency",
                TextFields: new Dictionary<string, string> { ["title"] = "notes" }),
        });

        var queries = new[] { new Bm25ValidationQuery("ranking", ["d1", "d2"]) };

        var result = new Bm25FParameterTuner(index, queries).Tune(
            k1Values: [1.2],
            bValues: [0.75],
            weightedFields: ["title"],
            weightValues: [1.0, 2.0, 3.0],
            metric: TuningMetric.Recall);

        // The whole point of the design: a weighting that buys nothing is a reported answer, not a
        // failure and not a silently accepted "improvement".
        Assert.False(result.WeightingHelped);
        Assert.Equal(result.UnweightedMetricScore, result.MetricScore, 12);
    }

    [Fact]
    public void AWeightGridContainingTheNeutralValueCanConcludeNeutralIsBest()
    {
        var result = new Bm25FParameterTuner(Corpus(), Queries()).Tune(
            k1Values: [1.2],
            bValues: [0.75],
            weightedFields: ["title"],
            weightValues: [1.0, 1.5, 2.0, 3.0],
            metric: TuningMetric.Recall);

        // Documented contract: a tuner's Parameters.FieldWeights is never null, so a caller can index
        // it without a null check. Asserted rather than silenced with '!'.
        Assert.NotNull(result.Parameters.FieldWeights);
        var weights = result.Parameters.FieldWeights;

        if (result.WeightingHelped)
        {
            // A weight was searched for and one beat neutral, so it is reported.
            Assert.True(weights["title"] > 1.0);
            Assert.True(result.MetricScore > result.UnweightedMetricScore);
        }
        else
        {
            // Nothing beat neutral, and ties keep the FIRST point evaluated, so the winner is the
            // unweighted one from stage 1: an empty weight map, not {title: 1.0}. Both describe the
            // same ranking, and WeightingHelped is how the caller tells neutral from tuned.
            Assert.Empty(weights);
            Assert.Equal(result.UnweightedMetricScore, result.MetricScore, 12);
        }
    }

    [Fact]
    public void TheWinningParametersFeedStraightIntoTheScorer()
    {
        var index = Corpus();
        var result = new Bm25FParameterTuner(index, Queries()).Tune(
            weightedFields: ["title"],
            weightValues: [1.0, 2.0],
            metric: TuningMetric.Recall);

        var engine = new RankedTextSearchEngine(index, new Bm25FScorer(result.Parameters));

        // Must not throw, and must actually retrieve something for a known-good query.
        Assert.NotEmpty(engine.Search("ranking"));
    }

    [Fact]
    public void NoWeightedFieldsReducesItToAPlainK1AndBsearch()
    {
        var result = new Bm25FParameterTuner(Corpus(), Queries()).Tune(
            k1Values: [1.0, 1.5],
            bValues: [0.5, 0.75],
            metric: TuningMetric.Recall);

        // 2 x 2 unweighted, plus a single empty weighting product.
        Assert.Equal(5, result.Grid.Count);
        Assert.Empty(result.Parameters.FieldWeights!);
        Assert.False(result.WeightingHelped);
    }

    [Fact]
    public void SeveralFieldsSearchTheirCartesianProduct()
    {
        var index = new InMemoryTextIndex();

        index.Index(new[]
        {
            new SearchDocument("d1", "ranking by statistics and by terms", TextFields:
                new Dictionary<string, string> { ["title"] = "obsidian", ["summary"] = "ranking" }),
            new SearchDocument("d2", "the ranking of results in practice", TextFields:
                new Dictionary<string, string> { ["title"] = "obsidian", ["summary"] = "notes" }),
        });

        var result = new Bm25FParameterTuner(
            index, [new Bm25ValidationQuery("ranking", ["d1"])]).Tune(
            k1Values: [1.2],
            bValues: [0.75],
            weightedFields: ["title", "summary"],
            weightValues: [1.0, 2.0],
            metric: TuningMetric.Recall);

        // 1 unweighted, then 2 x 2 weight combinations across the two fields.
        Assert.Equal(5, result.Grid.Count);
        Assert.Equal(4, result.Grid.Count(point => point.FieldWeights is { Count: > 0 }));

        // Whether weighting won is the corpus's business, not the test's: assert the result is
        // usable either way.
        var engine = new RankedTextSearchEngine(index, new Bm25FScorer(result.Parameters));
        Assert.NotEmpty(engine.Search("ranking"));
    }

    [Fact]
    public void TheSearchIsRefusedAboveTheConfigurationCapWithTheCount()
    {
        // 5 k1 x 5 b = 25 unweighted, plus 8 weights for one field = 33 configurations. Refused
        // because the cap is set to 10, not because 33 is large in itself.
        var exception = Assert.Throws<ArgumentException>(() =>
            new Bm25FParameterTuner(Corpus(), Queries()).Tune(
                weightedFields: ["title"],
                weightValues: [1.0, 2.0, 3.0, 4.0, 5.0, 6.0, 7.0, 8.0],
                k1Values: [0.5, 1.0, 1.2, 1.5, 2.0],
                bValues: [0.0, 0.25, 0.5, 0.75, 1.0],
                maxConfigurations: 10));

        // The message carries the real count and the cap, so the caller can decide rather than guess.
        Assert.Contains("33", exception.Message, StringComparison.Ordinal);
        Assert.Contains("10", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCapIsEnforcedAndRaisableDeliberately()
    {
        var tuner = new Bm25FParameterTuner(Corpus(), Queries());

        Assert.Throws<ArgumentException>(() => tuner.Tune(
            k1Values: [1.0, 1.5], bValues: [0.5, 0.75],
            weightedFields: ["title"], weightValues: [1.0, 2.0, 3.0],
            maxConfigurations: 5));

        var raised = tuner.Tune(
            k1Values: [1.0, 1.5], bValues: [0.5, 0.75],
            weightedFields: ["title"], weightValues: [1.0, 2.0, 3.0],
            maxConfigurations: 20);

        Assert.Equal(7, raised.EvaluatedConfigurations);
    }

    [Fact]
    public void AFieldTheIndexDoesNotHaveIsRefusedByName()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            new Bm25FParameterTuner(Corpus(), Queries()).Tune(weightedFields: ["nope"]));

        Assert.Contains("nope", exception.Message, StringComparison.Ordinal);
        Assert.Contains("title", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ArgumentValidationMatchesTheBm25Tuner()
    {
        Assert.Throws<ArgumentNullException>(() => new Bm25FParameterTuner(null!, Queries()));
        Assert.Throws<ArgumentNullException>(() => new Bm25FParameterTuner(Corpus(), null!));

        Assert.Throws<ArgumentException>(
            () => new Bm25FParameterTuner(Corpus(), []));

        Assert.Throws<ArgumentException>(() => new Bm25FParameterTuner(
            Corpus(), [new Bm25ValidationQuery("ranking", [])]));

        var tuner = new Bm25FParameterTuner(Corpus(), Queries());

        Assert.Throws<ArgumentException>(() => tuner.Tune(k1Values: []));
        Assert.Throws<ArgumentException>(() => tuner.Tune(bValues: []));
        Assert.Throws<ArgumentException>(() => tuner.Tune(weightedFields: ["title"], weightValues: []));
        Assert.Throws<ArgumentOutOfRangeException>(() => tuner.Tune(k1Values: [-1]));
        Assert.Throws<ArgumentOutOfRangeException>(() => tuner.Tune(bValues: [1.5]));
        Assert.Throws<ArgumentOutOfRangeException>(() => tuner.Tune(weightValues: [-1]));
        Assert.Throws<ArgumentOutOfRangeException>(() => tuner.Tune(topK: 0));

        // A blank field name is an argument error about the name, not about a range.
        Assert.Throws<ArgumentException>(() => tuner.Tune(weightedFields: [" "]));
    }

    [Fact]
    public void AnIndexWithoutFieldStatisticsIsRefusedByName()
    {
        // InMemoryTextIndex always tracks fields, so the refusal is asserted through an index that
        // reports it does not — the shape a hand-written ITextIndex without the capability takes.
        var inner = new InMemoryTextIndex();
        inner.Index([new SearchDocument("1", "a plain document")]);

        var exception = Assert.Throws<NotSupportedException>(
            () => new Bm25FParameterTuner(new NoFieldStatistics(inner), Queries()));

        Assert.Contains("BM25F", exception.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(Bm25ParameterTuner), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TiesAreBrokenDeterministically()
    {
        var tuner = new Bm25FParameterTuner(Corpus(), Queries());

        var first = tuner.Tune(k1Values: [1.0, 1.2, 1.5], bValues: [0.5, 0.75], metric: TuningMetric.Recall);
        var second = tuner.Tune(k1Values: [1.0, 1.2, 1.5], bValues: [0.5, 0.75], metric: TuningMetric.Recall);

        Assert.Equal(first.Parameters.K1, second.Parameters.K1);
        Assert.Equal(first.Parameters.B, second.Parameters.B);
        Assert.Equal(first.MetricScore, second.MetricScore);
    }

    [Fact]
    public void EveryMetricIsSupported()
    {
        var tuner = new Bm25FParameterTuner(Corpus(), Queries());

        foreach (TuningMetric metric in Enum.GetValues<TuningMetric>())
        {
            var result = tuner.Tune(
                k1Values: [1.2], bValues: [0.75],
                weightedFields: ["title"], weightValues: [1.0, 2.0],
                metric: metric);

            Assert.InRange(result.MetricScore, 0.0, 1.0);
        }
    }

    /// <summary>Delegates everything but reports no per-field statistics, like a flat index.</summary>
    private sealed class NoFieldStatistics(ITextIndex inner) : ITextIndex
    {
        public IReadOnlyCollection<SearchDocument> Documents => inner.Documents;

        public int Count => inner.Count;

        public double AverageDocumentLength => inner.AverageDocumentLength;

        public int VocabularySize => inner.VocabularySize;

        public long CorpusTokenCount => inner.CorpusTokenCount;

        public void Index(IEnumerable<SearchDocument> documents) => inner.Index(documents);

        public void Add(SearchDocument document) => inner.Add(document);

        public bool Remove(string documentId) => inner.Remove(documentId);

        public void Clear() => inner.Clear();

        public bool Contains(string documentId) => inner.Contains(documentId);

        public IReadOnlyList<string> GetTerms(string documentId) => inner.GetTerms(documentId);

        public IReadOnlyList<int> GetTermPositions(string documentId, string term) =>
            inner.GetTermPositions(documentId, term);

        public int DocumentFrequency(string term) => inner.DocumentFrequency(term);

        public int CorpusFrequency(string term) => inner.CorpusFrequency(term);

        public int TermFrequency(string documentId, string term) => inner.TermFrequency(documentId, term);

        public int DocumentLength(string documentId) => inner.DocumentLength(documentId);

        public bool TryGetDocument(string documentId, [NotNullWhen(true)] out SearchDocument? document) =>
            inner.TryGetDocument(documentId, out document);
    }
}
