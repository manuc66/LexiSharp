using LexiSharp.Core;
using LexiSharp.Indexing;
using Xunit;

namespace LexiSharp.Tests;

public class TextIndexStatisticsTests
{
    [Fact]
    public void Statistics_MatchTheCorpusFigures()
    {
        var index = new InMemoryTextIndex();
        index.Index(new[]
        {
            new SearchDocument("1", "alpha alpha beta"),
            new SearchDocument("2", "beta gamma"),
        });

        var statistics = index.GetStatistics();

        Assert.Equal(2, statistics.DocumentCount);
        Assert.Equal(3, statistics.TermCount); // alpha, beta, gamma
        Assert.Equal(5, statistics.TokenCount);
        Assert.Equal(2.5, statistics.AverageDocumentLength, 12);
        Assert.Equal(1.5, statistics.VocabularyRichness, 12); // 3 distinct terms / 2 documents
    }

    [Fact]
    public void Statistics_EmptyIndex_IsAllZeros()
    {
        var statistics = new InMemoryTextIndex().GetStatistics();

        Assert.Equal(0, statistics.DocumentCount);
        Assert.Equal(0, statistics.TermCount);
        Assert.Equal(0, statistics.TokenCount);
        Assert.Equal(0, statistics.AverageDocumentLength);
        Assert.Equal(0, statistics.VocabularyRichness);
    }
}