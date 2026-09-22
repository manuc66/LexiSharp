using LexiSharp.Core;
using LexiSharp.Indexing;
using LexiSharp.Ranking;
using Xunit;

namespace LexiSharp.Tests;

public class BoostedTextSearchEngineTests
{
    private static readonly string[] BoostedOrderByWeight = { "b", "a" };
    private static readonly string[] DescendingRankOrder = { "d1", "d2", "d3" };
    private static readonly string[] AscendingIdOrder = { "a", "b" };

    private static readonly SearchDocument[] Docs =
    {
        new("a", "the quick brown fox",
            new Dictionary<string, string> { ["weight"] = "1" }),
        new("b", "the quick brown fox jumps over the lazy dog",
            new Dictionary<string, string> { ["weight"] = "5" }),
    };

    private static RankedTextSearchEngine CreateInnerEngine(IEnumerable<SearchDocument> docs)
    {
        var engine = new RankedTextSearchEngine(new InMemoryTextIndex(), new Bm25Scorer());
        engine.Index(docs);
        return engine;
    }

    private static double FieldBoost(SearchResult r) =>
        double.Parse(r.Document.Fields!["weight"], System.Globalization.CultureInfo.InvariantCulture);

    private sealed class StubEngine : ITextSearchEngine
    {
        public List<SearchResult> Results { get; set; } = new();

        public int IndexCalls { get; private set; }

        public int AddCalls { get; private set; }

        public int RemoveCalls { get; private set; }

        public int ClearCalls { get; private set; }

        public int SearchCalls { get; private set; }

        public void Index(IEnumerable<SearchDocument> documents)
        {
            IndexCalls++;
            Results.Clear();
            foreach (var document in documents)
                Results.Add(new SearchResult(document.Id, 1.0, document));
        }

        public void Add(SearchDocument document)
        {
            AddCalls++;
            Results.Add(new SearchResult(document.Id, 1.0, document));
        }

        public void Remove(string documentId)
        {
            RemoveCalls++;
        }

        public void Clear()
        {
            ClearCalls++;
        }

        public IReadOnlyList<SearchResult> Search(string query, SearchOptions? options = null)
        {
            SearchCalls++;
            return options?.Limit is int limit and > 0
                ? Results.Take(limit).ToList()
                : Results.ToList();
        }
    }

    private static List<SearchResult> Ranked(int count)
    {
        var idle = new SearchDocument("idle", "unused text");
        return Enumerable.Range(1, count)
            .Select(n => new SearchResult($"d{n}", count + 1 - n, idle))
            .ToList();
    }

    [Fact]
    public void Boost_ReprioritizesByDocumentMetadata()
    {
        var inner = CreateInnerEngine(Docs);
        var boosted = new BoostedTextSearchEngine(inner, r => FieldBoost(r));

        var raw = inner.Search("quick brown fox");
        var results = boosted.Search("quick brown fox");

        Assert.Equal("a", raw[0].DocumentId);

        Assert.Equal(BoostedOrderByWeight, results.Select(r => r.DocumentId));

        var rawB = raw.Single(r => r.DocumentId == "b").Score;
        var boostedB = results.Single(r => r.DocumentId == "b").Score;
        Assert.Equal(rawB * 5, boostedB, 9);

        var rawA = raw.Single(r => r.DocumentId == "a").Score;
        var boostedA = results.Single(r => r.DocumentId == "a").Score;
        Assert.Equal(rawA, boostedA, 9);
    }

    [Fact]
    public void Damp_PushesTopMatchDown()
    {
        var inner = CreateInnerEngine(Docs);
        var damped = new BoostedTextSearchEngine(
            inner,
            r => r.DocumentId == "a" ? 0.1 : 1.0);

        var results = damped.Search("quick brown fox");

        Assert.Equal(BoostedOrderByWeight, results.Select(r => r.DocumentId));
    }

    [Fact]
    public void ZeroBoost_ExcludesDocument()
    {
        var inner = CreateInnerEngine(Docs);
        var boosted = new BoostedTextSearchEngine(
            inner,
            r => r.DocumentId == "a" ? 0.0 : 1.0);

        var results = boosted.Search("quick brown fox");

        Assert.DoesNotContain(results, r => r.DocumentId == "a");
        Assert.Contains(results, r => r.DocumentId == "b");
    }

    [Fact]
    public void OverFetch_BringsDeepCandidateIntoTopN()
    {
        var inner = new StubEngine { Results = Ranked(12).ToList() };
        var boosted = new BoostedTextSearchEngine(
            inner,
            r => r.DocumentId == "d11" ? 1000 : 1.0);

        var results = boosted.Search("query", new SearchOptions(Limit: 3));

        Assert.Equal(3, results.Count);
        Assert.Equal("d11", results[0].DocumentId);
    }

    [Fact]
    public void UnderFetch_LimitsBoostLatitude()
    {
        var inner = new StubEngine { Results = Ranked(12).ToList() };
        var boosted = new BoostedTextSearchEngine(
            inner,
            r => r.DocumentId == "d11" ? 1000 : 1.0,
            maxCandidates: 1);

        var results = boosted.Search("query", new SearchOptions(Limit: 3));

        Assert.DoesNotContain(results, r => r.DocumentId == "d11");
    }

    [Fact]
    public void MinimumScore_IsAppliedToBoostedScore()
    {
        var inner = new StubEngine { Results = Ranked(3).ToList() };
        var boosted = new BoostedTextSearchEngine(inner, r => 0.1);

        var results = boosted.Search("query", new SearchOptions(Limit: 3, MinimumScore: 0.5));

        Assert.Empty(results);
    }

    [Fact]
    public void NegativeOffset_PenalizesMatch()
    {
        var inner = new StubEngine { Results = Ranked(3).ToList() };
        var boosted = new BoostedTextSearchEngine(inner, r => new ScoreBoost(Add: -2.5));

        var results = boosted.Search("query");

        Assert.Equal(DescendingRankOrder, results.Select(r => r.DocumentId));
        Assert.Equal(0.5, results[0].Score, 9);
        Assert.Equal(-0.5, results[1].Score, 9);
        Assert.Equal(-1.5, results[2].Score, 9);
    }

    [Fact]
    public void Boost_CombinesFactorAndOffset()
    {
        var inner = new StubEngine { Results = Ranked(3).ToList() };
        var boosted = new BoostedTextSearchEngine(inner, r => new ScoreBoost(Add: 1, Multiply: 2));

        var results = boosted.Search("query");

        Assert.Equal(7, results[0].Score, 9);
        Assert.Equal(5, results[1].Score, 9);
        Assert.Equal(3, results[2].Score, 9);
    }

    [Fact]
    public void InvalidBoost_Throws()
    {
        var inner = new StubEngine { Results = Ranked(1).ToList() };

        foreach (double invalid in new[] { -1.0, double.NaN, double.PositiveInfinity })
        {
            var boosted = new BoostedTextSearchEngine(inner, r => invalid);

            Assert.Throws<ArgumentException>(() => boosted.Search("query"));
        }
    }

    [Fact]
    public void Writes_ForwardToInnerEngine()
    {
        var inner = new StubEngine();
        var boosted = new BoostedTextSearchEngine(inner, r => 1.0);

        boosted.Index(new[] { new SearchDocument("x", "text") });
        boosted.Add(new SearchDocument("y", "text"));
        boosted.Remove("x");
        boosted.Clear();

        Assert.Equal(1, inner.IndexCalls);
        Assert.Equal(1, inner.AddCalls);
        Assert.Equal(1, inner.RemoveCalls);
        Assert.Equal(1, inner.ClearCalls);
    }

    [Fact]
    public void Search_Offset_SkipsTheBoostedRanking()
    {
        var inner = new StubEngine { Results = Ranked(6).ToList() };
        var boosted = new BoostedTextSearchEngine(inner, r => 1.0);

        var all = boosted.Search("query", new SearchOptions(Limit: 10));
        Assert.Equal(6, all.Count);

        var page = boosted.Search("query", new SearchOptions(Limit: 2, Offset: 2));

        Assert.Equal(
            all.Skip(2).Take(2).Select(r => r.DocumentId),
            page.Select(r => r.DocumentId));

        Assert.Empty(boosted.Search("query", new SearchOptions(Limit: 2, Offset: 6)));
        Assert.Empty(boosted.Search("query", new SearchOptions(Limit: 2, Offset: -1)));
    }

    [Fact]
    public void Search_Offset_StillLetsDeepBoostReachThePage()
    {
        // Tight pool (maxCandidates = 2): the inner fetch must cover the skipped prefix too —
        // Offset(3) + max(Limit, maxCandidates) = 5 candidates — so raw #5 (d5) is fetched and
        // its boost can land it exactly on the page [3, 4). A pool of max(Limit, maxCandidates)
        // alone (2 candidates) would leave the page empty.
        var inner = new StubEngine { Results = Ranked(12).ToList() };
        var boosted = new BoostedTextSearchEngine(
            inner,
            r => r.DocumentId == "d5" ? 1.1875 : 1.0, // 8 * 1.1875 = 9.5 → rank 3, between d3 (10) and d4 (9)
            maxCandidates: 2);

        var results = boosted.Search("query", new SearchOptions(Limit: 1, Offset: 3));

        var hit = Assert.Single(results);
        Assert.Equal("d5", hit.DocumentId);
    }

    [Fact]
    public void Search_LimitZero_DoesNotReachInnerEngine()
    {
        var inner = new StubEngine();
        var boosted = new BoostedTextSearchEngine(inner, r => 1.0);

        var results = boosted.Search("query", new SearchOptions(Limit: 0));

        Assert.Empty(results);
        Assert.Equal(0, inner.SearchCalls);
    }

    [Fact]
    public void Search_BoostedTie_BreaksByDocumentId()
    {
        // Both documents reach the same boosted score although the inner engine orders "b" first.
        var inner = new StubEngine
        {
            Results = new List<SearchResult>
            {
                new("b", 1.0, new SearchDocument("b", "same")),
                new("a", 1.0, new SearchDocument("a", "same")),
            },
        };
        var boosted = new BoostedTextSearchEngine(inner, r => 1.0);

        var results = boosted.Search("query");

        Assert.Equal(AscendingIdOrder, results.Select(r => r.DocumentId));
    }
}