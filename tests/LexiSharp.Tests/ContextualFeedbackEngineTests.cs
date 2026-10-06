using LexiSharp.Core;
using LexiSharp.Ranking;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>Who is asking. A caller's own type, so the library models no concept of one.</summary>
internal sealed record Viewer(string Id);

public class ContextualFeedbackEngineTests
{
    private sealed class FixedEngine : ITextSearchEngine
    {
        private readonly IReadOnlyList<SearchResult> _results;

        public FixedEngine(params (string Id, double Score)[] results) =>
            _results = results
                .Select(r => new SearchResult(r.Id, r.Score, new SearchDocument(r.Id, $"text of {r.Id}")))
                .ToList();

        public void Index(IEnumerable<SearchDocument> documents) { }

        public void Add(SearchDocument document) { }

        public bool Remove(string documentId) => false;

        public void Clear() { }

        public IReadOnlyList<SearchResult> Search(string query, SearchOptions? options = null) =>
            options is { IsEmpty: false } ? _results : Array.Empty<SearchResult>();
    }

    [Fact]
    public void EachViewerGetsTheirOwnHistory()
    {
        var anaHistory = new QueryFeedbackHistory();
        anaHistory.Record("quarterly report", "b");

        var boHistory = new QueryFeedbackHistory();
        boHistory.Record("quarterly report", "a");

        var engine = new FeedbackAwareTextSearchEngine<Viewer>(
            new FixedEngine(("a", 100), ("b", 99.9)),
            viewer => viewer?.Id switch { "ana" => anaHistory, "bo" => boHistory, _ => null },
            maxBoost: 50);

        // Ana's history promotes b, Bo's promotes a. One engine, and the ranking follows whose
        // history was consulted.
        var forAna = engine.Search("quarterly report", null, new Viewer("ana"));
        var forBo = engine.Search("quarterly report", null, new Viewer("bo"));

        Assert.Equal("b", forAna[0].DocumentId);
        Assert.Equal("a", forBo[0].DocumentId);
    }

    [Fact]
    public void AViewerWithNoHistoryGetsTheBaseRankingUnchanged()
    {
        var engine = new FeedbackAwareTextSearchEngine<Viewer>(
            new FixedEngine(("a", 100), ("b", 99.9)),
            _ => null,
            maxBoost: 50);

        var results = engine.Search("quarterly report", null, new Viewer("stranger"));

        // Not an error: someone who has answered nothing has no history to contribute.
        Assert.Equal(new[] { 100.0, 99.9 }, results.Select(r => r.Score).OrderByDescending(s => s));
    }

    [Fact]
    public void RequirePayload_ThrowsRatherThanServingWithoutAHistory()
    {
        // The unboosted ranking is a plausible-looking answer, so a search that lost its payload
        // would not look wrong to the caller that lost it.
        var engine = new FeedbackAwareTextSearchEngine<Viewer>(
            new FixedEngine(("a", 100), ("b", 99.9)),
            _ => null,
            maxBoost: 50,
            requirePayload: true);

        ITextSearchEngine unaware = engine;

        var error = Assert.Throws<InvalidOperationException>(() => unaware.Search("q"));
        Assert.Contains("requirePayload", error.Message);
        Assert.Contains(nameof(IContextualSearchEngine<Viewer>), error.Message);

        // Offered, and mapped to no history: still served, because the selector decided that and
        // requirePayload only rejects an absent payload.
        Assert.NotEmpty(engine.Search("q", null, new Viewer("stranger")));
    }

    [Fact]
    public void RequirePayload_DefaultsOff_SoASingleHistoryEngineKeepsWorking()
    {
        var history = new QueryFeedbackHistory();
        history.Record("quarterly report", "b");

        var engine = new FeedbackAwareTextSearchEngine<Viewer>(
            new FixedEngine(("a", 100), ("b", 99.9)),
            _ => history,
            maxBoost: 50);

        ITextSearchEngine unaware = engine;
        var results = unaware.Search("quarterly report");

        Assert.Equal("b", results[0].DocumentId);
    }

    [Fact]
    public void Learn_RecordsAgainstTheViewersOwnHistory()
    {
        var anaHistory = new QueryFeedbackHistory();
        var boHistory = new QueryFeedbackHistory();

        var engine = new FeedbackAwareTextSearchEngine<Viewer>(
            new FixedEngine(("a", 100)),
            viewer => viewer?.Id switch { "ana" => anaHistory, "bo" => boHistory, _ => null },
            maxBoost: 50);

        Assert.True(engine.Learn(new Viewer("ana"), "quarterly report", "doc-a"));
        Assert.True(engine.Learn(new Viewer("bo"), "quarterly report", "doc-b"));

        // Ana's learning never reaches Bo's history.
        Assert.Equal(new[] { "doc-a" }, anaHistory.GetAssociations("quarterly report").Select(a => a.DocumentId));
        Assert.Equal(new[] { "doc-b" }, boHistory.GetAssociations("quarterly report").Select(a => a.DocumentId));
    }

    [Fact]
    public void Learn_ReportsWhenThereWasNoHistoryToRecordInto()
    {
        var engine = new FeedbackAwareTextSearchEngine<Viewer>(
            new FixedEngine(("a", 100)),
            _ => null,
            maxBoost: 50);

        // False rather than a silent no-op: the caller has just been told nothing was learned.
        Assert.False(engine.Learn(new Viewer("stranger"), "quarterly report", "doc-a"));
    }

    [Fact]
    public void Learn_ThenSearchPromotesThroughTheLearnedHistory()
    {
        var history = new QueryFeedbackHistory();
        var engine = new FeedbackAwareTextSearchEngine<Viewer>(
            new FixedEngine(("a", 100), ("b", 99.9)),
            _ => history,
            maxBoost: 50);

        engine.Learn(new Viewer("ana"), "quarterly report", "b");

        Assert.Equal("b", engine.Search("quarterly report", null, new Viewer("ana"))[0].DocumentId);
    }

    [Fact]
    public void NonGenericEngine_IsUnchangedByTheGenericOneExisting()
    {
        // One history for every search, the shape it has always had, now forwarded.
        var history = new QueryFeedbackHistory();
        history.Record("quarterly report", "b");

        var engine = new FeedbackAwareTextSearchEngine(
            new FixedEngine(("a", 100), ("b", 99.9)),
            history,
            maxBoost: 50);

        Assert.Equal("b", engine.Search("quarterly report")[0].DocumentId);
        Assert.Same(history, engine.History);

        engine.Learn("quarterly report", "b");

        // Strength is normalized against the most-chosen document, so it stays 1 however many
        // times that document was chosen; the counter is what the second Learn changed.
        Assert.Equal(2, history.Snapshot().Single().DocumentCounts["b"]);
    }

    [Fact]
    public void SnapshotRestore_KeepsTheFuzzyMatchWorking()
    {
        // The recorded terms are now computed at construction rather than per search; a restore
        // rebuilds the associations, so the terms have to be rebuilt with them.
        var original = new QueryFeedbackHistory();
        original.Record("monthly report review", "doc-1");

        var restored = new QueryFeedbackHistory();
        restored.Restore(original.Snapshot());

        var associations = restored.GetFuzzyAssociations("report", minSimilarity: 0.3);

        Assert.Contains(associations, a => a.DocumentId == "doc-1");
    }

    [Fact]
    public void AssociationScores_AndTheListProjection_Agree()
    {
        var history = new QueryFeedbackHistory();
        history.Record("monthly report review", "doc-1");
        history.Record("monthly report review", "doc-1");
        history.Record("quarterly report", "doc-2");

        var scores = history.GetFuzzyAssociationScores("report", minSimilarity: 0.3);
        var list = history.GetFuzzyAssociations("report", minSimilarity: 0.3);

        Assert.Equal(
            list.OrderByDescending(a => a.Strength).Select(a => a.DocumentId),
            scores.OrderByDescending(kvp => kvp.Value).Select(kvp => kvp.Key));
    }
}
