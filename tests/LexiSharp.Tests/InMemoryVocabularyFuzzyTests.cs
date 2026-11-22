using LexiSharp.Core;
using LexiSharp.Indexing;
using LexiSharp.Ranking;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// The fuzzy vocabulary range: the same terms the walk would find, with the distance the trie
/// computed, and the trie's visibility of an add/remove after it has been built.
/// </summary>
public class InMemoryVocabularyFuzzyTests
{
    private static InMemoryTextIndex IndexOf(params string[] terms)
    {
        var index = new InMemoryTextIndex();

        foreach (string term in terms)
            index.Add(new SearchDocument(term, term));

        return index;
    }

    private static FuzzyVocabularyMatch[] Within(InMemoryTextIndex index, string term, int edits)
    {
        Assert.True(
            ((IVocabularyFuzzyIndex)index).TryGetTermsWithinEdits(term, edits, out var matches),
            "the trie declined a corpus it should have served");

        return matches
            .OrderBy(match => match.Distance)
            .ThenBy(match => match.Term, StringComparer.Ordinal)
            .ToArray();
    }

    [Fact]
    public void TermsWithinEdits_ReturnsExactlyTheWalkMatches()
    {
        var index = IndexOf("catt", "cat", "cats", "cut", "dog", "catalog");

        var matches = Within(index, "catt", 1);

        Assert.Equal(["catt", "cat", "cats"], matches.Select(match => match.Term));
        Assert.Equal([0, 1, 1], matches.Select(match => match.Distance));
    }

    [Fact]
    public void TheResolver_UsesTheFuzzyRangeAndKeepsItsOrder()
    {
        var index = IndexOf("catt", "cat", "cats", "dog");
        var resolver = new QueryExpansionResolver(index, fuzzyOnlyOutOfVocabulary: false);

        Assert.Equal(
            ["catt", "cat", "cats"],
            resolver.Resolve(new QueryExpansion("catt", QueryExpansionKind.Fuzzy)));
    }

    [Fact]
    public void AnAddAndARemoveAfterTheTrieIsBuilt_AreVisible()
    {
        var index = IndexOf("cat");

        // Builds the trie over the one-term vocabulary.
        Assert.Equal(["cat"], Within(index, "catt", 2).Select(match => match.Term));

        index.Add(new SearchDocument("catt", "catt"));
        Assert.Equal(["catt", "cat"], Within(index, "catt", 1).Select(match => match.Term));

        index.Remove("catt");
        Assert.Equal(["cat"], Within(index, "catt", 1).Select(match => match.Term));

        // The tombstoned terminal is revived rather than duplicated.
        index.Add(new SearchDocument("catt", "catt"));
        Assert.Equal(["catt", "cat"], Within(index, "catt", 1).Select(match => match.Term));
    }

    [Fact]
    public void AnEmptyVocabulary_DeclinesRatherThanReturningNoMatches()
    {
        var index = new InMemoryTextIndex();

        Assert.False(((IVocabularyFuzzyIndex)index).TryGetTermsWithinEdits("catt", 2, out _));
    }

    [Fact]
    public void AVocabularyAboveTheLimit_FallsBackToTheWalkAndStillAnswers()
    {
        var index = new InMemoryTextIndex { FuzzyTrieTermLimit = 2 };
        index.Index(
        [
            new SearchDocument("catt", "catt"),
            new SearchDocument("cat", "cat"),
            new SearchDocument("cats", "cats"),
        ]);

        // The trie declines rather than returning an empty list with true.
        Assert.False(((IVocabularyFuzzyIndex)index).TryGetTermsWithinEdits("catt", 1, out _));

        var resolver = new QueryExpansionResolver(index, fuzzyOnlyOutOfVocabulary: false);

        // The resolver falls back to the walk and keeps the same order and cap.
        Assert.Equal(
            ["catt", "cat", "cats"],
            resolver.Resolve(new QueryExpansion("catt", QueryExpansionKind.Fuzzy)));
    }

    [Fact]
    public void ATrieBuiltBelowTheLimit_KeepsServingTermsAddedLater()
    {
        var index = new InMemoryTextIndex { FuzzyTrieTermLimit = 2 };
        index.Index(
        [
            new SearchDocument("cat", "cat"),
            new SearchDocument("catt", "catt"),
        ]);

        // Builds the trie while the vocabulary is at the limit.
        Assert.Equal(["catt", "cat"], Within(index, "catt", 1).Select(match => match.Term));

        // The vocabulary is now over the limit, but the existing trie stays authoritative.
        index.Add(new SearchDocument("cats", "cats"));

        Assert.Equal(
            ["catt", "cat", "cats"],
            Within(index, "catt", 1).Select(match => match.Term));
    }
}
