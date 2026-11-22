using LexiSharp.Core;
using LexiSharp.Indexing;
using LexiSharp.Ranking;
using Xunit;

namespace LexiSharp.Tests;

public class Bm25TunerTests
{
    [Fact]
    public void Tune_ReturnsABestConfigurationWithinTheGrid()
    {
        var index = new InMemoryTextIndex();
        index.Add(new SearchDocument("d1", "the quick brown fox jumps over the lazy dog"));
        index.Add(new SearchDocument("d2", "a quick brown fox"));
        index.Add(new SearchDocument("d3", "the lazy dog sleeps"));
        index.Add(new SearchDocument("d4", "quick fox"));

        var validation = new (string Query, IReadOnlyDictionary<string, double> Relevance)[]
        {
            ("quick fox", new Dictionary<string, double> { ["d1"] = 1, ["d2"] = 1, ["d4"] = 1 }),
            ("lazy dog", new Dictionary<string, double> { ["d1"] = 1, ["d3"] = 1 }),
        };

        var result = Bm25Tuner.Tune(index, validation, k: 10);

        Assert.True(result.BestK1 >= 0);
        Assert.InRange(result.BestB, 0, 1);
        Assert.True(result.BestMeanNdcg >= 0 && result.BestMeanNdcg <= 1);
        Assert.Equal(Bm25Tuner.DefaultK1Grid.Length * Bm25Tuner.DefaultBGrid.Length, result.Grid.Count);
        Assert.True(result.BestLower <= result.BestMeanNdcg);
        Assert.True(result.BestUpper >= result.BestMeanNdcg);
    }

    [Fact]
    public void Tune_ThrowsOnEmptyValidationSet()
    {
        var index = new InMemoryTextIndex();
        index.Add(new SearchDocument("d1", "hello world"));

        Assert.Throws<ArgumentException>(() => Bm25Tuner.Tune(index, Array.Empty<(string, IReadOnlyDictionary<string, double>)>()));
    }
}