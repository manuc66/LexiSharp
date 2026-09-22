using LexiSharp.Core;
using LexiSharp.Hybrid;
using LexiSharp.Indexing;
using LexiSharp.Ranking;
using Xunit;

namespace LexiSharp.Tests;

public class HybridTextSearchEngineTests
{
    private static ITextSearchEngine MemoryEngine(IEnumerable<SearchDocument> docs, ITextScorer? scorer = null)
    {
        var index = new InMemoryTextIndex();
        index.Index(docs);
        return new RankedTextSearchEngine(index, scorer ?? new Bm25Scorer());
    }

    private static SearchDocument Doc(string id, string text, string? category = null) =>
        new(id, text, null, category);

    [Fact]
    public void Search_MergesAndDeduplicatesAcrossEngines()
    {
        var hot = MemoryEngine(new[]
        {
            Doc("a", "red apple recipe"),
            Doc("b", "green apple smoothie"),
        });

        var cold = MemoryEngine(new[]
        {
            Doc("a", "red apple recipe"),  // same id as the hot engine
            Doc("c", "apple pie filling"),
        });

        var hybrid = new HybridTextSearchEngine(new[] { hot, cold });

        var results = hybrid.Search("apple");

        Assert.Equal(3, results.Count);
        Assert.Single(results, r => r.DocumentId == "a");
        Assert.Contains(results, r => r.DocumentId == "b");
        Assert.Contains(results, r => r.DocumentId == "c");
    }

    [Fact]
    public void Search_ReRanksOnTheUnion()
    {
        // Both engines rank their local result first; together, the union's best BM25 hit
        // is "c" (short doc with the only occurrence of the rare term "quince").
        var engineA = MemoryEngine(new[] { Doc("a", "apple"), Doc("c", "quince quince quince") });
        var engineB = MemoryEngine(new[] { Doc("b", "apple apple apple apple apple") });

        var hybrid = new HybridTextSearchEngine(new[] { engineA, engineB });

        var results = hybrid.Search("apple quince");

        Assert.NotEmpty(results);
        Assert.Equal("c", results[0].DocumentId);
    }

    [Fact]
    public void Search_AppliesMinimumScoreAndLimitOnFinalRanking()
    {
        // The five-doc corpus grows the per-engine BM25 scale (idf ≈ 0.88) well above the
        // re-ranked scale over the 3-candidate union (idf ≈ 0.47), so a MinimumScore that
        // every engine trivially passes still cuts candidates at the hybrid level.
        var engine = MemoryEngine(new[]
        {
            Doc("a", "tennis racket"),
            Doc("b", "tennis"),
            Doc("c", "racket"),
            Doc("d", "poker casino betting"),
            Doc("e", "mountain hiking"),
        });

        var hybrid = new HybridTextSearchEngine(new[] { engine });

        var limited = hybrid.Search("tennis", new SearchOptions(Limit: 1));
        Assert.Single(limited);
        Assert.Equal("b", limited[0].DocumentId);

        var unpruned = hybrid.Search("tennis racket");
        Assert.Equal(3, unpruned.Count);

        var pruned = hybrid.Search("tennis racket", new SearchOptions(Limit: 10, MinimumScore: 0.6));
        Assert.Single(pruned);
        Assert.Equal("a", pruned[0].DocumentId);
    }

    [Fact]
    public void Search_EmptyQueryOrEmptyEnginesYieldsNothing()
    {
        var engine = MemoryEngine(new[] { Doc("a", "apple") });

        var hybrid = new HybridTextSearchEngine(new[] { engine });
        Assert.Empty(hybrid.Search("   "));

        var nothing = new HybridTextSearchEngine(new[]
        {
            MemoryEngine(new[] { Doc("a", "apple") }),
        });
        Assert.Empty(nothing.Search("missing term"));
    }

    [Fact]
    public void Writes_FanOutToEveryEngine()
    {
        var hot = MemoryEngine(Array.Empty<SearchDocument>());
        var cold = MemoryEngine(Array.Empty<SearchDocument>());

        var hybrid = new HybridTextSearchEngine(new[] { hot, cold });

        hybrid.Add(Doc("x", "banana smoothie"));
        Assert.Single(hybrid.Search("banana"));
        Assert.Single(hybrid.Search("banana", new SearchOptions(Limit: 50)));

        hybrid.Remove("x");
        Assert.Empty(hybrid.Search("banana"));
    }

    [Fact]
    public void Ctor_RejectsEmptyOrDuplicateEngines()
    {
        var engine = MemoryEngine(Array.Empty<SearchDocument>());

        Assert.Throws<ArgumentException>(() => new HybridTextSearchEngine(Array.Empty<ITextSearchEngine>()));
        Assert.Throws<ArgumentException>(() => new HybridTextSearchEngine(new[] { engine, engine }));
    }

    [Fact]
    public void WeightedMerger_HonorsEngineWeights()
    {
        // Engine A boosts "a", engine B boosts "b"; with weights [1, 0], A decides.
        var engineA = MemoryEngine(new[]
        {
            Doc("a", "alpha beta"),
            Doc("b", "beta"),
        }, new TfIdfScorer());

        var engineB = MemoryEngine(new[]
        {
            Doc("a", "beta"),
            Doc("b", "alpha beta beta beta"),
        }, new TfIdfScorer());

        var preferA = new HybridTextSearchEngine(
            new[] { engineA, engineB },
            new WeightedScoreResultMerger(1.0, 0.0));

        var results = preferA.Search("alpha beta", new SearchOptions(Limit: 10));

        Assert.Equal("a", results[0].DocumentId);
    }

    [Fact]
    public void SearchWithDetails_ExposesPerSourceContributions()
    {
        var lexical = MemoryEngine(new[] { Doc("a", "apple pie"), Doc("b", "apple crumble") });
        var semantic = MemoryEngine(new[] { Doc("b", "apple crumble"), Doc("c", "apple juice") });

        var hybrid = new HybridTextSearchEngine(
            new[] { lexical, semantic },
            sourceNames: new[] { "lexical", "semantic" });

        var details = hybrid.SearchWithDetails("apple");

        Assert.Equal(3, details.Count);

        var docA = details.Single(d => d.DocumentId == "a");
        Assert.Equal(new[] { "lexical" }, docA.Contributions.Keys.ToArray());

        var docB = details.Single(d => d.DocumentId == "b");
        Assert.Equal(new[] { "lexical", "semantic" }, docB.Contributions.Keys.OrderBy(x => x).ToArray());

        var docC = details.Single(d => d.DocumentId == "c");
        Assert.Equal(new[] { "semantic" }, docC.Contributions.Keys.ToArray());

        // Contributions carry the raw per-engine scores.
        Assert.All(details, d => Assert.All(d.Contributions.Values, v => Assert.True(v > 0)));
    }

    [Fact]
    public void SearchWithDetails_DefaultSourceNames_AreEngineIndexed()
    {
        var engine = MemoryEngine(new[] { Doc("a", "apple") });

        var hybrid = new HybridTextSearchEngine(new[] { engine });

        var details = hybrid.SearchWithDetails("apple");

        var contribution = Assert.Single(details);
        Assert.Equal(new[] { "engine-0" }, contribution.Contributions.Keys.ToArray());
    }

    [Fact]
    public void SearchWithDetails_ToSearchResult_DropsContributions()
    {
        var engine = MemoryEngine(new[] { Doc("a", "apple") });

        var hybrid = new HybridTextSearchEngine(new[] { engine });

        var plain = hybrid.SearchWithDetails("apple")
            .Select(d => d.ToSearchResult())
            .ToList();

        var result = Assert.Single(plain);
        Assert.Equal("a", result.DocumentId);
        Assert.NotNull(result.Document);
    }

    [Fact]
    public void Ctor_RejectsInvalidSourceNames()
    {
        var engine = MemoryEngine(new[] { Doc("a", "apple") });

        Assert.Throws<ArgumentException>(() => new HybridTextSearchEngine(
            new[] { engine }, sourceNames: Array.Empty<string>()));
        Assert.Throws<ArgumentException>(() => new HybridTextSearchEngine(
            new[] { engine }, sourceNames: new[] { "dup", "dup" }));
        Assert.Throws<ArgumentException>(() => new HybridTextSearchEngine(
            new[] { engine }, sourceNames: new[] { " " }));
    }
}