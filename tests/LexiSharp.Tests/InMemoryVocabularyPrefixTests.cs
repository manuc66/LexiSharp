using LexiSharp.Core;
using LexiSharp.Indexing;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// The ordered prefix range the in-memory vocabulary now answers, and its invalidation.
/// </summary>
/// <remarks>
/// The resolver's fallback walks every term; this capability is what makes the walk a binary search.
/// The tests pin the two properties the fallback cannot provide: the range is contiguous and sorted,
/// and a write between two prefix queries is visible to the second.
/// </remarks>
public class InMemoryVocabularyPrefixTests
{
    private static InMemoryTextIndex IndexOf(params string[] terms)
    {
        var index = new InMemoryTextIndex();

        foreach (string term in terms)
            index.Add(new SearchDocument(term, term));

        return index;
    }

    private static string[] Prefix(InMemoryTextIndex index, string prefix) =>
        ((IVocabularyPrefixIndex)index).TermsWithPrefix(prefix).ToArray();

    [Fact]
    public void TermsWithPrefix_ReturnsTheSortedContiguousRange()
    {
        var index = IndexOf("cart", "dog", "car", "carbon", "cat", "carpet");

        Assert.Equal(["car", "carbon", "carpet", "cart"], Prefix(index, "car"));
        Assert.Equal(["cat"], Prefix(index, "cat"));
        Assert.Empty(Prefix(index, "zz"));
    }

    [Fact]
    public void AnEmptyPrefix_ReturnsTheWholeVocabularyInOrder()
    {
        var index = IndexOf("cart", "car", "dog");

        Assert.Equal(["car", "cart", "dog"], Prefix(index, ""));
    }

    [Fact]
    public void AWriteBetweenTwoPrefixQueries_IsVisiblyReindexed()
    {
        var index = IndexOf("car", "cart");

        Assert.Equal(["car", "cart"], Prefix(index, "car"));

        index.Add(new SearchDocument("carpenter", "carpenter"));
        Assert.Equal(["car", "carpenter", "cart"], Prefix(index, "car"));

        index.Remove("carpenter");
        Assert.Equal(["car", "cart"], Prefix(index, "car"));
    }

    /// <summary>
    /// <see cref="InMemoryTextIndex.AddExpansionTerms"/> posts a term without tokenizing a new
    /// document, and the vocabulary is what a prefix or a fuzzy atom is resolved against. A term it
    /// posts is a term of the index like any other, so it has to be in the ordered vocabulary too —
    /// otherwise the exact term matches and neither expansion can reach it.
    /// </summary>
    [Fact]
    public void TermsInjectedByAddExpansionTerms_ArePartOfTheVocabulary()
    {
        var index = new InMemoryTextIndex();
        index.Add(new SearchDocument("x", "the oauth token expiry loop"));
        index.AddExpansionTerms("x", ["refreshment"]);

        Assert.Contains("refreshment", index.Vocabulary);
        Assert.Equal(index.VocabularySize, index.Vocabulary.Count());
        Assert.Equal(["refreshment"], Prefix(index, "refre"));
    }

    /// <summary>
    /// The injected term leaves with the document, on the same path a literal term takes: the
    /// posting list empties, the term is dropped from the map, and the ordered vocabulary has to
    /// follow.
    /// </summary>
    [Fact]
    public void RemovingADocument_DropsItsExpansionTermsFromTheVocabulary()
    {
        var index = new InMemoryTextIndex();
        index.Add(new SearchDocument("x", "the oauth token expiry loop"));
        index.AddExpansionTerms("x", ["refreshment"]);

        index.Remove("x");

        Assert.DoesNotContain("refreshment", index.Vocabulary);
        Assert.Empty(Prefix(index, "refre"));
        Assert.Equal(index.VocabularySize, index.Vocabulary.Count());
    }
}
