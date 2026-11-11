using LexiSharp.Core;
using LexiSharp.Ranking;
using Xunit;

namespace LexiSharp.Tests;

public class FeedbackAwareTextSearchEngineTests
{
    [Fact]
    public void Search_NoHistory_ReturnsUnmodifiedResults()
    {
        var engine = new InMemoryTestEngine();
        var history = new QueryFeedbackHistory();
        var feedbackEngine = new FeedbackAwareTextSearchEngine(engine, history);

        var results = feedbackEngine.Search("test query");

        Assert.Equal(2, results.Count);
        Assert.Equal("doc-1", results[0].DocumentId);
        Assert.Equal("doc-2", results[1].DocumentId);
    }

    [Fact]
    public void Search_WithMatchingHistory_BoostsAssociatedDocument()
    {
        var engine = new InMemoryTestEngine();
        var history = new QueryFeedbackHistory();
        // The base engine returns doc-1 at 100 and doc-2 at 50, so a boost below 50
        // leaves the ordering untouched — which is the point of task 3 (a boost that
        // only applies when the base ranking is uncertain).
        var feedbackEngine = new FeedbackAwareTextSearchEngine(engine, history, maxBoost: 60.0);

        feedbackEngine.Learn("test query about invoices", "doc-2");

        var results = feedbackEngine.Search("test query about invoices");

        Assert.Equal("doc-2", results[0].DocumentId);
        Assert.Equal(110, results[0].Score);
        Assert.Equal(100, results[1].Score);
    }

    [Fact]
    public void Search_WithFuzzyMatch_BoostsPartially()
    {
        var engine = new InMemoryTestEngine();
        var history = new QueryFeedbackHistory();
        var feedbackEngine = new FeedbackAwareTextSearchEngine(engine, history, maxBoost: 5.0);

        // Learn a similar (but not identical) query
        feedbackEngine.Learn("test query about invoices", "doc-2");

        var results = feedbackEngine.Search("test query");

        // doc-2 should be boosted but maybe not enough to overtake doc-1
        var doc2 = results.First(r => r.DocumentId == "doc-2");
        Assert.True(doc2.Score > 50); // Original score was 50, boost should increase it
    }

    [Fact]
    public void Search_EmptyHistory_ReturnsOriginalRanking()
    {
        var engine = new InMemoryTestEngine();
        var history = new QueryFeedbackHistory();
        var feedbackEngine = new FeedbackAwareTextSearchEngine(engine, history);

        var results = feedbackEngine.Search("anything");

        Assert.Equal("doc-1", results[0].DocumentId);
        Assert.Equal("doc-2", results[1].DocumentId);
    }

    [Fact]
    public void Learn_RecordsAssociationInHistory()
    {
        var engine = new InMemoryTestEngine();
        var history = new QueryFeedbackHistory();
        var feedbackEngine = new FeedbackAwareTextSearchEngine(engine, history);

        feedbackEngine.Learn("my query", "doc-1");

        var associations = history.GetAssociations("my query");
        Assert.Single(associations);
        Assert.Equal("doc-1", associations[0].DocumentId);
    }

    [Fact]
    public void Search_CandidatePool_OverFetchesAndPromotesBeyondTheRequestedPage()
    {
        var history = new QueryFeedbackHistory();
        history.Record("query", "c");

        var inner = new PagedEngine(("a", 100), ("b", 90), ("c", 80));
        var engine = new FeedbackAwareTextSearchEngine(
            inner, history, maxBoost: 50, minSimilarity: 0.1, candidatePool: 3);

        var results = engine.Search("query", new SearchOptions(Limit: 1));

        // c is outside the requested one-result page, but the pool sees it and the boost promotes it.
        Assert.Equal("c", results[0].DocumentId);
        Assert.NotNull(inner.LastOptions);
        Assert.Equal(3, inner.LastOptions.Limit);
        Assert.Equal(0, inner.LastOptions.Offset);
        Assert.Equal(double.NegativeInfinity, inner.LastOptions.MinimumScore);
    }

    [Fact]
    public void Search_WithoutCandidatePool_CannotPromoteBeyondTheRequestedPage()
    {
        var history = new QueryFeedbackHistory();
        history.Record("query", "c");

        var inner = new PagedEngine(("a", 100), ("b", 90), ("c", 80));
        var engine = new FeedbackAwareTextSearchEngine(inner, history, maxBoost: 50, minSimilarity: 0.1);

        var results = engine.Search("query", new SearchOptions(Limit: 1));

        Assert.Equal("a", results[0].DocumentId);
        Assert.NotNull(inner.LastOptions);
        Assert.Equal(1, inner.LastOptions.Limit);
    }

    [Fact]
    public void Search_CandidatePool_CutsTheCallersPageAfterBoosting()
    {
        var history = new QueryFeedbackHistory();
        history.Record("query", "c");

        var inner = new PagedEngine(("a", 100), ("b", 90), ("c", 10));
        var engine = new FeedbackAwareTextSearchEngine(
            inner, history, maxBoost: 95, minSimilarity: 0.1, candidatePool: 3);

        // Boosted order: c (105), a (100), b (90). The second page entry is a, not b: the page is
        // cut from the boosted ranking after the inner engine returned the pool from offset 0.
        var results = engine.Search("query", new SearchOptions(Limit: 1, Offset: 1));

        Assert.Equal("a", results[0].DocumentId);
        Assert.NotNull(inner.LastOptions);
        Assert.Equal(0, inner.LastOptions.Offset);
        Assert.Equal(4, inner.LastOptions.Limit); // offset 1 + base pool max(limit 1, candidatePool 3)
    }

    [Fact]
    public void Search_CandidatePool_AppliesMinimumScoreToTheBoostedScore()
    {
        var history = new QueryFeedbackHistory();
        history.Record("query", "a", -1.0);
        history.Record("query", "b", 1.0);

        var inner = new PagedEngine(("a", 100), ("b", 10));
        var engine = new FeedbackAwareTextSearchEngine(
            inner, history, maxBoost: 60, minSimilarity: 0.1, candidatePool: 2);

        var results = engine.Search("query", new SearchOptions(Limit: 2, MinimumScore: 50));

        // b enters because the threshold applies after the boost (10 + 60), and a leaves because
        // its penalty takes it below it (100 - 60). The inner engine asked for no threshold.
        Assert.Contains(results, r => r.DocumentId == "b" && r.Score == 70);
        Assert.DoesNotContain(results, r => r.DocumentId == "a");
        Assert.NotNull(inner.LastOptions);
        Assert.Equal(double.NegativeInfinity, inner.LastOptions.MinimumScore);
    }

    [Fact]
    public void Search_DefaultCandidatePool_ForwardsTheCallersPageToTheInnerEngine()
    {
        var history = new QueryFeedbackHistory();
        var inner = new PagedEngine(("a", 100), ("b", 90), ("c", 80));
        var engine = new FeedbackAwareTextSearchEngine(inner, history);

        var results = engine.Search("query", new SearchOptions(Limit: 1, Offset: 1, MinimumScore: 5));

        Assert.Equal("b", results[0].DocumentId);
        Assert.NotNull(inner.LastOptions);
        Assert.Equal(1, inner.LastOptions.Limit);
        Assert.Equal(1, inner.LastOptions.Offset);
        Assert.Equal(5, inner.LastOptions.MinimumScore);
    }

    [Fact]
    public void Search_MultiplicativeBoost_ScalesAndDampsTheBaseScore()
    {
        var history = new QueryFeedbackHistory();
        history.Record("query", "a", -1.0);
        history.Record("query", "b", 1.0);

        var inner = new PagedEngine(("a", 100), ("b", 50));
        var engine = new FeedbackAwareTextSearchEngine(
            inner,
            history,
            maxBoost: 0.5,
            minSimilarity: 0.1,
            candidatePool: 2,
            boostMode: FeedbackBoostMode.Multiplicative);

        var results = engine.Search("query", new SearchOptions(Limit: 2));

        // a: 100 * (1 - 0.5) = 50; b: 50 * (1 + 0.5) = 75.
        Assert.Equal(50, results.Single(r => r.DocumentId == "a").Score);
        Assert.Equal(75, results.Single(r => r.DocumentId == "b").Score);
        Assert.Equal("b", results[0].DocumentId);
    }

    [Fact]
    public void Constructor_RejectsNegativeCandidatePoolAndUnknownBoostMode()
    {
        var history = new QueryFeedbackHistory();
        var engine = new InMemoryTestEngine();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => new FeedbackAwareTextSearchEngine(engine, history, candidatePool: -1));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new FeedbackAwareTextSearchEngine(engine, history, boostMode: (FeedbackBoostMode)99));
    }

    private sealed class InMemoryTestEngine : ITextSearchEngine
    {
        public void Index(IEnumerable<SearchDocument> documents) { }
        public void Add(SearchDocument document) { }
        public bool Remove(string documentId) => false;
        public void Clear() { }

        public IReadOnlyList<SearchResult> Search(string query, SearchOptions? options = null, CancellationToken cancellationToken = default)
        {
            return new[]
            {
                new SearchResult("doc-1", 100, new SearchDocument("doc-1", "test document one")),
                new SearchResult("doc-2", 50, new SearchDocument("doc-2", "test document two")),
            };
        }
    }

    /// <summary>A stub that honours the paging and score floor the decorator passes down.</summary>
    private sealed class PagedEngine : ITextSearchEngine
    {
        private readonly IReadOnlyList<SearchResult> _results;

        public PagedEngine(params (string Id, double Score)[] results) =>
            _results = results
                .Select(r => new SearchResult(r.Id, r.Score, new SearchDocument(r.Id, $"text of {r.Id}")))
                .ToList();

        public SearchOptions? LastOptions { get; private set; }

        public void Index(IEnumerable<SearchDocument> documents) { }
        public void Add(SearchDocument document) { }
        public bool Remove(string documentId) => false;
        public void Clear() { }

        public IReadOnlyList<SearchResult> Search(string query, SearchOptions? options = null, CancellationToken cancellationToken = default)
        {
            LastOptions = options;
            options ??= SearchOptions.Default;

            return _results
                .Where(r => r.Score >= options.MinimumScore)
                .Skip(options.Offset)
                .Take(options.Limit)
                .ToList();
        }
    }
}
