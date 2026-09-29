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
    public void LuceneEnglish_HasTheThirtyThreeWordsOfLucenesAnalyzer()
    {
        // org.apache.lucene.analysis.en.EnglishAnalyzer's STOP_WORDS_SET. The count is asserted
        // because a list that silently gained or lost a word would no longer be that analyzer, and
        // an index built from it would no longer be comparable with the published BM25 numbers
        // the set exists for.
        Assert.Equal(33, StopWords.LuceneEnglish.Count);

        foreach (var word in new[] { "a", "an", "and", "the", "with", "will", "not", "such" })
            Assert.Contains(word, StopWords.LuceneEnglish);
    }

    [Fact]
    public void LuceneEnglish_KeepsWordsTheTokenizerWouldOtherwiseDrop()
    {
        // "i" survives Lucene's analyzer and must survive here too, or a one-character term is
        // missing from the index for a reason that has nothing to do with the stop word list.
        Assert.DoesNotContain("i", StopWords.LuceneEnglish);

        // A subset of the larger list, so a caller can reason about the difference in one step.
        Assert.True(StopWords.LuceneEnglish.IsProperSubsetOf(StopWords.English));
    }

    [Fact]
    public void LuceneEnglish_IsNotTheDefault()
    {
        // Changing the default list would invalidate every recorded baseline in this repository.
        Assert.NotSame(StopWords.English, StopWords.LuceneEnglish);
        Assert.True(StopWords.English.Count > StopWords.LuceneEnglish.Count);
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
