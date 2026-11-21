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
}
