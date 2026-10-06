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

    private sealed class InMemoryTestEngine : ITextSearchEngine
    {
        public void Index(IEnumerable<SearchDocument> documents) { }
        public void Add(SearchDocument document) { }
        public bool Remove(string documentId) => false;
        public void Clear() { }

        public IReadOnlyList<SearchResult> Search(string query, SearchOptions? options = null)
        {
            return new[]
            {
                new SearchResult("doc-1", 100, new SearchDocument("doc-1", "test document one")),
                new SearchResult("doc-2", 50, new SearchDocument("doc-2", "test document two")),
            };
        }
    }
}
