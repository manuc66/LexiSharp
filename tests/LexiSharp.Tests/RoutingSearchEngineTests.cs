using LexiSharp.Core;
using Xunit;

namespace LexiSharp.Tests;

public class RoutingSearchEngineTests
{
    private static MetadataFilter Category(string value) =>
        new("category", MetadataFilterOperator.Equal, value);

    // Records what it was asked and returns a single result tagged with its own id, so tests can
    // assert which route ran and with which (filter-merged) options.
    private sealed class RecordingEngine : ITextSearchEngine
    {
        private readonly string _id;

        public RecordingEngine(string id) => _id = id;

        public string? LastQuery { get; private set; }

        public SearchOptions? LastOptions { get; private set; }

        public int Calls { get; private set; }

        public void Index(IEnumerable<SearchDocument> documents) { }

        public void Add(SearchDocument document) { }

        public void Remove(string documentId) { }

        public void Clear() { }

        public IReadOnlyList<SearchResult> Search(string query, SearchOptions? options = null)
        {
            Calls++;
            LastQuery = query;
            LastOptions = options;
            return new[] { new SearchResult(_id, 1.0, new SearchDocument(_id, "doc")) };
        }
    }

    private sealed class FakeQueryRouter : IQueryRouter
    {
        private readonly Func<string, IReadOnlyList<string>, QueryRoute?> _decide;

        public FakeQueryRouter(Func<string, IReadOnlyList<string>, QueryRoute?> decide) => _decide = decide;

        public int Calls { get; private set; }

        public string? LastQuery { get; private set; }

        public IReadOnlyList<string>? LastCandidates { get; private set; }

        public ValueTask<QueryRoute?> RouteAsync(
            string query,
            IReadOnlyList<string> candidateRouteIds,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            LastQuery = query;
            LastCandidates = candidateRouteIds;
            return new ValueTask<QueryRoute?>(_decide(query, candidateRouteIds));
        }
    }

    private static (RecordingEngine A, RecordingEngine B, RecordingEngine Fallback) Engines() =>
        (new RecordingEngine("a"), new RecordingEngine("b"), new RecordingEngine("fallback"));

    private static RoutingSearchEngine NewRouter(
        FakeQueryRouter router,
        (RecordingEngine A, RecordingEngine B, RecordingEngine Fallback) engines,
        IReadOnlyList<MetadataFilter>? routeAFilters = null,
        double minimumConfidence = 0.0) =>
        new(
            router,
            new[]
            {
                new SearchRoute("a", engines.A, routeAFilters),
                new SearchRoute("b", engines.B),
                new SearchRoute("fallback", engines.Fallback),
            },
            fallbackId: "fallback",
            minimumConfidence);

    [Fact]
    public void Search_RunsTheChosenRoute()
    {
        var router = new FakeQueryRouter((_, _) => new QueryRoute("b", 1.0));
        var engines = Engines();
        var engine = NewRouter(router, engines);

        var results = engine.Search("q");

        Assert.Equal("b", Assert.Single(results).DocumentId);
        Assert.Equal(1, engines.B.Calls);
        Assert.Equal(0, engines.A.Calls);
    }

    [Fact]
    public void Search_PassesTheQueryAndCandidateIdsToTheRouter()
    {
        var router = new FakeQueryRouter((_, _) => new QueryRoute("a", 1.0));
        var engine = NewRouter(router, Engines());

        engine.Search("hello world");

        Assert.Equal("hello world", router.LastQuery);
        Assert.Equal(new[] { "a", "b", "fallback" }, router.LastCandidates);
        Assert.Equal(1, router.Calls);
    }

    [Fact]
    public void Search_NullDecision_UsesFallback()
    {
        var router = new FakeQueryRouter((_, _) => null);
        var engines = Engines();

        Assert.Equal("fallback", Assert.Single(NewRouter(router, engines).Search("q")).DocumentId);
        Assert.Equal(1, engines.Fallback.Calls);
    }

    [Fact]
    public void Search_UnknownRouteId_UsesFallback()
    {
        var router = new FakeQueryRouter((_, _) => new QueryRoute("nope", 1.0));
        var engines = Engines();

        Assert.Equal("fallback", Assert.Single(NewRouter(router, engines).Search("q")).DocumentId);
        Assert.Equal(1, engines.Fallback.Calls);
    }

    [Fact]
    public void Search_ConfidenceBelowThreshold_UsesFallback()
    {
        var router = new FakeQueryRouter((_, _) => new QueryRoute("b", 0.59));
        var engines = Engines();

        Assert.Equal("fallback", Assert.Single(NewRouter(router, engines, minimumConfidence: 0.6).Search("q")).DocumentId);
        Assert.Equal(1, engines.Fallback.Calls);
    }

    [Fact]
    public void Search_ConfidenceAtThreshold_IsHonoured()
    {
        var router = new FakeQueryRouter((_, _) => new QueryRoute("b", 0.6));
        var engines = Engines();

        Assert.Equal("b", Assert.Single(NewRouter(router, engines, minimumConfidence: 0.6).Search("q")).DocumentId);
        Assert.Equal(1, engines.B.Calls);
    }

    [Fact]
    public void Search_NaNConfidence_UsesFallback()
    {
        var router = new FakeQueryRouter((_, _) => new QueryRoute("b", double.NaN));
        var engines = Engines();

        Assert.Equal("fallback", Assert.Single(NewRouter(router, engines, minimumConfidence: 0.0).Search("q")).DocumentId);
        Assert.Equal(1, engines.Fallback.Calls);
    }

    [Fact]
    public void Search_RouterThrows_UsesFallback()
    {
        var router = new FakeQueryRouter((_, _) => throw new InvalidOperationException("model down"));
        var engines = Engines();

        Assert.Equal("fallback", Assert.Single(NewRouter(router, engines).Search("q")).DocumentId);
        Assert.Equal(1, engines.Fallback.Calls);
    }

    [Fact]
    public void Search_RouteWithoutFilters_PassesCallerOptionsUnchanged()
    {
        var router = new FakeQueryRouter((_, _) => new QueryRoute("a", 1.0));
        var engines = Engines();
        var engine = NewRouter(router, engines);
        var options = new SearchOptions(Limit: 3, Offset: 1);

        engine.Search("q", options);

        Assert.Same(options, engines.A.LastOptions);
    }

    [Fact]
    public void Search_RouteFilters_AreMergedWithCallerFilters()
    {
        var routeFilter = new MetadataFilter("year", MetadataFilterOperator.GreaterThan, "2023");
        var router = new FakeQueryRouter((_, _) => new QueryRoute("a", 1.0));
        var engines = Engines();
        var engine = NewRouter(router, engines, routeAFilters: new[] { routeFilter });
        var callerFilter = Category("faq");

        engine.Search("q", new SearchOptions(Filters: new[] { callerFilter }));

        var effective = engines.A.LastOptions!;
        Assert.NotNull(effective.Filters);
        Assert.Equal(2, effective.Filters!.Count);
        Assert.Contains(callerFilter, effective.Filters);
        Assert.Contains(routeFilter, effective.Filters);
    }

    [Fact]
    public void Search_RouteFilters_Only_UsesThemWhenCallerHasNone()
    {
        var routeFilter = Category("billing");
        var router = new FakeQueryRouter((_, _) => new QueryRoute("a", 1.0));
        var engines = Engines();
        var engine = NewRouter(router, engines, routeAFilters: new[] { routeFilter });

        engine.Search("q");

        var effective = engines.A.LastOptions!;
        Assert.NotNull(effective.Filters);
        Assert.Equal(new[] { routeFilter }, effective.Filters!);
    }

    [Fact]
    public void Search_EmptyRequest_DoesNotCallTheRouter()
    {
        var router = new FakeQueryRouter((_, _) => new QueryRoute("a", 1.0));
        var engine = NewRouter(router, Engines());

        var results = engine.Search("q", new SearchOptions(Limit: 0));

        Assert.Empty(results);
        Assert.Equal(0, router.Calls);
    }

    [Fact]
    public void Search_NullQuery_Throws()
    {
        var router = new FakeQueryRouter((_, _) => new QueryRoute("a", 1.0));
        var engine = NewRouter(router, Engines());

        Assert.Throws<ArgumentNullException>(() => engine.Search(null!));
    }

    [Fact]
    public void Routes_AreExposed()
    {
        var router = new FakeQueryRouter((_, _) => new QueryRoute("a", 1.0));
        var engine = NewRouter(router, Engines());

        Assert.Equal(new[] { "a", "b", "fallback" }, engine.Routes.Select(r => r.Id));
    }

    [Fact]
    public void Writes_Throw()
    {
        var router = new FakeQueryRouter((_, _) => new QueryRoute("a", 1.0));
        var engine = NewRouter(router, Engines());
        var doc = new SearchDocument("x", "text");

        Assert.Throws<NotSupportedException>(() => engine.Index(new[] { doc }));
        Assert.Throws<NotSupportedException>(() => engine.Add(doc));
        Assert.Throws<NotSupportedException>(() => engine.Remove("x"));
        Assert.Throws<NotSupportedException>(() => engine.Clear());
    }

    [Fact]
    public void Constructor_RejectsInvalidConfiguration()
    {
        var router = new FakeQueryRouter((_, _) => new QueryRoute("a", 1.0));
        var engine = new RecordingEngine("a");

        Assert.Throws<ArgumentNullException>(() => new RoutingSearchEngine(null!, new[] { new SearchRoute("a", engine) }, "a"));
        Assert.Throws<ArgumentNullException>(() => new RoutingSearchEngine(router, null!, "a"));
        Assert.Throws<ArgumentException>(() => new RoutingSearchEngine(router, Array.Empty<SearchRoute>(), "a"));
        Assert.Throws<ArgumentException>(() => new RoutingSearchEngine(router, new[] { new SearchRoute("a", engine), new SearchRoute("a", engine) }, "a"));
        Assert.Throws<ArgumentException>(() => new RoutingSearchEngine(router, new[] { new SearchRoute("", engine) }, "a"));
        Assert.Throws<ArgumentException>(() => new RoutingSearchEngine(router, new[] { new SearchRoute("a", null!) }, "a"));
        Assert.Throws<ArgumentException>(() => new RoutingSearchEngine(router, new[] { new SearchRoute("a", engine) }, "missing"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RoutingSearchEngine(router, new[] { new SearchRoute("a", engine) }, "a", minimumConfidence: 1.5));
    }
}