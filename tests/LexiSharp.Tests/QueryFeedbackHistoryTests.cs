using LexiSharp.Ranking;
using Xunit;

namespace LexiSharp.Tests;

public class QueryFeedbackHistoryTests
{
    [Fact]
    public void Record_SingleAssociation_IsRetrievable()
    {
        var history = new QueryFeedbackHistory();

        history.Record("quarterly report", "doc-1");

        var associations = history.GetAssociations("quarterly report");

        Assert.Single(associations);
        Assert.Equal("doc-1", associations[0].DocumentId);
        Assert.Equal(1.0, associations[0].Strength, 5);
    }

    [Fact]
    public void Record_SameQueryMultipleTimes_IncreasesStrength()
    {
        var history = new QueryFeedbackHistory();

        history.Record("quarterly report", "doc-1");
        history.Record("quarterly report", "doc-1");
        history.Record("quarterly report", "doc-1");

        var associations = history.GetAssociations("quarterly report");

        Assert.Single(associations);
        Assert.Equal(1.0, associations[0].Strength, 5);
    }

    [Fact]
    public void Record_MultipleDocumentsForSameQuery_ReturnsAll()
    {
        var history = new QueryFeedbackHistory();

        history.Record("quarterly report", "doc-1");
        history.Record("quarterly report", "doc-2");

        var associations = history.GetAssociations("quarterly report");

        Assert.Equal(2, associations.Count);
    }

    [Fact]
    public void GetAssociations_UnknownQuery_ReturnsEmpty()
    {
        var history = new QueryFeedbackHistory();

        var associations = history.GetAssociations("unknown query");

        Assert.Empty(associations);
    }

    [Fact]
    public void GetFuzzyAssociations_PartialTokenOverlap_ReturnsMatches()
    {
        var history = new QueryFeedbackHistory();

        history.Record("monthly report review", "doc-1");
        history.Record("weekly review", "doc-2");

        // "report" overlaps with "monthly report review" (1 of 3 terms, above the floor)
        var associations = history.GetFuzzyAssociations("report", minSimilarity: 0.3);

        Assert.NotEmpty(associations);
        Assert.Contains(associations, a => a.DocumentId == "doc-1");
    }

    [Fact]
    public void GetFuzzyAssociations_NoOverlap_ReturnsEmpty()
    {
        var history = new QueryFeedbackHistory();

        history.Record("monthly report review", "doc-1");

        var associations = history.GetFuzzyAssociations("completely different words", minSimilarity: 0.3);

        Assert.Empty(associations);
    }

    [Fact]
    public void Forget_RemovesAssociation()
    {
        var history = new QueryFeedbackHistory();

        history.Record("quarterly report", "doc-1");
        history.Forget("quarterly report");

        var associations = history.GetAssociations("quarterly report");

        Assert.Empty(associations);
    }

    [Fact]
    public void SnapshotRestore_RoundTripsMultiplicity()
    {
        var original = new QueryFeedbackHistory();
        original.Record("quarterly report", "doc-1");
        original.Record("quarterly report", "doc-1");
        original.Record("quarterly report", "doc-1");
        original.Record("quarterly report", "doc-2");

        var restored = new QueryFeedbackHistory();
        restored.Restore(original.Snapshot());

        var associations = restored.GetAssociations("quarterly report");

        // doc-1 was answered three times and doc-2 once: replaying the associations as single
        // mentions would have tied them, so the ratio is the thing being preserved here.
        Assert.Equal(2, associations.Count);
        Assert.Equal(1.0, associations[0].Strength, 9);
        Assert.Equal(1.0 / 3.0, associations[1].Strength, 9);
        Assert.Equal(1, restored.Count);
    }

    [Fact]
    public void Restore_ReplacesRatherThanMerges()
    {
        var history = new QueryFeedbackHistory();
        history.Record("old query", "doc-1");

        history.Restore(new[]
        {
            ("new query", (IReadOnlyDictionary<string, int>)new Dictionary<string, int> { ["doc-2"] = 1 }),
        });

        Assert.Empty(history.GetAssociations("old query"));
        Assert.Single(history.GetAssociations("new query"));
    }

    [Fact]
    public void Count_TracksDistinctQueries()
    {
        var history = new QueryFeedbackHistory();

        Assert.Equal(0, history.Count);

        history.Record("query one", "doc-1");
        history.Record("query two", "doc-2");
        history.Record("query one", "doc-3"); // Same query, different doc

        Assert.Equal(2, history.Count);
    }

    [Fact]
    public void Record_NormalizesQueryCase()
    {
        var history = new QueryFeedbackHistory();

        history.Record("Quarterly Report", "doc-1");

        var associations = history.GetAssociations("quarterly report");

        Assert.Single(associations);
    }
}
