using System.Linq;
using LexiSharp.Core;
using LexiSharp.Embeddings;
using LexiSharp.Hybrid;
using LexiSharp.Indexing;
using LexiSharp.Ranking;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// A weighted merge reports the weights and divisors it applied, so a final score can be checked
/// by hand instead of trusted.
/// </summary>
/// <remarks>
/// The gap these tests close is specific: <see cref="DetailedSearchResult.Contributions"/> gives the
/// raw per-engine scores, and a weighted blend's final score is <c>Σ wᵢ·sᵢ/maxᵢ</c> — with neither
/// <c>wᵢ</c> nor <c>maxᵢ</c> anywhere in the result. A reader could see 0.4 and 0.9 contribute to a
/// 0.62 and had no way to find out where the 0.62 came from.
/// </remarks>
public class MergeExplanationTests
{
    private static readonly SearchDocument AnyDocument = new("d", "text");

    private static SearchResult Hit(string id, double score) => new(id, score, AnyDocument);

    /// <summary>
    /// Two engines with deliberately different scales: the first tops out at 2, the second at 0.5.
    /// A normalization taken from a shared maximum would be visibly wrong on these.
    /// </summary>
    private static IReadOnlyList<IReadOnlyList<SearchResult>> TwoEngines() =>
    [
        [Hit("a", 2.0), Hit("b", 1.0)],
        [Hit("a", 0.5)],
    ];

    [Fact]
    public void TheWeightsAreTheOnesTheMergeApplied()
    {
        var merger = new WeightedScoreResultMerger(2, 1);

        var explanation = merger.Explain(TwoEngines());

        Assert.Equal([2.0, 1.0], explanation.Weights);
    }

    [Fact]
    public void OneWeightIsBroadcastToEveryEngineAsTheMergeDoesIt()
    {
        // The merger accepts a single weight and repeats it for every engine; an explanation that
        // reported the configured array (length one) would name one engine for a two-engine merge.
        var merger = new WeightedScoreResultMerger(3);

        var explanation = merger.Explain(TwoEngines());

        Assert.Equal([3.0, 3.0], explanation.Weights);
    }

    [Fact]
    public void TheNormalisationIsEachEnginesOwnMaximum()
    {
        var merger = new WeightedScoreResultMerger(1, 1);

        var explanation = merger.Explain(TwoEngines());

        // Each engine divides by its own largest score. Taking the maximum across engines would
        // shrink the second engine's contribution by four and nothing in the output would say so.
        Assert.Equal([2.0, 0.5], explanation.Normalization);
    }

    [Fact]
    public void AnEngineThatReturnedNothingNormalisesToZero()
    {
        var merger = new WeightedScoreResultMerger(1, 1);

        var explanation = merger.Explain([new[] { Hit("a", 2.0) }, Array.Empty<SearchResult>()]);

        Assert.Equal([2.0, 0.0], explanation.Normalization);
    }

    [Fact]
    public void TheExplanationThrowsForTheSameWeightMismatchTheMergeRefuses()
    {
        // Two ways to compute the same arithmetic must agree about what is an error, or a caller's
        // error path depends on whether tracing happened to be on.
        var merger = new WeightedScoreResultMerger(1, 1, 1);

        Assert.Throws<InvalidOperationException>(() => merger.Merge(TwoEngines(), "q"));
        Assert.Throws<InvalidOperationException>(() => merger.Explain(TwoEngines()));
    }

    [Fact]
    public void TheExplanationReproducesTheScoreTheMergeProduced()
    {
        // The whole point. For every document the blend returned, w·s/max summed over the engines
        // that returned it must equal the score the merger wrote — otherwise the explanation is
        // two plausible columns that do not explain anything.
        var merger = new WeightedScoreResultMerger(2, 1);
        var engines = TwoEngines();

        var merged = merger.Merge(engines, "query");
        var explanation = merger.Explain(engines);

        Assert.NotEmpty(merged);

        foreach (var result in merged)
        {
            double rebuilt = 0;

            for (int engine = 0; engine < engines.Count; engine++)
            {
                var hit = engines[engine].FirstOrDefault(candidate => candidate.DocumentId == result.DocumentId);

                if (hit.DocumentId is null)
                    continue;

                double divisor = explanation.Normalization[engine];

                rebuilt += explanation.Weights[engine] * (divisor > 0 ? hit.Score / divisor : 0);
            }

            Assert.Equal(result.Score, rebuilt, 10);
        }
    }

    [Fact]
    public void ARankOnlyMergerDoesNotClaimToExplain()
    {
        // A rank merger reads order, not scores, so there is no weight and no divisor to report.
        // Reporting zeros would be worse than not implementing the contract.
        Assert.False(typeof(IMergeExplainer).IsAssignableFrom(typeof(ReciprocalRankFusionMerger)));
    }

    [Fact]
    public void TheTraceCarriesTheMergeExplanationOncePerSearch()
    {
        var trace = new SearchTrace();

        HybridSearch(sourceNames: ["lexical", "semantic"], trace: trace);

        var queryLevel = trace.Steps
            .Where(step => step.Stage == TraceStage.Merge && step.DocumentId.Length == 0)
            .ToArray();

        // Once for the search, not once per document: the weights and the divisors are properties
        // of the query's pools, and repeating them on every row would be ten copies of one fact.
        Assert.Single(queryLevel);
        Assert.Contains("weights lexical=1 semantic=1", queryLevel[0].Detail);
        Assert.Contains("max lexical=", queryLevel[0].Detail);
        Assert.Contains("semantic=", queryLevel[0].Detail);
    }

    [Fact]
    public void TheExplanationIsRecordedBeforeTheDocumentsItExplains()
    {
        // A reader walking the trace meets the arithmetic before the rows that used it, the same
        // way a Route step precedes the scores of the route it chose.
        var trace = new SearchTrace();

        HybridSearch(sourceNames: ["lexical", "semantic"], trace: trace);

        int explanation = trace.Steps.ToList().FindIndex(step => step.Stage == TraceStage.Merge && step.DocumentId.Length == 0);
        int firstDocument = trace.Steps
            .ToList()
            .FindIndex(step => step.Stage == TraceStage.Merge && step.DocumentId.Length > 0);

        Assert.True(explanation >= 0);
        Assert.True(firstDocument < 0 || explanation < firstDocument);
    }

    [Fact]
    public void TheExplanationLabelsTheSourcesThePerDocumentStepsUse()
    {
        // The two readings have to be joinable by label; a positional explanation would line up
        // only if the reader also knew the engine order the engine happened to use.
        var trace = new SearchTrace();

        HybridSearch(sourceNames: ["zeta", "alpha"], trace: trace);

        var detail = trace.Steps
            .Single(step => step.Stage == TraceStage.Merge && step.DocumentId.Length == 0)
            .Detail!;

        // Sorted by label, like every other stable detail the trace records.
        Assert.Contains("weights alpha=1 zeta=1", detail);
        Assert.Contains("max alpha=", detail);
    }

    private static IReadOnlyList<SearchResult> HybridSearch(string[] sourceNames, SearchTrace trace)
    {
        var corpus = new[] { new SearchDocument("a", "token token"), new SearchDocument("b", "token") };

        var first = new RankedTextSearchEngine(new InMemoryTextIndex(), new Bm25Scorer());
        var second = new InMemoryVectorSearchEngine(new HashingEmbeddingProvider());

        first.Index(corpus);
        second.Index(corpus);

        var hybrid = new HybridTextSearchEngine(
            [first, second],
            new WeightedScoreResultMerger(1, 1),
            sourceNames: sourceNames);

        return hybrid.Search("token", new SearchOptions(Limit: 2, Trace: trace));
    }
}
