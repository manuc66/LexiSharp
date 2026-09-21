using LexiSharp.Core;
using LexiSharp.Hybrid;
using Xunit;

namespace LexiSharp.Tests;

public class CascadeRerankPipelineTests
{
    private static readonly SearchDocument DocA = new("a", "alpha");
    private static readonly SearchDocument DocB = new("b", "beta");
    private static readonly SearchDocument DocC = new("c", "gamma");
    private static readonly SearchDocument DocD = new("d", "delta");

    private static SearchResult R(string id, SearchDocument doc, double score = 1.0) => new(id, score, doc);

    /// <summary>Reverses the list and records its size, to observe what reaches the stage.</summary>
    private sealed class RecordingReranker : IReranker
    {
        public List<int> ReceivedCounts { get; } = new();
        public string Name => "Recording";

        public IReadOnlyList<SearchResult> Rerank(string query, IReadOnlyList<SearchResult> candidates)
        {
            ReceivedCounts.Add(candidates.Count);
            return candidates.Reverse().ToList();
        }
    }

    private sealed class PassthroughReranker : IReranker
    {
        public string Name => "Pass";
        public IReadOnlyList<SearchResult> Rerank(string query, IReadOnlyList<SearchResult> candidates) => candidates;
    }

    [Fact]
    public void Rerank_ChainsStages_LastStageWinsOnOrder()
    {
        // Two reversing stages: a → b → a, the final order is the original one.
        var pipeline = new CascadeRerankPipeline(new[] { new RecordingReranker(), new RecordingReranker() });

        var results = pipeline.Rerank("q", new[] { R("a", DocA), R("b", DocB), R("c", DocC) });

        Assert.Equal(new[] { "a", "b", "c" }, results.Select(r => r.DocumentId).ToArray());
    }

    [Fact]
    public void Rerank_StageLimit_TrimsBetweenStages()
    {
        var first = new RecordingReranker();
        var second = new RecordingReranker();
        var pipeline = new CascadeRerankPipeline(
            new IReranker[] { first, second },
            new CascadeRerankOptions(StageLimit: 2));

        pipeline.Rerank("q", new[] { R("a", DocA), R("b", DocB), R("c", DocC), R("d", DocD) });

        // Stage 1 saw everything; stage 2 only the 2 strongest candidates of stage 1's order.
        Assert.Single(first.ReceivedCounts);
        Assert.Single(second.ReceivedCounts);
        Assert.Equal(4, first.ReceivedCounts[0]);
        Assert.Equal(2, second.ReceivedCounts[0]);
    }

    [Fact]
    public void Rerank_FinalLimit_TrimsOutput()
    {
        var pipeline = new CascadeRerankPipeline(
            new[] { new RecordingReranker() },
            new CascadeRerankOptions(FinalLimit: 2));

        var results = pipeline.Rerank("q", new[] { R("a", DocA), R("b", DocB), R("c", DocC) });

        Assert.Equal(2, results.Count);
    }

    [Fact]
    public void Rerank_MinimumScore_AppliesAfterLastStage()
    {
        var pipeline = new CascadeRerankPipeline(
            new IReranker[] { new PassthroughReranker() },
            new CascadeRerankOptions(MinimumScore: 1.5));

        var results = pipeline.Rerank("q", new[]
        {
            R("a", DocA, 2.0), R("b", DocB, 1.0),
        });

        Assert.Single(results, r => r.DocumentId == "a");
    }

    [Fact]
    public void Rerank_BrokenScoresAreDropped()
    {
        var pipeline = new CascadeRerankPipeline(new[] { new PassthroughReranker() });

        var results = pipeline.Rerank("q", new[]
        {
            R("a", DocA, double.NaN), R("b", DocB, 0.0), R("c", DocC, 1.0),
        });

        Assert.Single(results, r => r.DocumentId == "c");
    }

    [Fact]
    public void Rerank_CascadesNest()
    {
        var inner = new CascadeRerankPipeline(new[] { new RecordingReranker(), new RecordingReranker() });
        var outer = new CascadeRerankPipeline(new IReranker[] { inner, new RecordingReranker() });

        var results = outer.Rerank("q", new[] { R("a", DocA), R("b", DocB) });

        // Two reversals inside, one outside: b then a.
        Assert.Equal(new[] { "b", "a" }, results.Select(r => r.DocumentId).ToArray());
    }

    [Fact]
    public void Rerank_EmptyCandidates_YieldEmpty()
    {
        var pipeline = new CascadeRerankPipeline(new[] { new PassthroughReranker() });

        Assert.Empty(pipeline.Rerank("q", Array.Empty<SearchResult>()));
    }

    [Fact]
    public void Ctor_ValidatesStages()
    {
        Assert.Throws<ArgumentException>(() => new CascadeRerankPipeline(Array.Empty<IReranker>()));
        Assert.Throws<ArgumentException>(() => new CascadeRerankPipeline(new IReranker[] { null! }));
        Assert.Throws<ArgumentNullException>(() => new CascadeRerankPipeline(null!));
    }

    [Fact]
    public void Ctor_RejectsInvalidOptions()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new CascadeRerankPipeline(new[] { new PassthroughReranker() }, new CascadeRerankOptions(StageLimit: 0)));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new CascadeRerankPipeline(new[] { new PassthroughReranker() }, new CascadeRerankOptions(FinalLimit: -1)));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new CascadeRerankPipeline(new[] { new PassthroughReranker() }, new CascadeRerankOptions(MinimumScore: double.NaN)));
    }
}
