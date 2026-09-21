using LexiSharp.Core;
using LexiSharp.Hybrid;
using LexiSharp.Ranking;
using Xunit;

namespace LexiSharp.Tests;

public class RerankingResultMergerTests
{
    private static SearchResult R(string id, string text, double score) =>
        new(id, score, new SearchDocument(id, text));

    [Fact]
    public void Merge_ReScoresUnionAgainstSharedIndex()
    {
        var engineA = new[]
        {
            R("a", "search engine is fast and reliable", 5.0),
            R("b", "search is a really classic technique", 3.0),
        };
        var engineB = new[]
        {
            R("b", "search is a really classic technique", 9.0),
            R("c", "vectors power modern search", 1.0),
        };

        var merged = new RerankingResultMerger()
            .Merge(new[] { engineA, engineB }, "search engine").ToList();

        // Every candidate is re-scored with BM25 against the union: "a" matches both query
        // terms so it must come first; between the single-term matches, "c" (4 tokens)
        // outranks "b" (5 tokens, "a" dropped as a single-character term) because of the
        // length normalization.
        Assert.Equal(new[] { "a", "c", "b" }, merged.Select(r => r.DocumentId).ToArray());
        Assert.All(merged, r => Assert.True(r.Score > 0));
    }

    [Fact]
    public void Merge_DuplicateDocument_UsesFirstOccurrence()
    {
        var engineA = new[] { R("b", "alpha beta", 5.0) };
        var engineB = new[] { R("b", "gamma", 9.0) };

        var merger = new RerankingResultMerger();
        var hit = merger.Merge(new[] { engineA, engineB }, "alpha").Single();
        Assert.Empty(merger.Merge(new[] { engineA, engineB }, "gamma"));

        // The union keeps engine A's version of "b", so only "alpha" can match.
        Assert.Equal("alpha beta", hit.Document.Text);
    }

    [Fact]
    public void Merge_DocumentsWithoutQueryTermsAreDropped()
    {
        var engineA = new[] { R("a", "apple pie recipe", 4.0) };
        var engineB = new[] { R("z", "zebra stripe pattern", 4.0) };

        var merged = new RerankingResultMerger()
            .Merge(new[] { engineA, engineB }, "apple pie");

        var only = Assert.Single(merged);
        Assert.Equal("a", only.DocumentId);
    }

    [Fact]
    public void Merge_QueryWithoutTerms_ReturnsEmpty()
    {
        var engineA = new[] { R("a", "anything at all", 1.0) };

        var merged = new RerankingResultMerger()
            .Merge(new[] { engineA }, "!!! ??? ...");

        Assert.Empty(merged);
    }

    [Fact]
    public void Merge_EmptyLists_ReturnsEmpty() =>
        Assert.Empty(new RerankingResultMerger().Merge(new[] { Array.Empty<SearchResult>() }, "query"));

    [Fact]
    public void Merge_HonorsInjectedScorer()
    {
        var engineA = new[] { R("a", "search engine", 1.0) };
        var engineB = new[] { R("b", "search", 2.0) };

        var merged = new RerankingResultMerger(scorer: new BooleanScorer(BooleanMatch.AllTerms))
            .Merge(new[] { engineA, engineB }, "search engine");

        // Only "a" contains every query term.
        var only = Assert.Single(merged);
        Assert.Equal("a", only.DocumentId);
    }

    [Fact]
    public void Name_IsRerank() =>
        Assert.Equal("Rerank", new RerankingResultMerger().Name);

    [Fact]
    public void Merge_ValidatesArguments()
    {
        var merger = new RerankingResultMerger();
        var lists = new[] { new[] { R("a", "text", 1.0) } };

        Assert.Throws<ArgumentNullException>(() => merger.Merge(null!, "query"));
        Assert.Throws<ArgumentNullException>(() => merger.Merge(lists, null!));
    }
}
