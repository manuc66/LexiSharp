using LexiSharp.Classification;
using LexiSharp.Core;
using Xunit;

namespace LexiSharp.Tests;

public class CorpusDividerTests
{
    private static readonly SearchDocument[] TwoTopics =
    {
        new("b1", "invoice payment"),
        new("b2", "invoice refund"),
        new("b3", "invoice card"),
        new("b4", "invoice late"),
        new("s1", "delivery parcel"),
        new("s2", "delivery tracking"),
        new("s3", "delivery delay"),
        new("s4", "delivery address"),
    };

    private static readonly SearchDocument[] FourGroups =
    {
        new("a1", "alpha aone"),
        new("a2", "alpha atwo"),
        new("b1", "beta bone"),
        new("b2", "beta btwo"),
        new("c1", "gamma cone"),
        new("c2", "gamma ctwo"),
        new("d1", "delta done"),
        new("d2", "delta dtwo"),
    };

    private static IEnumerable<CorpusCluster> Leaves(DivisionNode node)
    {
        if (node is DivisionLeaf leaf)
        {
            yield return leaf.Cluster;
            yield break;
        }

        var branch = (DivisionBranch)node;

        foreach (var child in Leaves(branch.TrueBranch))
            yield return child;

        foreach (var child in Leaves(branch.FalseBranch))
            yield return child;
    }

    [Fact]
    public void Divide_SeparatesTwoTopicsOnTheMostDiscriminativeTerm()
    {
        var divider = new CorpusDivider(options: new CorpusDividerOptions
        {
            MaxDepth = 1,
            MinDocumentsPerLeaf = 1,
            MinDocumentFrequency = 1,
            MinSeparation = 0,
        });

        var division = divider.Divide(TwoTopics);

        var root = Assert.IsType<DivisionBranch>(division.Root);
        Assert.Equal(new FeaturePresenceQuestion("delivery"), root.Question);
        Assert.Equal(8, division.DocumentCount);
        Assert.Equal(2, division.Clusters.Count);

        var shipping = Assert.Single(division.Clusters, cluster => cluster.DocumentIds.Contains("s1"));
        var billing = Assert.Single(division.Clusters, cluster => cluster.DocumentIds.Contains("b1"));

        Assert.Equal(new[] { "s1", "s2", "s3", "s4" }, shipping.DocumentIds);
        Assert.Equal(new[] { "b1", "b2", "b3", "b4" }, billing.DocumentIds);
    }

    [Fact]
    public void Divide_RecursesUntilEachGroupIsIsolated()
    {
        var divider = new CorpusDivider(options: new CorpusDividerOptions
        {
            MaxDepth = 3,
            MinDocumentsPerLeaf = 2,
            MinDocumentFrequency = 1,
            MinSeparation = 0.001,
            MaxCandidateTerms = 100,
            MaxVocabularyTerms = 100,
        });

        var division = divider.Divide(FourGroups);

        Assert.Equal(4, division.Clusters.Count);
        Assert.All(division.Clusters, cluster => Assert.Equal(2, cluster.DocumentIds.Count));

        var ids = division.Clusters.SelectMany(cluster => cluster.DocumentIds).OrderBy(id => id, StringComparer.Ordinal);
        Assert.Equal(FourGroups.Select(document => document.Id), ids);
    }

    [Fact]
    public void Divide_WithMaxDepthZero_ReturnsOneCluster()
    {
        var divider = new CorpusDivider(options: new CorpusDividerOptions { MaxDepth = 0 });

        var division = divider.Divide(TwoTopics);

        var cluster = Assert.Single(division.Clusters);
        Assert.Equal(8, cluster.DocumentIds.Count);
        Assert.Equal(0, cluster.Depth);
        Assert.IsType<DivisionLeaf>(division.Root);
    }

    [Fact]
    public void Divide_WhenNoSplitReachesMinSeparation_ReturnsOneCluster()
    {
        var divider = new CorpusDivider(options: new CorpusDividerOptions
        {
            MaxDepth = 4,
            MinSeparation = 1.0,
        });

        var division = divider.Divide(TwoTopics);

        Assert.Single(division.Clusters);
    }

    [Fact]
    public void Divide_RespectsMinDocumentsPerLeaf()
    {
        var divider = new CorpusDivider(options: new CorpusDividerOptions
        {
            MaxDepth = 3,
            MinDocumentsPerLeaf = 2,
            MinDocumentFrequency = 1,
            MinSeparation = 0.001,
            MaxCandidateTerms = 100,
            MaxVocabularyTerms = 100,
        });

        var division = divider.Divide(FourGroups);

        Assert.All(Leaves(division.Root!), cluster => Assert.True(cluster.DocumentIds.Count >= 2));
    }

    [Fact]
    public void Divide_EmptyCorpus_ReturnsAnEmptyDivision()
    {
        var division = new CorpusDivider().Divide(Array.Empty<SearchDocument>());

        Assert.Equal(0, division.DocumentCount);
        Assert.Null(division.Root);
        Assert.Empty(division.Clusters);
    }

    [Fact]
    public void Divide_IsDeterministic()
    {
        var divider = new CorpusDivider(options: new CorpusDividerOptions
        {
            MaxDepth = 2,
            MinDocumentsPerLeaf = 1,
            MinDocumentFrequency = 1,
            MinSeparation = 0.001,
            MaxCandidateTerms = 100,
            MaxVocabularyTerms = 100,
        });

        Assert.Equal(divider.Divide(TwoTopics).Describe(), divider.Divide(TwoTopics).Describe());
    }

    [Fact]
    public void Divide_TopTermsReflectEachCluster()
    {
        var divider = new CorpusDivider(options: new CorpusDividerOptions
        {
            MaxDepth = 1,
            MinDocumentsPerLeaf = 1,
            MinDocumentFrequency = 1,
            MinSeparation = 0,
        });

        var division = divider.Divide(TwoTopics);

        var shipping = Assert.Single(division.Clusters, cluster => cluster.DocumentIds.Contains("s1"));
        var billing = Assert.Single(division.Clusters, cluster => cluster.DocumentIds.Contains("b1"));

        Assert.Contains("delivery", shipping.TopFeatures);
        Assert.Contains("invoice", billing.TopFeatures);
    }

    [Fact]
    public void Describe_RendersQuestionsAndClusterTerms()
    {
        var divider = new CorpusDivider(options: new CorpusDividerOptions
        {
            MaxDepth = 1,
            MinDocumentsPerLeaf = 1,
            MinDocumentFrequency = 1,
            MinSeparation = 0,
        });

        var text = divider.Divide(TwoTopics).Describe();

        Assert.Contains("contains \"delivery\"", text, StringComparison.Ordinal);
        Assert.Contains("invoice", text, StringComparison.Ordinal);
        Assert.Contains("delivery", text, StringComparison.Ordinal);
    }
}
