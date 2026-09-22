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

    // Same term frequency, growing length: BM25's length normalization gives four distinct
    // scores, so the ranking (and its pages) is unambiguous.
    private static readonly SearchDocument[] PaginationDocs =
    {
        new("p1", "common filler filler filler"),
        new("p2", "common filler filler"),
        new("p3", "common filler"),
        new("p4", "common"),
    };

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
    public void Search_Offset_SkipsTheTopOfTheRanking()
    {
        var engine = CreateEngine(PaginationDocs);

        var all = engine.Search("common", new SearchOptions(Limit: 10));
        Assert.Equal(4, all.Count);

        var page = engine.Search("common", new SearchOptions(Limit: 2, Offset: 2));

        Assert.Equal(
            all.Skip(2).Take(2).Select(r => r.DocumentId),
            page.Select(r => r.DocumentId));
        Assert.Equal(
            all.Skip(2).Take(2).Select(r => r.Score),
            page.Select(r => r.Score));
    }

    [Fact]
    public void Search_OffsetPages_PartitionTheFullRanking()
    {
        var engine = CreateEngine(PaginationDocs);

        var all = engine.Search("common", new SearchOptions(Limit: 10));
        Assert.Equal(4, all.Count);

        var page1 = engine.Search("common", new SearchOptions(Limit: 2, Offset: 0));
        var page2 = engine.Search("common", new SearchOptions(Limit: 2, Offset: 2));
        var page3 = engine.Search("common", new SearchOptions(Limit: 2, Offset: 4));

        Assert.Equal(
            all.Select(r => r.DocumentId),
            page1.Concat(page2).Concat(page3).Select(r => r.DocumentId));
        Assert.Equal(2, page1.Count);
        Assert.Equal(2, page2.Count);
        Assert.Empty(page3);

        Assert.Empty(engine.Search("common", new SearchOptions(Limit: 2, Offset: 42)));
        Assert.Empty(engine.Search("common", new SearchOptions(Limit: 2, Offset: -1)));
    }

    [Fact]
    public void Search_OffsetAppliesAfterMinimumScoreFiltering()
    {
        var engine = CreateEngine(PaginationDocs);

        var all = engine.Search("common", new SearchOptions(Limit: 10));
        double threshold = all[1].Score;
        var filtered = all.Where(r => r.Score >= threshold).ToList();
        Assert.True(filtered.Count >= 2);

        // The page is cut from the *filtered* ranking: without the threshold the skip would
        // land on a different document.
        var page = engine.Search("common", new SearchOptions(
            Limit: 10, MinimumScore: threshold, Offset: 1));

        Assert.Equal(
            filtered.Skip(1).Select(r => r.DocumentId),
            page.Select(r => r.DocumentId));
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

    private static readonly SearchDocument[] PhraseDocs =
    {
        new("adjacent", "machine learning systems"),
        new("reversed", "learning machine systems"),
        new("gapped", "machine that learning systems"),
        new("other", "completely unrelated text"),
    };

    [Fact]
    public void Search_PhraseQuery_RequiresConsecutivePositions()
    {
        var engine = CreateEngine(PhraseDocs);

        var results = engine.Search("\"machine learning\"", new SearchOptions(Limit: 10));

        var hit = Assert.Single(results);
        Assert.Equal("adjacent", hit.DocumentId);
        Assert.True(hit.Score > 0);
    }

    [Fact]
    public void Search_MixedQuery_PhraseGatesCorpusButFreeTermsOnlyScore()
    {
        var engine = CreateEngine(new[]
        {
            new SearchDocument("phrase-only", "machine learning systems rock"),
            new SearchDocument("both", "neural machine learning systems"),
            new SearchDocument("free-only", "neural networks accelerate fast"),
            new SearchDocument("reversed", "learning machine neural systems"),
        });

        var results = engine.Search("neural \"machine learning\"", new SearchOptions(Limit: 10));

        // The free term alone never pulls a document in, and never keeps one out: "phrase-only"
        // lacks "neural" yet matches through the phrase; "free-only" has "neural" but no phrase.
        Assert.Equal(2, results.Count);
        Assert.Contains(results, r => r.DocumentId == "phrase-only");
        Assert.Contains(results, r => r.DocumentId == "both");
        Assert.DoesNotContain(results, r => r.DocumentId == "free-only");
        Assert.DoesNotContain(results, r => r.DocumentId == "reversed");
    }

    [Fact]
    public void Search_MultiplePhrases_AreAnded()
    {
        var engine = CreateEngine(new[]
        {
            new SearchDocument("both", "deep learning and machine learning"),
            new SearchDocument("first-only", "deep learning beats statistics"),
            new SearchDocument("second-only", "machine learning beats statistics"),
        });

        var results = engine.Search("\"deep learning\" \"machine learning\"", new SearchOptions(Limit: 10));

        var hit = Assert.Single(results);
        Assert.Equal("both", hit.DocumentId);
    }

    [Fact]
    public void Search_SingleTokenPhrase_MatchesTermPresence()
    {
        var engine = CreateEngine(Docs);

        var results = engine.Search("\"search\"", new SearchOptions(Limit: 10));

        Assert.Equal(2, results.Count);
        Assert.DoesNotContain(results, r => r.DocumentId == "3");
    }

    [Fact]
    public void Search_OnlyVacuousPhrases_ReturnsNothing()
    {
        var engine = CreateEngine(Docs);

        Assert.Empty(engine.Search("\"\"", new SearchOptions(Limit: 10)));
        Assert.Empty(engine.Search("\"   \"", new SearchOptions(Limit: 10)));
    }
}