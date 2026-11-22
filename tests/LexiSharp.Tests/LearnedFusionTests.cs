using LexiSharp.Core;
using LexiSharp.Hybrid;
using Xunit;

namespace LexiSharp.Tests;

public class LearnedFusionTests
{
    [Fact]
    public void Merge_BlendsScoresWithLearnedWeights()
    {
        var merger = new LearnedFusionMerger(new[] { 1.0, 2.0 });

        var engine1 = new[] { new SearchResult("d1", 1.0, new SearchDocument("d1", "a")), new SearchResult("d2", 0.5, new SearchDocument("d2", "b")) };
        var engine2 = new[] { new SearchResult("d1", 1.0, new SearchDocument("d1", "a")), new SearchResult("d3", 0.2, new SearchDocument("d3", "c")) };

        var merged = merger.Merge(new[] { engine1, engine2 }, "q");

        Assert.Equal(3, merged.Count);
        // d1 appears in both engines with high scores → should be first.
        Assert.Equal("d1", merged[0].DocumentId);
    }

    [Fact]
    public void Merge_ThrowsOnEngineCountMismatch()
    {
        var merger = new LearnedFusionMerger(new[] { 1.0, 2.0 });
        var single = new[] { new[] { new SearchResult("d1", 1.0, new SearchDocument("d1", "a")) } };

        Assert.Throws<ArgumentException>(() => merger.Merge(single, "q"));
    }

    [Fact]
    public void FusionWeightTuner_Tune_ReturnsBestWeights()
    {
        var engine1 = new[] { new SearchResult("d1", 1.0, new SearchDocument("d1", "a")), new SearchResult("d2", 0.5, new SearchDocument("d2", "b")) };
        var engine2 = new[] { new SearchResult("d1", 0.8, new SearchDocument("d1", "a")), new SearchResult("d3", 1.0, new SearchDocument("d3", "c")) };

        var validation = new[]
        {
            ((IReadOnlyList<IReadOnlyList<SearchResult>>)new[] { (IReadOnlyList<SearchResult>)engine1, (IReadOnlyList<SearchResult>)engine2 },
             (IReadOnlyDictionary<string, double>)new Dictionary<string, double> { ["d1"] = 1.0 })
        };

        var result = FusionWeightTuner.Tune(validation);

        Assert.Equal(2, result.BestWeights.Count);
        Assert.True(result.BestMeanNdcg >= 0);
        Assert.True(result.Grid.Count > 0);
    }
}