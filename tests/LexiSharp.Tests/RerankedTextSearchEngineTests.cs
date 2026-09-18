using LexiSharp.Core;
using LexiSharp.Indexing;
using LexiSharp.Ranking;
using Xunit;

namespace LexiSharp.Tests;

public class RerankedTextSearchEngineTests
{
    private static readonly SearchDocument DocA = new("a", "alpha");
    private static readonly SearchDocument DocB = new("b", "beta");
    private static readonly SearchDocument DocC = new("c", "gamma");
    private static readonly SearchDocument DocD = new("d", "delta");

    private static SearchResult R(string id, SearchDocument doc, double score = 1.0) => new(id, score, doc);

    /// <summary>Stub engine returning a fixed list, counting calls and remembering the last options.</summary>
    private sealed class StubEngine : ITextSearchEngine
    {
        public List<SearchResult> Results { get; set; } = new();
        public int SearchCalls { get; private set; }
        public SearchOptions? LastOptions { get; private set; }
        public int IndexCalls { get; private set; }
        public int AddCalls { get; private set; }
        public int RemoveCalls { get; private set; }
        public int ClearCalls { get; private set; }

        public void Index(IEnumerable<SearchDocument> documents) => IndexCalls++;
        public void Add(SearchDocument document) => AddCalls++;
        public void Remove(string documentId) => RemoveCalls++;
        public void Clear() => ClearCalls++;

        public IReadOnlyList<SearchResult> Search(string query, SearchOptions? options = null)
        {
            SearchCalls++;
            LastOptions = options;
            return options?.Limit is int limit and > 0
                ? Results.Take(limit).ToList()
                : Results.ToList();
        }
    }

    /// <summary>Stub reranker applying a transform and recording what it received.</summary>
    private sealed class StubReranker : IReranker
    {
        private readonly Func<IReadOnlyList<SearchResult>, IReadOnlyList<SearchResult>> _transform;

        public StubReranker(Func<IReadOnlyList<SearchResult>, IReadOnlyList<SearchResult>>? transform = null)
        {
            _transform = transform ?? (x => x);
        }

        public IReadOnlyList<SearchResult>? ReceivedCandidates { get; private set; }
        public string? ReceivedQuery { get; private set; }
        public string Name => "Stub";

        public IReadOnlyList<SearchResult> Rerank(string query, IReadOnlyList<SearchResult> candidates)
        {
            ReceivedQuery = query;
            ReceivedCandidates = candidates;
            return _transform(candidates);
        }
    }

    private static RankedTextSearchEngine CreateInnerEngine(params SearchDocument[] docs)
    {
        var engine = new RankedTextSearchEngine(new InMemoryTextIndex(), new Bm25Scorer());
        engine.Index(docs);
        return engine;
    }

    [Fact]
    public void Search_RerankerReordersResults()
    {
        var engine = new RerankedTextSearchEngine(
            CreateInnerEngine(DocA, DocB, DocC),
            new StubReranker(c => c.Reverse().ToList()));

        var results = engine.Search("alpha beta gamma");

        // BM25 favors "alpha" first; the reranker reverses its order.
        Assert.Equal(new[] { "c", "b", "a" }, results.Select(r => r.DocumentId).ToArray());
    }

    [Fact]
    public void Search_OverFetchesFromInnerEngine_BeyondFinalLimit()
    {
        var inner = new StubEngine
        {
            Results = { R("a", DocA), R("b", DocB), R("c", DocC), R("d", DocD) }
        };
        var engine = new RerankedTextSearchEngine(inner, new StubReranker(), maxCandidates: 3);

        engine.Search("alpha", new SearchOptions(Limit: 2));

        // The inner engine was asked once, for max(limit, maxCandidates) = 3 candidates.
        Assert.Equal(1, inner.SearchCalls);
        Assert.NotNull(inner.LastOptions);
        Assert.Equal(3, inner.LastOptions.Limit);
    }

    [Fact]
    public void Search_OrderOnlyReranker_KeepsOriginalScores()
    {
        var engine = new RerankedTextSearchEngine(
            CreateInnerEngine(DocA, DocB),
            new StubReranker(c => c.Reverse().ToList()));

        var results = engine.Search("alpha beta");

        // Scores travel with the documents; only the order changed.
        Assert.Equal(new[] { "b", "a" }, results.Select(r => r.DocumentId).ToArray());
        Assert.True(results[0].Score > 0);
        Assert.Equal(results.Select(r => r.Score).OrderByDescending(x => x).ToList(),
            results.Select(r => r.Score).ToList());
    }

    [Fact]
    public void Search_MinimumScoreAppliesToFinalScore_NotToInnerScore()
    {
        // A reranker that replaces scores: inner score 1.0 becomes 0.5 — above MinimumScore,
        // while the inner engine alone with MinimumScore=0.5 would also have passed it.
        var inner = new StubEngine { Results = { R("a", DocA, 1.0) } };
        var engine = new RerankedTextSearchEngine(
            inner,
            new StubReranker(_ => new List<SearchResult> { R("a", DocA, 0.5) }));

        var results = engine.Search("q", new SearchOptions(Limit: 10, MinimumScore: 0.5));

        Assert.Single(results, r => r.DocumentId == "a");
        Assert.Equal(0.5, results[0].Score);

        // ...and below MinimumScore the re-scored candidate falls out even though the
        // inner score (1.0) would have passed it.
        var dropped = engine.Search("q", new SearchOptions(Limit: 10, MinimumScore: 0.6));
        Assert.Empty(dropped);
    }

    [Fact]
    public void Search_RerankedScoreOfZero_IsDropped()
    {
        var inner = new StubEngine { Results = { R("a", DocA), R("b", DocB) } };
        var engine = new RerankedTextSearchEngine(
            inner,
            new StubReranker(c => new List<SearchResult>
            {
                R("a", DocA, 0.0), // convention: 0 = "not a match" after re-scoring
                R("b", DocB, 1.0),
            }));

        var results = engine.Search("q");

        Assert.Single(results, r => r.DocumentId == "b");
    }

    [Fact]
    public void Search_NonFiniteRerankedScores_AreDropped()
    {
        var inner = new StubEngine { Results = { R("a", DocA) } };
        var engine = new RerankedTextSearchEngine(
            inner,
            new StubReranker(_ => new List<SearchResult> { R("a", DocA, double.NaN) }));

        Assert.Empty(engine.Search("q"));
    }

    [Fact]
    public void Search_RerankerMayFilter_OutExtraCandidatesAreTrimmedToLimit()
    {
        var inner = new StubEngine
        {
            Results = { R("a", DocA, 4.0), R("b", DocB, 3.0), R("c", DocC, 2.0), R("d", DocD, 1.0) }
        };
        var engine = new RerankedTextSearchEngine(
            inner,
            new StubReranker(c => c.Take(3).ToList()),
            maxCandidates: 10);

        var results = engine.Search("q", new SearchOptions(Limit: 3));

        Assert.Equal(new[] { "a", "b", "c" }, results.Select(r => r.DocumentId).ToArray());
    }

    [Fact]
    public void Search_EqualScores_DecoratorPreservesRerankerOrder()
    {
        // Tie-breaking on equal scores is the reranker's job, not the decorator's:
        // whatever order it returns is what the caller sees.
        var inner = new StubEngine
        {
            Results = { R("z", DocD, 2.0), R("a", DocA, 2.0) }
        };
        var engine = new RerankedTextSearchEngine(inner, new StubReranker());

        var results = engine.Search("q");

        Assert.Equal(new[] { "z", "a" }, results.Select(r => r.DocumentId).ToArray());
    }

    [Fact]
    public void Search_EmptyInnerResults_RerankerIsNeverCalled()
    {
        var inner = new StubEngine();
        var reranker = new StubReranker();
        var engine = new RerankedTextSearchEngine(inner, reranker);

        Assert.Empty(engine.Search("q"));
        Assert.Null(reranker.ReceivedCandidates);
    }

    [Fact]
    public void Search_LimitAtOrBelowZero_ReturnsEmpty()
    {
        var engine = new RerankedTextSearchEngine(new StubEngine(), new StubReranker());

        Assert.Empty(engine.Search("q", new SearchOptions(Limit: 0)));
    }

    [Fact]
    public void Search_RerankerReceivesQueryAndCandidates()
    {
        var inner = new StubEngine { Results = { R("a", DocA) } };
        var reranker = new StubReranker();
        var engine = new RerankedTextSearchEngine(inner, reranker);

        engine.Search("hello");

        Assert.Equal("hello", reranker.ReceivedQuery);
        Assert.Single(reranker.ReceivedCandidates!, r => r.DocumentId == "a");
    }

    [Fact]
    public void Writes_AreForwardedToInnerEngine()
    {
        var inner = new StubEngine();
        var engine = new RerankedTextSearchEngine(inner, new StubReranker());

        engine.Index(new[] { DocA });
        engine.Add(DocB);
        engine.Remove("a");
        engine.Clear();

        Assert.Equal(1, inner.IndexCalls);
        Assert.Equal(1, inner.AddCalls);
        Assert.Equal(1, inner.RemoveCalls);
        Assert.Equal(1, inner.ClearCalls);
    }

    [Fact]
    public void Ctor_RejectsNullInnerEngineOrReranker()
    {
        Assert.Throws<ArgumentNullException>(() => new RerankedTextSearchEngine(null!, new StubReranker()));
        Assert.Throws<ArgumentNullException>(() => new RerankedTextSearchEngine(new StubEngine(), null!));
    }

    [Fact]
    public void Integration_RerankedEngineOverRealEngine_KeepsRelevance()
    {
        // With a pass-through reranker the engine behaves like the inner one.
        var docs = new[] { DocA, DocB, DocC };
        var plain = CreateInnerEngine(docs);
        var wrapped = new RerankedTextSearchEngine(CreateInnerEngine(docs), new StubReranker());

        var expected = plain.Search("alpha");
        var actual = wrapped.Search("alpha");

        Assert.Equal(expected.Select(r => r.DocumentId), actual.Select(r => r.DocumentId));
    }
}
