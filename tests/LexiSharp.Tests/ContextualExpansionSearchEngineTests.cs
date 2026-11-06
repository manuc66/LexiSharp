using LexiSharp.Core;
using LexiSharp.Expansion;
using LexiSharp.Hybrid;
using LexiSharp.Indexing;
using LexiSharp.Ranking;
using Xunit;

namespace LexiSharp.Tests;

public class ContextualExpansionSearchEngineTests
{
    [Fact]
    public void Search_RunsEndToEnd_WithTermOverlapScorer()
    {
        var index = new InMemoryTextIndex();
        var engine = new RankedTextSearchEngine(index, new LexiSharp.Ranking.Bm25Scorer());
        var docs = new[]
        {
            new SearchDocument("1", "Erik Hort was born in Montebello New York"),
            new SearchDocument("2", "Montebello is a village in Rockland County"),
            new SearchDocument("3", "Rockland County borders New Jersey"),
            new SearchDocument("4", "Unrelated document about cooking"),
        };
        engine.Index(docs);

        var multi = new ContextualExpansionSearchEngine(
            engine,
            new ContextualTermOverlapScorer(),
            index);

        var results = multi.Search("Erik Hort birthplace", new SearchOptions(Limit: 5));

        Assert.True(results.Count > 0);
        Assert.Contains(results, r => r.DocumentId == "1");
    }

    [Fact]
    public void Search_EmptyCorpus_ReturnsEmpty()
    {
        var index = new InMemoryTextIndex();
        var engine = new RankedTextSearchEngine(index, new LexiSharp.Ranking.Bm25Scorer());
        var multi = new ContextualExpansionSearchEngine(engine, new ContextualTermOverlapScorer(), index);
        var results = multi.Search("anything", new SearchOptions(Limit: 5));
        Assert.Empty(results);
    }

    [Fact]
    public void Search_H0_NoExpansion_StillScores()
    {
        var index = new InMemoryTextIndex();
        var engine = new RankedTextSearchEngine(index, new LexiSharp.Ranking.Bm25Scorer());
        engine.Index(new[] { new SearchDocument("1", "hello world"), new SearchDocument("2", "goodbye world") });
        var multi = new ContextualExpansionSearchEngine(engine, new ContextualTermOverlapScorer(), index, additionalRounds: 0);
        var results = multi.Search("hello", new SearchOptions(Limit: 5));
        Assert.NotEmpty(results);
    }
}