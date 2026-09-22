using LexiSharp.Core;
using LexiSharp.Indexing;
using Xunit;

namespace LexiSharp.Tests;

public class InMemoryTextIndexTests
{
    private static InMemoryTextIndex Create(params (string Id, string Text)[] docs)
    {
        var index = new InMemoryTextIndex();
        index.Index(docs.Select(d => new SearchDocument(d.Id, d.Text)));
        return index;
    }

    private static readonly string[] RedBlueRedTerms = new[] { "red", "blue", "red" };
    private static readonly int[] RedPositions = new[] { 0, 2 };
    private static readonly int[] BluePositions = new[] { 1 };

    [Fact]
    public void Add_TracksTermAndDocumentFrequency()
    {
        var index = Create(
            ("1", "red blue red"),
            ("2", "red green"));

        Assert.Equal(2, index.Count);
        Assert.Equal(2, index.DocumentFrequency("red"));
        Assert.Equal(1, index.DocumentFrequency("blue"));
        Assert.Equal(0, index.DocumentFrequency("missing"));

        Assert.Equal(2, index.TermFrequency("1", "red"));
        Assert.Equal(1, index.TermFrequency("1", "blue"));
        Assert.Equal(1, index.TermFrequency("2", "red"));
        Assert.Equal(0, index.TermFrequency("2", "blue"));
        Assert.Equal(0, index.TermFrequency("nope", "red"));
    }

    [Fact]
    public void Add_TracksDocumentLengthsAndAverage()
    {
        var index = Create(
            ("1", "red blue green"),
            ("2", "red blue"));

        Assert.Equal(3, index.DocumentLength("1"));
        Assert.Equal(2, index.DocumentLength("2"));
        Assert.Equal(2.5, index.AverageDocumentLength, precision: 10);
    }

    [Fact]
    public void GetTerms_ReturnsTokensInDocumentOrder()
    {
        var index = Create(("1", "red blue red"));

        Assert.Equal(RedBlueRedTerms, index.GetTerms("1"));
    }

    [Fact]
    public void GetTermPositions_ReturnsZeroBasedPositions()
    {
        var index = Create(("1", "red blue red"));

        Assert.Equal(RedPositions, index.GetTermPositions("1", "red"));
        Assert.Equal(BluePositions, index.GetTermPositions("1", "blue"));
        Assert.Empty(index.GetTermPositions("1", "green"));
    }

    [Fact]
    public void CorpusStatistics_AreAccurate()
    {
        var index = Create(
            ("1", "red red blue"),
            ("2", "green blue"));

        Assert.Equal(2, index.CorpusFrequency("red"));
        Assert.Equal(2, index.CorpusFrequency("blue"));
        Assert.Equal(1, index.CorpusFrequency("green"));
        Assert.Equal(5, index.CorpusTokenCount);
        Assert.Equal(3, index.VocabularySize);
    }

    [Fact]
    public void Replace_BySameId_UpdatesDocument()
    {
        var index = new InMemoryTextIndex();
        index.Add(new SearchDocument("1", "red"));
        index.Add(new SearchDocument("1", "blue"));

        Assert.Equal(1, index.Count);
        Assert.Equal(0, index.DocumentFrequency("red"));
        Assert.Equal(1, index.DocumentFrequency("blue"));
        Assert.True(index.TryGetDocument("1", out var doc));
        Assert.Equal("blue", doc!.Text);
    }

    [Fact]
    public void Remove_DeletesPostingsAndStatistics()
    {
        var index = Create(
            ("1", "red blue"),
            ("2", "red"));

        Assert.True(index.Remove("1"));
        Assert.False(index.Remove("999"));

        Assert.Equal(1, index.Count);
        Assert.Equal(1, index.DocumentFrequency("red"));
        Assert.Equal(0, index.DocumentFrequency("blue"));
        Assert.Equal(1, index.CorpusFrequency("red"));
        Assert.Equal(1, index.CorpusTokenCount);
    }

    [Fact]
    public void Index_ReplacesAllPreviousContent()
    {
        var index = Create(("1", "red"));

        index.Index(new[] { new SearchDocument("9", "green") });

        Assert.Equal(1, index.Count);
        Assert.False(index.Contains("1"));
        Assert.True(index.Contains("9"));
    }

    [Fact]
    public void Clear_ResetsEverything()
    {
        var index = Create(("1", "red"));

        index.Clear();

        Assert.Equal(0, index.Count);
        Assert.Equal(0, index.AverageDocumentLength);
        Assert.Equal(0, index.CorpusTokenCount);
        Assert.Equal(0, index.VocabularySize);
        Assert.Empty(index.Documents);
    }

    [Fact]
    public void Churn_AddThenRemove_VocabularyReturnsToBaseline()
    {
        var index = new InMemoryTextIndex();

        index.Add(new SearchDocument("keep", "alpha bravo"));

        for (int cycle = 0; cycle < 50; cycle++)
        {
            index.Add(new SearchDocument("temp", $"term{cycle} other{cycle}"));
            Assert.True(index.Remove("temp"));
        }

        // Every term introduced by a removed document must leave the vocabulary with it:
        // an implementation that never evicts terms would report a growing VocabularySize here.
        Assert.Equal(2, index.VocabularySize);
        Assert.Equal(1, index.Count);
        Assert.Equal(2, index.CorpusTokenCount);
        Assert.Equal(0, index.CorpusFrequency("term0"));
        Assert.Empty(index.GetTermPositions("keep", "term0"));
    }

    [Fact]
    public void Churn_RepeatedReplace_KeepsVocabularyBounded()
    {
        var index = new InMemoryTextIndex();

        for (int cycle = 0; cycle < 200; cycle++)
            index.Add(new SearchDocument("doc", $"unique{cycle}"));

        // Replacing the same id must not accumulate the terms of previous revisions.
        Assert.Equal(1, index.Count);
        Assert.Equal(1, index.VocabularySize);
        Assert.Equal(1, index.CorpusTokenCount);
        Assert.Equal(0, index.CorpusFrequency("unique0"));
        Assert.Equal(1, index.CorpusFrequency("unique199"));
    }

    [Fact]
    public void Churn_DoesNotGrowRetainedMemory()
    {
        var index = new InMemoryTextIndex();

        index.Add(new SearchDocument("keep", "alpha bravo charlie"));

        long Baseline()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            return GC.GetTotalMemory(forceFullCollection: true);
        }

        // Warm up the postings structures at their steady state before measuring.
        for (int cycle = 0; cycle < 50; cycle++)
        {
            index.Add(new SearchDocument("temp", $"term{cycle} other{cycle}"));
            index.Remove("temp");
        }

        long before = Baseline();

        for (int cycle = 0; cycle < 5000; cycle++)
        {
            index.Add(new SearchDocument("temp", $"term{cycle} other{cycle}"));
            index.Remove("temp");
        }

        long after = Baseline();

        // Retained memory must stay flat: allow 1 MB of measurement noise, nothing more.
        Assert.True(
            after - before < 1_000_000,
            $"Retained memory grew by {after - before} bytes across churn.");
    }
}
