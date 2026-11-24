using LexiSharp.Core;
using LexiSharp.Indexing;
using LexiSharp.Ranking;
using Xunit;

namespace LexiSharp.Tests;

public class Bm25ScorerEdgeTests
{
    private static RankedTextSearchEngine Engine(params SearchDocument[] docs)
    {
        var index = new InMemoryTextIndex();
        foreach (var d in docs) index.Add(d);
        return new RankedTextSearchEngine(index, new Bm25Scorer());
    }

    [Fact]
    public void EmptyQuery_ReturnsNothing()
    {
        var engine = Engine(new SearchDocument("1", "hello world"));
        Assert.Empty(engine.Search(""));
    }

    [Fact]
    public void WhitespaceQuery_ReturnsNothing()
    {
        var engine = Engine(new SearchDocument("1", "hello world"));
        Assert.Empty(engine.Search("   "));
    }

    [Fact]
    public void UnknownTerm_ReturnsNothing()
    {
        var engine = Engine(new SearchDocument("1", "hello world"));
        Assert.Empty(engine.Search("zzzz"));
    }

    [Fact]
    public void ZeroLengthDocument_DoesNotProduceNaN()
    {
        var engine = Engine(new SearchDocument("1", ""));
        var results = engine.Search("hello");
        foreach (var r in results)
        {
            Assert.False(double.IsNaN(r.Score));
            Assert.False(double.IsInfinity(r.Score));
        }
    }

    [Fact]
    public void DegenerateParameters_DoNotCrash()
    {
        var index = new InMemoryTextIndex();
        index.Add(new SearchDocument("1", "hello world"));
        var engine = new RankedTextSearchEngine(index, new Bm25Scorer(new Bm25Parameters(0, 0)));
        var results = engine.Search("hello");
        foreach (var r in results)
        {
            Assert.False(double.IsNaN(r.Score));
            Assert.False(double.IsInfinity(r.Score));
        }
    }
}
