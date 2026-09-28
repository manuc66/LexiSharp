using LexiSharp.Core;
using LexiSharp.Indexing;
using LexiSharp.Linguistics;
using LexiSharp.Ranking;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// The span-first overloads must be byte-for-byte equivalent to their string counterparts.
/// </summary>
public class SpanFirstApiTests
{
    private static readonly string[] Samples =
    {
        "plain ascii text with punctuation, and some 1234567890 numbers!",
        "C'est déjà vu - l'été à Paris…",
        "hello 🚀 world 🌍 and 🐍",
        "привет мир как дела хорошо",
        "  leading and trailing  ",
        "中文测试与word混合mixed文本",
        "one\ttwo\nthree\r\nfour",
        "double  space  after   words",
    };

    private static readonly string[] Queries =
    {
        "neural network",
        "neural \"machine learning\"",
        "catt~2 net* \"deep learning\" vs",
        "\"\" \"!!!\" real",
        "foo ~bar a* bb",
        "",
        "   ",
        "terminé déjà",
    };

    [Fact]
    public void Tokenize_SpanOverload_MatchesStringOverload()
    {
        foreach (var options in OptionVariants())
        {
            var sut = new Tokenizer(options);

            foreach (var sample in Samples)
                Assert.Equal(sut.Tokenize(sample), sut.Tokenize(sample.AsSpan()));
        }
    }

    [Fact]
    public void TokenizeWithSpans_SpanOverload_MatchesStringOverload()
    {
        foreach (var options in OptionVariants())
        {
            var sut = new Tokenizer(options);

            foreach (var sample in Samples)
                Assert.Equal(sut.TokenizeWithSpans(sample), sut.TokenizeWithSpans(sample.AsSpan()));
        }
    }

    [Fact]
    public void Tokenize_InvalidSurrogates_ThrowLikeRuneGetRuneAt()
    {
        const string loneHigh = "\uD800";
        const string loneLow = "\uDC00";
        const string highThenAscii = "\uD800A";

        Assert.Throws<ArgumentException>(() => Tokenizer.Default.Tokenize(loneHigh.AsSpan()));
        Assert.Throws<ArgumentException>(() => Tokenizer.Default.Tokenize(loneLow.AsSpan()));
        Assert.Throws<ArgumentException>(() => Tokenizer.Default.Tokenize(highThenAscii.AsSpan()));

        // The string path keeps throwing the same way (Rune.GetRuneAt).
        Assert.Throws<ArgumentException>(() => Tokenizer.Default.Tokenize(loneHigh));
    }

    [Fact]
    public void Parse_SpanOverload_MatchesStringOverload()
    {
        foreach (var query in Queries)
        {
            var fromString = QueryParser.Parse(query, Tokenizer.Default);
            var fromSpan = QueryParser.Parse(query.AsSpan(), Tokenizer.Default);

            Assert.Equal(fromString.FreeTerms, fromSpan.FreeTerms);
            Assert.Equal(fromString.AllTerms, fromSpan.AllTerms);
            Assert.Equal(fromString.Phrases, fromSpan.Phrases);
            Assert.Equal(fromString.Expansions, fromSpan.Expansions);
        }
    }

    [Fact]
    public void SplitRaw_SpanOverload_MatchesStringOverload()
    {
        foreach (var query in Queries)
        {
            var fromString = QueryParser.SplitRaw(query);
            var fromSpan = QueryParser.SplitRaw(query.AsSpan());

            Assert.Equal(fromString.FreeText, fromSpan.FreeText);
            Assert.Equal(fromString.Phrases, fromSpan.Phrases);
        }
    }

    [Fact]
    public void Search_SpanOverload_MatchesStringOverload()
    {
        var engine = CreateEngine();

        foreach (var query in new[] { "fast car", "fast \"car review\"", "boat carr~1" })
        {
            var fromString = engine.Search(query, Page);
            var fromSpan = engine.Search(query.AsSpan(), Page);

            Assert.Equal(
                fromString.Select(r => (r.DocumentId, r.Score)),
                fromSpan.Select(r => (r.DocumentId, r.Score)));
        }
    }

    [Fact]
    public void SearchWithFacets_SpanOverload_MatchesStringOverload()
    {
        var engine = CreateEngine();

        var fromString = engine.SearchWithFacets("fast car", Page, ["kind", "lang"]);
        var fromSpan = engine.SearchWithFacets("fast car".AsSpan(), Page, ["kind", "lang"]);

        Assert.Equal(
            fromString.Results.Select(r => (r.DocumentId, r.Score)),
            fromSpan.Results.Select(r => (r.DocumentId, r.Score)));
        Assert.Equal(fromString.Buckets.Count, fromSpan.Buckets.Count);

        for (int i = 0; i < fromString.Buckets.Count; i++)
        {
            Assert.Equal(fromString.Buckets[i].Field, fromSpan.Buckets[i].Field);
            Assert.Equal(
                fromString.Buckets[i].Values.Select(v => (v.Value, v.Count)),
                fromSpan.Buckets[i].Values.Select(v => (v.Value, v.Count)));
        }
    }

    [Fact]
    public void Explain_SpanOverload_MatchesStringOverload()
    {
        var engine = CreateEngine();

        var fromString = engine.Explain("1", "fast car");
        var fromSpan = engine.Explain("1", "fast car".AsSpan());

        Assert.NotNull(fromString);
        Assert.NotNull(fromSpan);
        Assert.Equal(fromString!.TotalScore, fromSpan!.TotalScore);
        Assert.Equal(
            fromString.Terms.Select(c => (c.Term, c.Score)),
            fromSpan.Terms.Select(c => (c.Term, c.Score)));
    }

    [Fact]
    public void Search_NullString_StillBindsTheStringOverload()
    {
        var engine = CreateEngine();

        Assert.Throws<ArgumentNullException>(() => engine.Search((string)null!));
        Assert.Throws<ArgumentNullException>(() => engine.SearchWithFacets((string)null!));
        Assert.Throws<ArgumentNullException>(() => QueryParser.Parse((string)null!, Tokenizer.Default));
        Assert.Throws<ArgumentNullException>(() => QueryParser.SplitRaw((string)null!));
    }

    [Fact]
    public void InterfaceDefaultImplementations_ForwardSpanToTheStringOverload()
    {
        // A tokenizer that only implements the string overload must still answer the span call.
        ITokenizer tokenizer = new StringOnlyTokenizer();
        Assert.Equal(new[] { "hello", "world" }, tokenizer.Tokenize("hello world".AsSpan()));

        // Same for the search engine surface.
        ITextSearchEngine engine = new StringOnlyEngine();
        Assert.Equal(
            new[] { "1" },
            engine.Search("q".AsSpan()).Select(r => r.DocumentId));

        IFacetedSearchEngine faceted = new StringOnlyEngine();
        var result = faceted.SearchWithFacets("q".AsSpan(), null, ["kind"]);
        Assert.Equal("kind", Assert.Single(result.Buckets).Field);
    }

    private static IEnumerable<TokenizerOptions?> OptionVariants()
    {
        yield return null;
        yield return new TokenizerOptions { KeepSingleCharTerms = true };
        yield return new TokenizerOptions { NGramMax = 2 };
        yield return new TokenizerOptions { RemoveStopWords = true };
        yield return new TokenizerOptions { Stemmer = new SuffixStrippingStemmer() };
    }

    private static readonly SearchOptions Page = new(Limit: 10);

    private static RankedTextSearchEngine CreateEngine()
    {
        var engine = new RankedTextSearchEngine(new InMemoryTextIndex(), new Bm25Scorer());

        engine.Index(new[]
        {
            new SearchDocument("1", "fast car review",
                new Dictionary<string, string> { ["kind"] = "review", ["lang"] = "en" }),
            new SearchDocument("2", "fast boat review",
                new Dictionary<string, string> { ["kind"] = "review", ["lang"] = "fr" }),
            new SearchDocument("3", "slow car notes",
                new Dictionary<string, string> { ["kind"] = "notes", ["lang"] = "en" }),
        });

        return engine;
    }

    private sealed class StringOnlyTokenizer : ITokenizer
    {
        public IReadOnlyList<string> Tokenize(string text) =>
            text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }

    private sealed class StringOnlyEngine : IFacetedSearchEngine
    {
        public void Index(IEnumerable<SearchDocument> documents) => throw new NotSupportedException();

        public void Add(SearchDocument document) => throw new NotSupportedException();

        public void Remove(string documentId) => throw new NotSupportedException();

        public void Clear() => throw new NotSupportedException();

        public IReadOnlyList<SearchResult> Search(string query, SearchOptions? options = null) =>
            new[] { new SearchResult("1", 1, new SearchDocument("1", query)) };

        public FacetedSearchResult SearchWithFacets(
            string query,
            SearchOptions? options = null,
            IReadOnlyList<string>? facetFields = null) =>
            new(
                Search(query, options),
                new[] { new FacetBucket("kind", new[] { new FacetValue("x", 1) }) });
    }
}
