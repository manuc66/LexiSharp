using LexiSharp.Core;
using LexiSharp.Expansion;
using LexiSharp.Indexing;
using LexiSharp.Linguistics;
using LexiSharp.Ranking;
using Xunit;

namespace LexiSharp.Tests;

public class ExpandingTextSearchEngineTests
{
    private static ExpandedTerm[] E(params string[] terms) =>
        terms.Select(term => new ExpandedTerm(term, 1)).ToArray();

    private sealed class RecordingEngine : ITextSearchEngine
    {
        public string? LastQuery { get; private set; }

        public void Index(IEnumerable<SearchDocument> documents) { }

        public void Add(SearchDocument document) { }

        public void Remove(string documentId) { }

        public void Clear() { }

        public IReadOnlyList<SearchResult> Search(string query, SearchOptions? options = null)
        {
            LastQuery = query;
            return Array.Empty<SearchResult>();
        }
    }

    private sealed class RecordingDetailedEngine : ITextSearchEngine, IDetailedSearchEngine
    {
        public string? LastQuery { get; private set; }

        public void Index(IEnumerable<SearchDocument> documents) { }

        public void Add(SearchDocument document) { }

        public void Remove(string documentId) { }

        public void Clear() { }

        public IReadOnlyList<SearchResult> Search(string query, SearchOptions? options = null) =>
            Array.Empty<SearchResult>();

        public IReadOnlyList<DetailedSearchResult> SearchWithDetails(string query, SearchOptions? options = null)
        {
            LastQuery = query;
            return [new DetailedSearchResult("doc", 5, new SearchDocument("doc", "gamma"), new Dictionary<string, double>())];
        }
    }

    private sealed class RecordingExplainEngine : ITextSearchEngine, IExplainableSearchEngine
    {
        public string? LastQuery { get; private set; }

        public void Index(IEnumerable<SearchDocument> documents) { }

        public void Add(SearchDocument document) { }

        public void Remove(string documentId) { }

        public void Clear() { }

        public IReadOnlyList<SearchResult> Search(string query, SearchOptions? options = null) =>
            Array.Empty<SearchResult>();

        public ScoreExplanation? Explain(string documentId, string query)
        {
            LastQuery = query;
            return new ScoreExplanation(
                documentId, "stub", 5, 3, 3, 1, 1,
                Array.Empty<TermContribution>(), new Dictionary<string, double>());
        }
    }

    private sealed class RecordingProbeEngine : ITextSearchEngine, IQueryCostProbe
    {
        public string? LastQuery { get; private set; }

        public void Index(IEnumerable<SearchDocument> documents) { }

        public void Add(SearchDocument document) { }

        public void Remove(string documentId) { }

        public void Clear() { }

        public IReadOnlyList<SearchResult> Search(string query, SearchOptions? options = null)
        {
            LastQuery = query;
            return Array.Empty<SearchResult>();
        }

        public long EstimateCandidateCount(ReadOnlySpan<char> query, SearchOptions options)
        {
            LastQuery = query.ToString();
            return 42;
        }
    }

    private sealed class MapExpander : ITermExpander
    {
        private readonly Dictionary<string, ExpandedTerm[]> _map;

        public MapExpander(Dictionary<string, ExpandedTerm[]> map) => _map = map;

        public int Calls { get; private set; }

        public IReadOnlyCollection<ExpandedTerm> Expand(IReadOnlyList<string> terms)
        {
            Calls++;
            var result = new List<ExpandedTerm>();

            foreach (var term in terms)
            {
                if (_map.TryGetValue(term, out var expansion))
                    result.AddRange(expansion);
            }

            return result;
        }
    }

    [Fact]
    public void Search_PassesExpandedQueryToInner()
    {
        var inner = new RecordingEngine();
        var expander = new MapExpander(new Dictionary<string, ExpandedTerm[]> { ["alpha"] = E("gamma") });
        var engine = new ExpandingTextSearchEngine(inner, expander);

        engine.Search("alpha beta");

        Assert.Equal("alpha beta gamma", inner.LastQuery);
    }

    [Fact]
    public void Search_DedupesTermsAlreadyInTheQuery()
    {
        var inner = new RecordingEngine();
        var expander = new MapExpander(new Dictionary<string, ExpandedTerm[]> { ["alpha"] = E("beta", "gamma") });
        var engine = new ExpandingTextSearchEngine(inner, expander);

        engine.Search("alpha beta");

        Assert.Equal("alpha beta gamma", inner.LastQuery);
    }

    [Fact]
    public void Search_SkipsEmptyAndDuplicateExpansionTerms()
    {
        var inner = new RecordingEngine();
        var expander = new MapExpander(new Dictionary<string, ExpandedTerm[]> { ["alpha"] = E("", "gamma", "gamma") });
        var engine = new ExpandingTextSearchEngine(inner, expander);

        engine.Search("alpha");

        Assert.Equal("alpha gamma", inner.LastQuery);
    }

    [Fact]
    public void Search_NoExpansion_DelegatesRaw()
    {
        var inner = new RecordingEngine();
        var expander = new MapExpander(new Dictionary<string, ExpandedTerm[]>());
        var engine = new ExpandingTextSearchEngine(inner, expander);

        engine.Search("alpha beta");

        Assert.Equal("alpha beta", inner.LastQuery);
        Assert.Equal(1, expander.Calls);
    }

    [Theory]
    [InlineData("alpha*")]
    [InlineData("alpha~")]
    [InlineData("\"alpha beta\"")]
    public void Search_SyntaxQuery_IsNotExpanded(string query)
    {
        var inner = new RecordingEngine();
        var expander = new MapExpander(new Dictionary<string, ExpandedTerm[]> { ["alpha"] = E("gamma") });
        var engine = new ExpandingTextSearchEngine(inner, expander);

        engine.Search(query);

        Assert.Equal(query, inner.LastQuery);
        Assert.Equal(0, expander.Calls);
    }

    [Fact]
    public void Search_EmptyQuery_IsNotExpanded()
    {
        var inner = new RecordingEngine();
        var expander = new MapExpander(new Dictionary<string, ExpandedTerm[]> { ["alpha"] = E("gamma") });
        var engine = new ExpandingTextSearchEngine(inner, expander);

        engine.Search("   ");

        Assert.Equal(0, expander.Calls);
    }

    [Fact]
    public void Constructor_NullArguments_Throw()
    {
        var inner = new RecordingEngine();
        var expander = new MapExpander(new Dictionary<string, ExpandedTerm[]>());

        Assert.Throws<ArgumentNullException>(() => new ExpandingTextSearchEngine(null!, expander));
        Assert.Throws<ArgumentNullException>(() => new ExpandingTextSearchEngine(inner, null!));
    }

    [Fact]
    public void Search_NullQuery_Throws()
    {
        var engine = new ExpandingTextSearchEngine(new RecordingEngine(), new MapExpander(new Dictionary<string, ExpandedTerm[]>()));
        Assert.Throws<ArgumentNullException>(() => engine.Search(null!));
    }

    [Fact]
    public void Search_ReachesDocumentsBeyondLiteralTerms()
    {
        var tokenizer = Tokenizer.Default;
        var index = new InMemoryTextIndex(tokenizer);
        index.Index(new[]
        {
            new SearchDocument("gamma-doc", "gamma"),
            new SearchDocument("unrelated", "delta epsilon"),
        });

        var literal = new RankedTextSearchEngine(index, new Bm25Scorer(), tokenizer);
        Assert.Empty(literal.Search("alpha"));

        var expander = new MapExpander(new Dictionary<string, ExpandedTerm[]> { ["alpha"] = E("gamma") });
        var expanding = new ExpandingTextSearchEngine(literal, expander, tokenizer);

        var results = expanding.Search("alpha");

        Assert.Contains(results, result => result.DocumentId == "gamma-doc");
    }

    [Fact]
    public void Writes_ForwardToInner()
    {
        var index = new InMemoryTextIndex();
        var inner = new RankedTextSearchEngine(index, new Bm25Scorer());
        var engine = new ExpandingTextSearchEngine(inner, new MapExpander(new Dictionary<string, ExpandedTerm[]>()));

        engine.Index(new[] { new SearchDocument("a", "alpha"), new SearchDocument("b", "beta") });
        Assert.Equal(2, index.Count);

        engine.Add(new SearchDocument("c", "gamma"));
        Assert.Equal(3, index.Count);

        engine.Remove("a");
        Assert.Equal(2, index.Count);

        engine.Clear();
        Assert.Equal(0, index.Count);
    }

    [Fact]
    public void SearchWithFacets_ForwardsTheExpandedQueryAndItsMatches()
    {
        var index = new InMemoryTextIndex();
        index.Index(new[]
        {
            new SearchDocument("gamma-doc", "gamma", Fields: new Dictionary<string, string> { ["kind"] = "g" }),
            new SearchDocument("unrelated", "delta epsilon", Fields: new Dictionary<string, string> { ["kind"] = "u" }),
        });
        var inner = new RankedTextSearchEngine(index, new Bm25Scorer());
        Assert.IsAssignableFrom<IFacetedSearchEngine>(inner);

        var expander = new MapExpander(new Dictionary<string, ExpandedTerm[]> { ["alpha"] = E("gamma") });
        var engine = new ExpandingTextSearchEngine(inner, expander);

        var faceted = engine.SearchWithFacets("alpha", facetFields: ["kind"]);

        // The facet counts the *expanded* matches: only gamma-doc, so only the "g" bucket exists.
        Assert.Contains(faceted.Results, result => result.DocumentId == "gamma-doc");
        Assert.DoesNotContain(faceted.Results, result => result.DocumentId == "unrelated");
        var bucket = Assert.Single(faceted.Buckets);
        Assert.Equal("kind", bucket.Field);
        var value = Assert.Single(bucket.Values);
        Assert.Equal("g", value.Value);
        Assert.Equal(1, value.Count);
    }

    [Fact]
    public void SearchWithFacets_InnerWithoutCapability_ThrowsNamingTheWrappedType()
    {
        var engine = new ExpandingTextSearchEngine(new RecordingEngine(), new MapExpander(new Dictionary<string, ExpandedTerm[]>()));
        var exception = Assert.Throws<NotSupportedException>(
            () => engine.SearchWithFacets("alpha", facetFields: ["kind"]));
        Assert.Contains(nameof(RecordingEngine), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SearchWithDetails_ForwardsTheExpandedQuery()
    {
        var inner = new RecordingDetailedEngine();
        var expander = new MapExpander(new Dictionary<string, ExpandedTerm[]> { ["alpha"] = E("gamma") });
        var engine = new ExpandingTextSearchEngine(inner, expander);

        var details = engine.SearchWithDetails("alpha");

        Assert.Equal("alpha gamma", inner.LastQuery);
        Assert.Equal("doc", Assert.Single(details).DocumentId);
    }

    [Fact]
    public void SearchWithDetails_InnerWithoutCapability_ThrowsNamingTheWrappedType()
    {
        var engine = new ExpandingTextSearchEngine(new RecordingEngine(), new MapExpander(new Dictionary<string, ExpandedTerm[]>()));
        var exception = Assert.Throws<NotSupportedException>(() => engine.SearchWithDetails("alpha"));
        Assert.Contains(nameof(RecordingEngine), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Explain_ForwardsTheExpandedQuery()
    {
        var inner = new RecordingExplainEngine();
        var expander = new MapExpander(new Dictionary<string, ExpandedTerm[]> { ["alpha"] = E("gamma") });
        var engine = new ExpandingTextSearchEngine(inner, expander);

        var explanation = engine.Explain("doc", "alpha");

        Assert.Equal("alpha gamma", inner.LastQuery);
        Assert.NotNull(explanation);
        Assert.Equal("doc", explanation.DocumentId);
    }

    [Fact]
    public void Explain_InnerWithoutCapability_ThrowsNamingTheWrappedType()
    {
        var engine = new ExpandingTextSearchEngine(new RecordingEngine(), new MapExpander(new Dictionary<string, ExpandedTerm[]>()));
        var exception = Assert.Throws<NotSupportedException>(() => engine.Explain("doc", "alpha"));
        Assert.Contains(nameof(RecordingEngine), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EstimateCandidateCount_DelegatesToInnerProbeWithExpandedQuery()
    {
        var inner = new RecordingProbeEngine();
        var expander = new MapExpander(new Dictionary<string, ExpandedTerm[]> { ["alpha"] = E("gamma") });
        var engine = new ExpandingTextSearchEngine(inner, expander);

        long estimate = engine.EstimateCandidateCount("alpha", SearchOptions.Default);

        Assert.Equal(42, estimate);
        Assert.Equal("alpha gamma", inner.LastQuery);
    }

    [Fact]
    public void EstimateCandidateCount_NonProbeInner_ReturnsMaxValue()
    {
        var engine = new ExpandingTextSearchEngine(new RecordingEngine(), new MapExpander(new Dictionary<string, ExpandedTerm[]>()));
        Assert.Equal(long.MaxValue, engine.EstimateCandidateCount("alpha", SearchOptions.Default));
    }

    [Fact]
    public void EstimateCandidateCount_NullOptions_Throws()
    {
        var engine = new ExpandingTextSearchEngine(new RecordingEngine(), new MapExpander(new Dictionary<string, ExpandedTerm[]>()));
        Assert.Throws<ArgumentNullException>(() => engine.EstimateCandidateCount("alpha", null!));
    }

    [Fact]
    public void Index_NullDocuments_Throws()
    {
        var engine = new ExpandingTextSearchEngine(new RecordingEngine(), new MapExpander(new Dictionary<string, ExpandedTerm[]>()));
        Assert.Throws<ArgumentNullException>(() => engine.Index(null!));
    }

    [Fact]
    public void Add_NullDocument_Throws()
    {
        var engine = new ExpandingTextSearchEngine(new RecordingEngine(), new MapExpander(new Dictionary<string, ExpandedTerm[]>()));
        Assert.Throws<ArgumentNullException>(() => engine.Add(null!));
    }
}
