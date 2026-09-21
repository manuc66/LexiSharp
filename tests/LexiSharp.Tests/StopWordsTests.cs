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
    public void Create_BuildsAnOrdinalSet()
    {
        var set = StopWords.Create("foo", "bar");

        Assert.True(set.Contains("foo"));
        Assert.True(set.Contains("bar"));
        Assert.False(set.Contains("baz"));
        Assert.False(set.Contains("FOO"));
    }
}
