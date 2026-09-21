using LexiSharp.Core;
using LexiSharp.Hybrid;
using Xunit;

namespace LexiSharp.Tests;

public class WeightedScoreResultMergerTests
{
    private static SearchResult R(string id, double score) =>
        new(id, score, new SearchDocument(id, id));

    [Fact]
    public void Merge_NormalizesPerEngineThenBlendsWithWeights()
    {
        var engineA = new[] { R("a", 10.0), R("b", 5.0) };
        var engineB = new[] { R("a", 2.0), R("b", 1.0), R("c", 0.5) };

        var merged = new WeightedScoreResultMerger(1.0, 1.0)
            .Merge(new[] { engineA, engineB }, "query").ToList();

        Assert.Equal(new[] { "a", "b", "c" }, merged.Select(r => r.DocumentId).ToArray());
        // a: 10/10 + 2/2 = 2.0 ; b: 5/10 + 1/2 = 1.0 ; c: 0 + 0.5/2 = 0.25
        Assert.Equal(2.0, merged[0].Score, 9);
        Assert.Equal(1.0, merged[1].Score, 9);
        Assert.Equal(0.25, merged[2].Score, 9);
    }

    [Fact]
    public void Merge_ZeroWeightEngineContributesNothing()
    {
        var engineA = new[] { R("a", 10.0), R("b", 5.0) };
        var engineB = new[] { R("a", 2.0), R("c", 0.5) };

        var merged = new WeightedScoreResultMerger(0.0, 1.0)
            .Merge(new[] { engineA, engineB }, "query").ToList();

        // Only engine B (normalized by its max 2.0) survives.
        Assert.Equal(new[] { "a", "c" }, merged.Select(r => r.DocumentId).ToArray());
        Assert.Equal(1.0, merged[0].Score, 9);
        Assert.Equal(0.25, merged[1].Score, 9);
    }

    [Fact]
    public void Merge_SingleWeightBroadcastsToAllEngines()
    {
        var engineA = new[] { R("a", 10.0) };
        var engineB = new[] { R("a", 2.0) };

        var merged = new WeightedScoreResultMerger(3.0)
            .Merge(new[] { engineA, engineB }, "query").Single();

        // 3 * (10/10) + 3 * (2/2) = 6.
        Assert.Equal(6.0, merged.Score, 9);
    }

    [Fact]
    public void Merge_AllNonPositiveScoresContributeNothing()
    {
        var engineA = new[] { R("a", -5.0), R("b", 0.0) };
        var engineB = new[] { R("a", 2.0) };

        var merged = new WeightedScoreResultMerger()
            .Merge(new[] { engineA, engineB }, "query");

        // Engine A's max is 0, so its divisor is 0 and it contributes nothing; b only ever
        // appeared with a non-positive score, so it is dropped entirely.
        var only = Assert.Single(merged);
        Assert.Equal("a", only.DocumentId);
        Assert.Equal(1.0, only.Score, 9);
    }

    [Fact]
    public void Merge_EmptyLists_ReturnEmpty()
    {
        var merged = new WeightedScoreResultMerger()
            .Merge(new[] { Array.Empty<SearchResult>(), new[] { R("a", 1.0) } }, "query");

        Assert.Single(merged);
        Assert.Empty(new WeightedScoreResultMerger()
            .Merge(new[] { Array.Empty<SearchResult>() }, "query"));
    }

    [Fact]
    public void Name_IsWeightedScore() =>
        Assert.Equal("WeightedScore", new WeightedScoreResultMerger().Name);

    [Fact]
    public void Ctor_RejectsNegativeWeights() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new WeightedScoreResultMerger(1.0, -0.5));

    [Fact]
    public void Ctor_RejectsNonFiniteWeights()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new WeightedScoreResultMerger(double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WeightedScoreResultMerger(1.0, double.PositiveInfinity));
    }

    [Fact]
    public void Merge_NanOrInfiniteScores_DoNotPoisonTheBlend()
    {
        var engineA = new[] { R("a", 2.0), R("b", double.PositiveInfinity) };
        var engineB = new[] { R("b", double.NaN), R("c", 4.0) };

        var merged = new WeightedScoreResultMerger()
            .Merge(new[] { engineA, engineB }, "query").ToList();

        // Engine A normalizes by 2 (the infinite "b" is skipped): a → 1.0.
        // Engine B normalizes by 4 (the NaN "b" is skipped): c → 1.0.
        Assert.Equal(new[] { "a", "c" }, merged.Select(r => r.DocumentId).ToArray());
        Assert.Equal(1.0, merged[0].Score, 9);
        Assert.Equal(1.0, merged[1].Score, 9);
    }

    [Fact]
    public void Merge_WrongWeightCount_Throws()
    {
        var engineA = new[] { R("a", 1.0) };
        var engineB = new[] { R("b", 1.0) };

        var merger = new WeightedScoreResultMerger(1.0, 1.0, 1.0);

        Assert.Throws<InvalidOperationException>(() => merger.Merge(new[] { engineA, engineB }, "query"));
    }
}
