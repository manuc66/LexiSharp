using LexiSharp.Core;
using LexiSharp.Indexing;
using LexiSharp.Ranking;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// Covers the length-ratio component: the arithmetic, and the three places it has to degrade to
/// zero instead of producing something an engine would reject.
/// </summary>
/// <remarks>
/// The corpus is built so the average is a number a reader can check by hand — two documents of two
/// and four tokens give an average of three, and the two ratios are then two thirds and four thirds.
/// Anything cleverer here would make a failing assertion harder to read than the code it tests.
/// </remarks>
public class DocumentLengthRatioScorerTests
{
    private static InMemoryTextIndex CreateTwoDocumentCorpus()
    {
        var index = new InMemoryTextIndex();

        index.Add(new SearchDocument("short", "alpha beta"));
        index.Add(new SearchDocument("long", "alpha beta gamma delta"));

        return index;
    }

    [Fact]
    public void Score_ReturnsTheDocumentLengthOverTheCorpusAverage()
    {
        var index = CreateTwoDocumentCorpus();
        var scorer = new DocumentLengthRatioScorer();
        string[] query = ["alpha"];

        Assert.Equal(3, index.AverageDocumentLength);
        Assert.Equal(2d / 3d, scorer.Score("short", query, index));
        Assert.Equal(4d / 3d, scorer.Score("long", query, index));
    }

    [Fact]
    public void Score_DoesNotReadTheQuery()
    {
        var index = CreateTwoDocumentCorpus();
        var scorer = new DocumentLengthRatioScorer();

        double matching = scorer.Score("short", ["alpha"], index);
        double unrelated = scorer.Score("short", ["zebra", "marmot"], index);

        Assert.Equal(matching, unrelated);
    }

    [Fact]
    public void Score_OnACorpusHoldingNoTokens_ReturnsZero()
    {
        var index = new InMemoryTextIndex();
        index.Add(new SearchDocument("empty", ""));

        var scorer = new DocumentLengthRatioScorer();
        double score = scorer.Score("empty", ["alpha"], index);

        // Not NaN and not infinity: an engine rejects both as not-a-score, which would take the
        // whole search down for a component that had nothing to say.
        Assert.Equal(0, score);
    }

    [Fact]
    public void Score_OnADocumentTheIndexDoesNotHold_ReturnsZero()
    {
        var index = CreateTwoDocumentCorpus();
        var scorer = new DocumentLengthRatioScorer();

        // The index answers 0 for an unknown id, so the ratio is 0 rather than a division by an
        // absent length.
        Assert.Equal(0, index.DocumentLength("absent"));
        Assert.Equal(0, scorer.Score("absent", ["alpha"], index));
    }

    [Fact]
    public void Score_WithoutItsArguments_Throws()
    {
        var index = CreateTwoDocumentCorpus();
        var scorer = new DocumentLengthRatioScorer();

        Assert.Throws<ArgumentNullException>(() => scorer.Score("short", ["alpha"], null!));
        Assert.Throws<ArgumentNullException>(() => scorer.Score("short", null!, index));
    }

    [Fact]
    public void Name_IdentifiesTheComponent()
    {
        Assert.Equal("LengthRatio", new DocumentLengthRatioScorer().Name);
    }
}