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
            ("new query", (IReadOnlyDictionary<string, double>)new Dictionary<string, double> { ["doc-2"] = 1 }),
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

    [Fact]
    public void Record_WithReward_AccumulatesProxySignal()
    {
        var history = new QueryFeedbackHistory();

        // A strong proxy reward on doc-2 outweighs a single binary click on doc-1.
        history.Record("quarterly report", "doc-1");
        history.Record("quarterly report", "doc-2", 3.0);

        var associations = history.GetAssociations("quarterly report");

        Assert.Equal(2, associations.Count);
        Assert.Equal("doc-2", associations[0].DocumentId);
        Assert.Equal(1.0, associations[0].Strength, 5);
        Assert.Equal(1.0 / 3.0, associations[1].Strength, 5);
    }

    [Fact]
    public void Seed_AddsAssociationsWithoutReplacing()
    {
        var history = new QueryFeedbackHistory();
        history.Record("quarterly report", "doc-1");

        history.Seed(new[] { ("quarterly report", "doc-2") });
        history.Seed(new[] { ("quarterly report", "doc-2", 2.0) });

        var associations = history.GetAssociations("quarterly report");

        Assert.Equal(2, associations.Count);
        // doc-2: 1 + 2 = 3, doc-1: 1 -> doc-2 wins.
        Assert.Equal("doc-2", associations[0].DocumentId);
    }

    [Fact]
    public void Seed_WithReward_OverloadResolves()
    {
        var history = new QueryFeedbackHistory();

        history.Seed(new[] { ("q", "d", 0.5) });

        var associations = history.GetAssociations("q");

        Assert.Single(associations);
        Assert.Equal(1.0, associations[0].Strength, 5);
    }

    [Fact]
    public void GetFuzzyAssociationScores_ManyLooselySimilarQueries_DoNotOutrankTheExactMatch()
    {
        var history = new QueryFeedbackHistory();

        history.Record("weekly sync", "exact-doc");

        // Four queries share only "weekly" with the current query: similarity 0.5 each.
        history.Record("weekly planning", "frequent-doc");
        history.Record("weekly report", "frequent-doc");
        history.Record("weekly review", "frequent-doc");
        history.Record("weekly standup", "frequent-doc");

        var scores = history.GetFuzzyAssociationScores("weekly sync", minSimilarity: 0.1);

        // The exact association contributes 1.0; a sum would give frequent-doc 4 x 0.5 = 2.0.
        Assert.Equal(1.0, scores["exact-doc"], 5);
        Assert.Equal(0.5, scores["frequent-doc"], 5);
        Assert.True(scores["frequent-doc"] < scores["exact-doc"]);

        var ordered = history.GetFuzzyAssociations("weekly sync", minSimilarity: 0.1);
        Assert.Equal("exact-doc", ordered[0].DocumentId);
    }

    [Fact]
    public void GetFuzzyAssociationScores_NegativeRewards_StillProducePenalties()
    {
        var history = new QueryFeedbackHistory();
        history.Record("weekly sync", "good-doc", 1.0);
        history.Record("weekly sync", "rejected-doc", -1.0);

        var scores = history.GetFuzzyAssociationScores("weekly sync", minSimilarity: 0.1);

        Assert.True(scores["good-doc"] > 0);
        Assert.True(scores["rejected-doc"] < 0);
    }

    [Fact]
    public void GetFuzzyAssociationScores_PositiveAndNegativeAssociationsCompose()
    {
        var history = new QueryFeedbackHistory();

        history.Record("weekly sync", "doc");
        history.Record("weekly planning", "doc", -1.0);

        var scores = history.GetFuzzyAssociationScores("weekly sync", minSimilarity: 0.1);

        // 1.0 for the exact query, minus 0.5 for the half-similar rejection.
        Assert.Equal(0.5, scores["doc"], 5);
    }

    [Fact]
    public void GetFuzzyAssociationScores_AreIndependentOfAssociationOrder()
    {
        // The same evidence, recorded in two orders. Two positives take the strongest one and the
        // negative accumulates, so both histories must read -0.5 rather than one reading 0.0.
        var positiveFirst = new QueryFeedbackHistory();
        positiveFirst.Record("weekly planning", "doc");
        positiveFirst.Record("weekly report", "doc");
        positiveFirst.Record("sync weekly", "doc", -1.0);

        var negativeFirst = new QueryFeedbackHistory();
        negativeFirst.Record("sync weekly", "doc", -1.0);
        negativeFirst.Record("weekly planning", "doc");
        negativeFirst.Record("weekly report", "doc");

        var first = positiveFirst.GetFuzzyAssociationScores("weekly sync", minSimilarity: 0.1);
        var second = negativeFirst.GetFuzzyAssociationScores("weekly sync", minSimilarity: 0.1);

        Assert.Equal(-0.5, first["doc"], 5);
        Assert.Equal(first["doc"], second["doc"], 5);
    }
}
