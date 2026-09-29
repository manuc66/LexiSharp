using LexiSharp.Linguistics;
using Xunit;

namespace LexiSharp.Tests;

public class StopWordsTests
{
    [Fact]
    public void English_ContainsCommonFunctionWords()
    {
        foreach (var word in new[] { "the", "and", "of", "you", "is", "that" })
            Assert.Contains(word, StopWords.English);
    }

    [Fact]
    public void English_ExcludesContentWords()
    {
        foreach (var word in new[] { "engine", "neural", "retrieval", "kitten" })
            Assert.DoesNotContain(word, StopWords.English);
    }

    [Fact]
    public void English_IsCaseSensitive()
    {
        // The built-in list is an Ordinal set of lowercase words, matching the normalized
        // terms produced by the tokenizer.
        Assert.DoesNotContain("The", StopWords.English);
        Assert.Contains("the", StopWords.English);
    }

    [Fact]
    public void EnglishFunction_HasTheConventionalThirtyThreeWords()
    {
        // The exact contents matter: a list that silently gained or lost a word would produce a
        // different index, and an index built from it would no longer be comparable with the
        // published BM25 measurements the set exists for. The count is the cheap guard.
        Assert.Equal(33, StopWords.EnglishFunction.Count);

        foreach (var word in new[] { "a", "an", "and", "the", "with", "will", "not", "such" })
            Assert.Contains(word, StopWords.EnglishFunction);
    }

    [Fact]
    public void EnglishFunction_KeepsWordsTheTokenizerWouldOtherwiseDrop()
    {
        // "i" survives the conventional English stop list and must survive here too, or a
        // single-character term goes missing from the index for a reason that has nothing to do
        // with stop words.
        Assert.DoesNotContain("i", StopWords.EnglishFunction);

        // A subset of the larger list, so a caller can reason about the difference in one step.
        Assert.True(StopWords.EnglishFunction.IsProperSubsetOf(StopWords.English));
    }

    [Fact]
    public void EnglishFunction_IsNotTheDefault()
    {
        // Changing the default list would invalidate every recorded baseline in this repository.
        Assert.NotSame(StopWords.English, StopWords.EnglishFunction);
        Assert.True(StopWords.English.Count > StopWords.EnglishFunction.Count);
    }

    [Fact]
    public void Create_BuildsAnOrdinalSet()
    {
        var set = StopWords.Create("foo", "bar");

        Assert.True(set.Contains("foo"));
        Assert.True(set.Contains("bar"));
        Assert.False(set.Contains("baz"));
        Assert.False(set.Contains("FOO"));
    }
}
