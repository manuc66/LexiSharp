using System.Linq;
using LexiSharp.Core;
using LexiSharp.Demo;
using LexiSharp.Linguistics;
using Xunit;

namespace LexiSharp.Tests;

public sealed class DemoSearchServiceLanesTests
{
    [Fact]
    public void Compare_ExposesAllSearchLanes()
    {
        var corpus = DemoCorpusCatalog.Load(DemoCorpusCatalog.BuiltIn, null, "/nonexistent");
        var service = new DemoSearchService(corpus, WordSegmentation.UnicodeWordBoundaries);

        var comparison = service.Compare("refresh token", limit: 5);

        var keys = comparison.Lanes.Select(l => l.Key).ToList();

        Assert.Contains("lexical", keys);
        Assert.Contains("semantic", keys);
        Assert.Contains("dense", keys);
        Assert.Contains("hybrid", keys);
        Assert.Contains("rerank", keys);
        Assert.Contains("reformulate", keys);
        Assert.Contains("mmr", keys);
        Assert.Contains("contextual", keys);
        Assert.Contains("cascade", keys);
        Assert.Contains("bayesian", keys);
        Assert.Contains("segment", keys);
    }

    [Fact]
    public void NewLanes_ReturnHitsForAKnownQuery()
    {
        var corpus = DemoCorpusCatalog.Load(DemoCorpusCatalog.BuiltIn, null, "/nonexistent");
        var service = new DemoSearchService(corpus, WordSegmentation.UnicodeWordBoundaries);

        var comparison = service.Compare("refresh token", limit: 5);

        foreach (string key in new[] { "reformulate", "mmr", "contextual", "cascade" })
        {
            var lane = comparison.Lanes.Single(l => l.Key == key);
            Assert.NotEmpty(lane.Hits);
        }
    }

    [Fact]
    public void Compare_WithPhraseQuery_DoesNotThrow()
    {
        var corpus = DemoCorpusCatalog.Load(DemoCorpusCatalog.BuiltIn, null, "/nonexistent");
        var service = new DemoSearchService(corpus, WordSegmentation.UnicodeWordBoundaries);

        var comparison = service.Compare("\"refresh token\"", limit: 5);

        Assert.NotEmpty(comparison.Lanes);
    }

    [Fact]
    public void EvaluateSearch_ReturnsAllLanesWithNdcg()
    {
        var corpus = DemoCorpusCatalog.Load(DemoCorpusCatalog.BuiltIn, null, "/nonexistent");
        var service = new DemoSearchService(corpus, WordSegmentation.UnicodeWordBoundaries);

        var metrics = service.EvaluateSearch();

        Assert.NotEmpty(metrics);
        Assert.All(metrics, m => Assert.InRange(m.NdcgAt5, 0.0, 1.0));
    }

    [Fact]
    public void SegmentLane_MatchesLexicalLane_OnANewLaneQuery()
    {
        var corpus = DemoCorpusCatalog.Load(DemoCorpusCatalog.BuiltIn, null, "/nonexistent");
        var service = new DemoSearchService(corpus, WordSegmentation.UnicodeWordBoundaries);

        var comparison = service.Compare("inverted index", limit: 5);
        var segment = comparison.Lanes.Single(l => l.Key == "segment");
        var lexical = comparison.Lanes.Single(l => l.Key == "lexical");

        Assert.Equal(lexical.Hits.Count, segment.Hits.Count);
    }
}