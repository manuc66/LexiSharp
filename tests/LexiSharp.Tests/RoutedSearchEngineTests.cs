using LexiSharp.Core;
using LexiSharp.Indexing;
using LexiSharp.Ranking;
using Xunit;

namespace LexiSharp.Tests;

public class RoutedSearchEngineTests
{
    private static RoutedEngine Routed(string name, long cost) => new(name, new ProbeEngine(name, cost));

    [Fact]
    public void Search_RoutesToTheCheapestProbedEngine()
    {
        var cheap = (ProbeEngine)Routed("cheap", 1).Engine;
        var expensive = (ProbeEngine)Routed("expensive", 100).Engine;

        var router = new RoutedSearchEngine(new[]
        {
            new RoutedEngine("expensive", expensive),
            new RoutedEngine("cheap", cheap),
        });

        var results = router.Search("q");

        Assert.Equal("cheap", Assert.Single(results).DocumentId);
        Assert.Equal("q", cheap.LastQuery);
        Assert.Null(expensive.LastQuery);
    }

    [Fact]
    public void Search_Tie_KeepsTheEarliestEngine()
    {
        var first = (ProbeEngine)Routed("first", 5).Engine;
        var second = (ProbeEngine)Routed("second", 5).Engine;

        var router = new RoutedSearchEngine(new[]
        {
            new RoutedEngine("first", first),
            new RoutedEngine("second", second),
        });

        Assert.Equal("first", Assert.Single(router.Search("q")).DocumentId);
    }

    [Fact]
    public void Search_IgnoresUnprobedEngines_WhenAProbeExists()
    {
        var plain = new PlainEngine("plain");
        var probed = (ProbeEngine)Routed("probed", 1000).Engine;

        var router = new RoutedSearchEngine(new[]
        {
            new RoutedEngine("plain", plain),
            new RoutedEngine("probed", probed),
        });

        Assert.Equal("probed", Assert.Single(router.Search("q")).DocumentId);
    }

    [Fact]
    public void Search_WithoutAnyProbe_FallsBackToTheFirstEngine()
    {
        var router = new RoutedSearchEngine(new[]
        {
            new RoutedEngine("first", new PlainEngine("first")),
            new RoutedEngine("second", new PlainEngine("second")),
        });

        Assert.Equal("first", Assert.Single(router.Search("q")).DocumentId);
    }

    [Fact]
    public void Search_PassesQueryAndOptionsToTheSelectedEngine()
    {
        var probe = (ProbeEngine)Routed("only", 0).Engine;
        var router = new RoutedSearchEngine(new[] { new RoutedEngine("only", probe) });
        var options = new SearchOptions(Limit: 3, Offset: 1);

        router.Search("hello world", options);

        Assert.Equal("hello world", probe.LastQuery);
        Assert.Same(options, probe.LastOptions);
    }

    [Fact]
    public void Search_NullOptions_PassesTheDefault()
    {
        var probe = (ProbeEngine)Routed("only", 0).Engine;
        var router = new RoutedSearchEngine(new[] { new RoutedEngine("only", probe) });

        router.Search("q".AsSpan());

        Assert.Same(SearchOptions.Default, probe.LastOptions);
    }

    [Fact]
    public void Search_NullString_Throws()
    {
        var router = new RoutedSearchEngine(new[] { Routed("only", 0) });

        Assert.Throws<ArgumentNullException>(() => router.Search((string)null!));
    }

    [Fact]
    public void Search_CustomEstimatorControlsTheChoice()
    {
        var probe = (ProbeEngine)Routed("only", 0).Engine;
        var router = new RoutedSearchEngine(
            new[]
            {
                new RoutedEngine("first", new PlainEngine("first")),
                new RoutedEngine("second", probe),
            },
            new AlwaysIndexEstimator(1));

        Assert.Equal("only", Assert.Single(router.Search("q")).DocumentId);
    }

    [Fact]
    public void Search_EstimatorReturningOutOfRangeIndex_Throws()
    {
        var router = new RoutedSearchEngine(
            new[] { Routed("only", 0) },
            new AlwaysIndexEstimator(7));

        Assert.Throws<InvalidOperationException>(() => router.Search("q"));
    }

    [Fact]
    public void Constructor_RejectsEmptyList()
    {
        Assert.Throws<ArgumentException>(() => new RoutedSearchEngine(Array.Empty<RoutedEngine>()));
    }

    [Fact]
    public void Constructor_RejectsNullNameAndNullEngine()
    {
        var engine = new PlainEngine("x");

        Assert.Throws<ArgumentException>(() => new RoutedSearchEngine(new[] { new RoutedEngine(null!, engine) }));
        Assert.Throws<ArgumentException>(() => new RoutedSearchEngine(new[] { new RoutedEngine("   ", engine) }));
        Assert.Throws<ArgumentException>(() => new RoutedSearchEngine(new[] { new RoutedEngine("x", null!) }));
    }

    [Fact]
    public void Writes_AreNotSupported()
    {
        var router = new RoutedSearchEngine(new[] { Routed("only", 0) });

        Assert.Throws<NotSupportedException>(() => router.Index(Array.Empty<SearchDocument>()));
        Assert.Throws<NotSupportedException>(() => router.Add(new SearchDocument("1", "text")));
        Assert.Throws<NotSupportedException>(() => router.Remove("1"));
        Assert.Throws<NotSupportedException>(() => router.Clear());
    }

    [Fact]
    public void RankedEngineProbe_SumsTheQueryTermsDocumentFrequencies()
    {
        var engine = new RankedTextSearchEngine(new InMemoryTextIndex(), new Bm25Scorer());
        engine.Index(new[]
        {
            new SearchDocument("1", "fast car review"),
            new SearchDocument("2", "fast boat"),
            new SearchDocument("3", "slow car"),
        });

        IQueryCostProbe probe = engine;
        var options = SearchOptions.Default;

        // "fast" (docs 1,2) + "car" (docs 1,3) = 4.
        Assert.Equal(4, probe.EstimateCandidateCount("fast car".AsSpan(), options));
        // A term nobody carries costs nothing.
        Assert.Equal(0, probe.EstimateCandidateCount("missing".AsSpan(), options));
        // An empty request costs nothing.
        Assert.Equal(0, probe.EstimateCandidateCount("fast".AsSpan(), new SearchOptions(Limit: 0)));
    }

    [Fact]
    public void RankedEngineProbe_EmptyIndexCostsZero()
    {
        var engine = new RankedTextSearchEngine(new InMemoryTextIndex(), new Bm25Scorer());

        IQueryCostProbe probe = engine;

        Assert.Equal(0, probe.EstimateCandidateCount("anything".AsSpan(), SearchOptions.Default));
    }

    [Fact]
    public void SearchWithFacets_RoutesToTheSelectedEngine()
    {
        var faceted = new FacetedEngine();
        var router = new RoutedSearchEngine(
            new[] { new RoutedEngine("plain", new PlainEngine("plain")), new RoutedEngine("faceted", faceted) },
            new AlwaysIndexEstimator(1));

        var result = router.SearchWithFacets("q", null, ["kind"]);

        Assert.Equal("kind", Assert.Single(result.Buckets).Field);
        Assert.Equal("q", faceted.LastFacetQuery);
    }

    [Fact]
    public void SearchWithFacets_SelectedEngineWithoutTheCapability_Throws()
    {
        var router = new RoutedSearchEngine(new[] { new RoutedEngine("plain", new PlainEngine("plain")) });

        Assert.Throws<NotSupportedException>(() => router.SearchWithFacets("q"));
    }

    [Fact]
    public void SearchWithDetails_RoutesToTheSelectedEngine()
    {
        var router = new RoutedSearchEngine(new[] { new RoutedEngine("detailed", new DetailedEngine()) });

        var results = router.SearchWithDetails("q");

        var hit = Assert.Single(results);
        Assert.Equal("detailed", hit.DocumentId);
        Assert.Equal(1, hit.Contributions["lexical"]);
    }

    [Fact]
    public void SearchWithDetails_SelectedEngineWithoutTheCapability_Throws()
    {
        var router = new RoutedSearchEngine(new[] { new RoutedEngine("plain", new PlainEngine("plain")) });

        Assert.Throws<NotSupportedException>(() => router.SearchWithDetails("q"));
    }

    [Fact]
    public void Explain_RoutesToTheSelectedEngine()
    {
        var router = new RoutedSearchEngine(new[] { new RoutedEngine("explainable", new ExplainableEngine()) });

        var explanation = router.Explain("doc", "q");

        Assert.NotNull(explanation);
        Assert.Equal("doc", explanation!.DocumentId);
    }

    [Fact]
    public void Explain_SelectedEngineWithoutTheCapability_Throws()
    {
        var router = new RoutedSearchEngine(new[] { new RoutedEngine("plain", new PlainEngine("plain")) });

        Assert.Throws<NotSupportedException>(() => router.Explain("doc", "q"));
    }

    [Fact]
    public void EstimateCandidateCount_IsTheSmallestAcrossProbedEngines()
    {
        var router = new RoutedSearchEngine(new[]
        {
            Routed("a", 10),
            Routed("b", 3),
            new RoutedEngine("plain", new PlainEngine("plain")),
        });

        Assert.Equal(3, router.EstimateCandidateCount("q".AsSpan(), SearchOptions.Default));
    }

    [Fact]
    public void EstimateCandidateCount_WithoutAnyProbedEngine_IsLastResort()
    {
        var router = new RoutedSearchEngine(new[] { new RoutedEngine("plain", new PlainEngine("plain")) });

        Assert.Equal(long.MaxValue, router.EstimateCandidateCount("q".AsSpan(), SearchOptions.Default));
    }

    [Fact]
    public void BoostedAndReranked_DelegateTheCostProbeToTheInner()
    {
        var inner = (ProbeEngine)Routed("inner", 7).Engine;

        IQueryCostProbe boosted = new BoostedTextSearchEngine(inner, _ => ScoreBoost.Factor(2));
        IQueryCostProbe reranked = new RerankedTextSearchEngine(inner, new NoopReranker());

        Assert.Equal(7, boosted.EstimateCandidateCount("q".AsSpan(), SearchOptions.Default));
        Assert.Equal(7, reranked.EstimateCandidateCount("q".AsSpan(), SearchOptions.Default));

        IQueryCostProbe unprobed = new BoostedTextSearchEngine(new PlainEngine("plain"), _ => ScoreBoost.Factor(2));
        Assert.Equal(long.MaxValue, unprobed.EstimateCandidateCount("q".AsSpan(), SearchOptions.Default));
    }

    private sealed class ProbeEngine : ITextSearchEngine, IQueryCostProbe
    {
        private readonly long _cost;

        public ProbeEngine(string tag, long cost)
        {
            Tag = tag;
            _cost = cost;
        }

        public string Tag { get; }

        public string? LastQuery { get; private set; }

        public SearchOptions? LastOptions { get; private set; }

        public IReadOnlyList<SearchResult> Search(string query, SearchOptions? options = null)
        {
            LastQuery = query;
            LastOptions = options;
            return new[] { new SearchResult(Tag, 1, new SearchDocument(Tag, query)) };
        }

        public long EstimateCandidateCount(ReadOnlySpan<char> query, SearchOptions options) => _cost;

        public void Index(IEnumerable<SearchDocument> documents) => throw new NotSupportedException();

        public void Add(SearchDocument document) => throw new NotSupportedException();

        public void Remove(string documentId) => throw new NotSupportedException();

        public void Clear() => throw new NotSupportedException();
    }

    private sealed class PlainEngine : ITextSearchEngine
    {
        private readonly string _tag;

        public PlainEngine(string tag) => _tag = tag;

        public IReadOnlyList<SearchResult> Search(string query, SearchOptions? options = null) =>
            new[] { new SearchResult(_tag, 1, new SearchDocument(_tag, query)) };

        public void Index(IEnumerable<SearchDocument> documents) => throw new NotSupportedException();

        public void Add(SearchDocument document) => throw new NotSupportedException();

        public void Remove(string documentId) => throw new NotSupportedException();

        public void Clear() => throw new NotSupportedException();
    }

    private sealed class AlwaysIndexEstimator : IQueryCostEstimator
    {
        private readonly int _index;

        public AlwaysIndexEstimator(int index) => _index = index;

        public int Select(IReadOnlyList<RoutedEngine> engines, ReadOnlySpan<char> query, SearchOptions options) =>
            _index;
    }

    private sealed class FacetedEngine : IFacetedSearchEngine
    {
        public string? LastFacetQuery { get; private set; }

        public IReadOnlyList<SearchResult> Search(string query, SearchOptions? options = null) =>
            new[] { new SearchResult("faceted", 1, new SearchDocument("faceted", query)) };

        public FacetedSearchResult SearchWithFacets(
            string query,
            SearchOptions? options = null,
            IReadOnlyList<string>? facetFields = null)
        {
            LastFacetQuery = query;
            return new(
                Search(query, options),
                new[] { new FacetBucket("kind", new[] { new FacetValue("x", 1) }) });
        }

        public void Index(IEnumerable<SearchDocument> documents) => throw new NotSupportedException();

        public void Add(SearchDocument document) => throw new NotSupportedException();

        public void Remove(string documentId) => throw new NotSupportedException();

        public void Clear() => throw new NotSupportedException();
    }

    private sealed class DetailedEngine : IDetailedSearchEngine, ITextSearchEngine
    {
        public IReadOnlyList<DetailedSearchResult> SearchWithDetails(string query, SearchOptions? options = null) =>
            new[]
            {
                new DetailedSearchResult(
                    "detailed", 1, new SearchDocument("detailed", query),
                    new Dictionary<string, double> { ["lexical"] = 1 }),
            };

        public IReadOnlyList<SearchResult> Search(string query, SearchOptions? options = null) =>
            new[] { new SearchResult("detailed", 1, new SearchDocument("detailed", query)) };

        public void Index(IEnumerable<SearchDocument> documents) => throw new NotSupportedException();

        public void Add(SearchDocument document) => throw new NotSupportedException();

        public void Remove(string documentId) => throw new NotSupportedException();

        public void Clear() => throw new NotSupportedException();
    }

    private sealed class ExplainableEngine : IExplainableSearchEngine, ITextSearchEngine
    {
        public ScoreExplanation? Explain(string documentId, string query) =>
            new(
                documentId, "fake", 1, 1, 1, 1, 1,
                Array.Empty<TermContribution>(),
                new Dictionary<string, double>());

        public IReadOnlyList<SearchResult> Search(string query, SearchOptions? options = null) =>
            new[] { new SearchResult("explainable", 1, new SearchDocument("explainable", query)) };

        public void Index(IEnumerable<SearchDocument> documents) => throw new NotSupportedException();

        public void Add(SearchDocument document) => throw new NotSupportedException();

        public void Remove(string documentId) => throw new NotSupportedException();

        public void Clear() => throw new NotSupportedException();
    }

    private sealed class NoopReranker : IReranker
    {
        public string Name => "noop";

        public IReadOnlyList<SearchResult> Rerank(string query, IReadOnlyList<SearchResult> candidates) => candidates;
    }
}
