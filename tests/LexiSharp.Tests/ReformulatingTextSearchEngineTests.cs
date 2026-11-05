using LexiSharp.Core;
using LexiSharp.Expansion;
using LexiSharp.Indexing;
using LexiSharp.Linguistics;
using LexiSharp.Ranking;
using Xunit;

namespace LexiSharp.Tests;

public class ReformulatingTextSearchEngineTests
{
    private static SearchResult R(string id, double score) =>
        new(id, score, new SearchDocument(id, id));

    private static ITextSearchEngine StubEngine(Func<string, IReadOnlyList<SearchResult>> fn) =>
        new StubTextSearchEngine(fn);

    [Fact]
    public void BuildVariants_OriginalOnly_WhenNoCorrections()
    {
        var corrector = new PassthroughTermCorrector();
        var index = new InMemoryTextIndex(new Tokenizer());
        var inner = new RankedTextSearchEngine(index, new Bm25Scorer(), new Tokenizer());
        var engine = new ReformulatingTextSearchEngine(inner, corrector);

        var variants = engine.BuildVariants("hello world");
        Assert.Single(variants);
        Assert.Equal("hello world", variants[0]);
    }

    [Fact]
    public void BuildVariants_GeneratesVariantPerCorrectedTerm()
    {
        // A corrector that suggests "hello" for "helo"
        var vocab = new[] { "hello", "world" };
        var corrector = new DamerauLevenshteinTermCorrector(vocab, maxDistance: 2);
        var index = new InMemoryTextIndex(new Tokenizer());
        var inner = new RankedTextSearchEngine(index, new Bm25Scorer(), new Tokenizer());
        var engine = new ReformulatingTextSearchEngine(inner, corrector);

        var variants = engine.BuildVariants("helo world");

        // Original + variant with "helo" → "hello"
        Assert.True(variants.Count >= 2);
        Assert.Contains("helo world", variants);
        Assert.Contains("hello world", variants);
    }

    [Fact]
    public void Search_DelegatesToInner_WhenNoVariants()
    {
        var index = new InMemoryTextIndex(new Tokenizer());
        index.Index(new[] { new SearchDocument("1", "hello world") });
        var inner = new RankedTextSearchEngine(index, new Bm25Scorer(), new Tokenizer());
        var engine = new ReformulatingTextSearchEngine(inner, new PassthroughTermCorrector());

        var results = engine.Search("hello");
        Assert.Single(results);
        Assert.Equal("1", results[0].DocumentId);
    }

    [Fact]
    public void Search_FusesVariants_WhenCorrectionsExist()
    {
        var index = new InMemoryTextIndex(new Tokenizer());
        index.Index(new[]
        {
            new SearchDocument("1", "hello world"),
            new SearchDocument("2", "hallo world"),
        });
        var vocab = new[] { "hello", "hallo", "world" };
        var corrector = new DamerauLevenshteinTermCorrector(vocab, maxDistance: 2);
        var inner = new RankedTextSearchEngine(index, new Bm25Scorer(), new Tokenizer());
        var engine = new ReformulatingTextSearchEngine(inner, corrector);

        // "helo" should generate variants "hello" and "hallo"
        var results = engine.Search("helo world");
        Assert.NotEmpty(results);
    }

    [Fact]
    public void BuildVariants_DeduplicatesIdenticalVariants()
    {
        var vocab = new[] { "hello" };
        var corrector = new DamerauLevenshteinTermCorrector(vocab, maxDistance: 2);
        var index = new InMemoryTextIndex(new Tokenizer());
        var inner = new RankedTextSearchEngine(index, new Bm25Scorer(), new Tokenizer());
        var engine = new ReformulatingTextSearchEngine(inner, corrector);

        var variants = engine.BuildVariants("hello");
        // "hello" is in vocab, no correction needed → only original
        Assert.Single(variants);
    }

    [Fact]
    public void Search_ForwardsWritesToInner()
    {
        var index = new InMemoryTextIndex(new Tokenizer());
        var inner = new RankedTextSearchEngine(index, new Bm25Scorer(), new Tokenizer());
        var engine = new ReformulatingTextSearchEngine(inner, new PassthroughTermCorrector());

        engine.Index(new[] { new SearchDocument("1", "test") });
        Assert.Single(engine.Search("test"));
    }
}

internal class StubTextSearchEngine : ITextSearchEngine
{
    private readonly Func<string, IReadOnlyList<SearchResult>> _fn;
    public StubTextSearchEngine(Func<string, IReadOnlyList<SearchResult>> fn) => _fn = fn;
    public void Index(IEnumerable<SearchDocument> documents) { }
    public void Add(SearchDocument document) { }
    public bool Remove(string documentId) => false;
    public void Clear() { }
    public IReadOnlyList<SearchResult> Search(string query, SearchOptions? options = null, CancellationToken cancellationToken = default) => _fn(query);
}