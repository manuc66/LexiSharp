using LexiSharp;
using LexiSharp.Core;
using LexiSharp.Indexing;
using LexiSharp.Linguistics;
using LexiSharp.Ranking;
using Xunit;

namespace LexiSharp.Tests;

public class RankedTextSearchEngineTests
{
    private static RankedTextSearchEngine CreateEngine(
        IEnumerable<SearchDocument> docs,
        ITextScorer? scorer = null)
    {
        var engine = new RankedTextSearchEngine(
            new InMemoryTextIndex(),
            scorer ?? new Bm25Scorer());
        engine.Index(docs);
        return engine;
    }

    private static readonly SearchDocument[] Docs =
    {
        new("1", "The search engine uses BM25 to rank the results"),
        new("2", "TF-IDF is a classic method of textual search"),
        new("3", "Italian cuisine is renowned in Rome"),
    };

    private static readonly string[] ABIds = new[] { "a", "b" };

    [Fact]
    public void Search_ReturnsRelevantDocumentsFirst()
    {
        var engine = CreateEngine(Docs);

        var results = engine.Search("textual search", new SearchOptions(Limit: 10));

        Assert.Equal(2, results.Count);
        Assert.True(results[0].Score > results[1].Score);
    }

    [Fact]
    public void Search_RespectsLimit()
    {
        var engine = CreateEngine(Docs);

        Assert.Single(engine.Search("search", new SearchOptions(Limit: 1)));
        Assert.Empty(engine.Search("search", new SearchOptions(Limit: 0)));
    }

    [Fact]
    public void Search_RespectsMinimumScore()
    {
        var engine = CreateEngine(Docs);

        var results = engine.Search("search", new SearchOptions(Limit: 10, MinimumScore: double.MaxValue));

        Assert.Empty(results);
    }

    [Fact]
    public void Search_BreaksScoreTiesByCorpusOrderAtTheLimit()
    {
        // All four documents match the boolean query with the exact same score (1).
        // The stable descending sort keeps the earliest-enumerated ones when the limit
        // cuts into a run of equal scores.
        var engine = CreateEngine(
            new[]
            {
                new SearchDocument("a", "shared alpha"),
                new SearchDocument("b", "shared beta"),
                new SearchDocument("g", "shared gamma"),
                new SearchDocument("d", "shared delta"),
            },
            new BooleanScorer(BooleanMatch.AnyTerm));

        var results = engine.Search("shared", new SearchOptions(Limit: 2));

        Assert.Equal(2, results.Count);
        Assert.Equal(1.0, results[0].Score);
        Assert.Equal(1.0, results[1].Score);
        Assert.Equal(ABIds, results.Select(r => r.DocumentId));
    }

    [Fact]
    public void Search_WithOnlyStopWords_ReturnsNothing()
    {
        var tokenizer = new Tokenizer(new TokenizerOptions { RemoveStopWords = true });
        var engine = new RankedTextSearchEngine(
            new InMemoryTextIndex(tokenizer),
            new Bm25Scorer(),
            tokenizer);
        engine.Index(Docs);

        Assert.Empty(engine.Search("the and of"));
    }

    [Fact]
    public void Search_NormalizesAccentsInQueryAndDocuments()
    {
        var engine = CreateEngine(Docs);
        var results = engine.Search("cuisine Rome");

        Assert.Single(results);
        Assert.Equal("3", results[0].DocumentId);
    }

    [Fact]
    public void Add_Remove_AreReflectedInSearch()
    {
        var engine = CreateEngine(Docs);

        engine.Remove("1");

        Assert.DoesNotContain(engine.Search("engine").Select(r => r.DocumentId), id => id == "1");

        engine.Add(new SearchDocument("4", "a powerful electric engine"));

        Assert.Contains(engine.Search("engine").Select(r => r.DocumentId), id => id == "4");
    }

    [Fact]
    public void Clear_DropsEverything()
    {
        var engine = CreateEngine(Docs);

        engine.Clear();

        Assert.Empty(engine.Search("search"));
    }

    [Fact]
    public void Search_WorksWithSwappedScorers()
    {
        var scaffolder = new Func<ITextScorer, ITextSearchEngine>(scorer =>
            CreateEngine(Docs, scorer));

        var engines = new (ITextSearchEngine Engine, string Name)[]
        {
            (scaffolder(new Bm25Scorer()), "bm25"),
            (scaffolder(new TfIdfScorer()), "tfidf"),
            (scaffolder(new BooleanScorer(BooleanMatch.AnyTerm)), "bool"),
            (scaffolder(new QueryLikelihoodScorer()), "ql"),
        };

        foreach (var (engine, _) in engines)
        {
            var results = engine.Search("textual search");
            Assert.Equal(2, results.Count);
        }
    }

    [Fact]
    public void Search_EngineAndIndexShareTokenizerConfiguration()
    {
        var tokenizer = new Tokenizer(new TokenizerOptions { Stemmer = new SuffixStrippingStemmer() });
        var engine = new RankedTextSearchEngine(
            new InMemoryTextIndex(tokenizer),
            new Bm25Scorer(),
            tokenizer);

        engine.Add(new SearchDocument("1", "searching searching searching"));

        var results = engine.Search("search");

        Assert.Single(results);
    }
}