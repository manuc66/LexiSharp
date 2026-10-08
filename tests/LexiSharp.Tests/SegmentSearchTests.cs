using System;
using System.Globalization;
using System.Linq;
using LexiSharp.Core;
using LexiSharp.Indexing;
using LexiSharp.Ranking;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// A query over a segment and the same query over the index it was written from must produce the same
/// page — same documents, same order, same score bits.
/// </summary>
/// <remarks>
/// This is what the whole storage direction rests on. The two indexes are different objects holding
/// different representations, and the engine is the same engine neither of them knows about: it asks for
/// <see cref="IReadOnlyTextIndex"/> and gets one from each.
/// </remarks>
public class SegmentSearchTests
{
    private static readonly string[] Queries =
    [
        "search",
        "search engine",
        "index query score",
        "document token term corpus",
        "quokka",                      // absent from the vocabulary entirely
        "zulu whiskey tango",          // absent from this corpus's vocabulary
    ];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AQueryOverASegmentGivesTheSamePageAsTheIndex(bool phrase)
    {
        var (memory, segment) = Engines();

        foreach (string query in Queries)
        {
            var text = phrase ? $"\"{query}\"" : query;

            AssertSamePage(memory.Search(text), segment.Search(text), text);
        }
    }

    [Fact]
    public void AQueryLikelihoodQueryOverASegmentGivesTheSamePage()
    {
        var (memoryIndex, segmentIndex) = Indexes();

        // Query likelihood does not accumulate: it walks candidates and asks the index for corpus
        // frequency, term frequency and document length per document — the whole read surface, one
        // candidate at a time, which a segment answers by scanning its postings and re-tokenizing text.
        var memory = new RankedTextSearchEngine(memoryIndex, new QueryLikelihoodScorer());
        var segment = new RankedTextSearchEngine(segmentIndex, new QueryLikelihoodScorer());

        foreach (string query in Queries)
            AssertSamePage(memory.Search(query), segment.Search(query), query);
    }

    [Fact]
    public void TheSegmentIndexAnswersTheReadSurface()
    {
        var (memoryIndex, segmentIndex) = Indexes();

        Assert.Equal(memoryIndex.Count, segmentIndex.Count);
        Assert.Equal(memoryIndex.VocabularySize, segmentIndex.VocabularySize);
        Assert.Equal(memoryIndex.CorpusTokenCount, segmentIndex.CorpusTokenCount);
        Assert.Equal(memoryIndex.AverageDocumentLength, segmentIndex.AverageDocumentLength);
        Assert.Equal(memoryIndex.Documents.Count, segmentIndex.Documents.Count);

        for (int ordinal = 0; ordinal < memoryIndex.Count; ordinal++)
        {
            var expected = memoryIndex.DocumentAt(ordinal)!;

            Assert.True(segmentIndex.Contains(expected.Id));
            Assert.True(segmentIndex.TryGetDocument(expected.Id, out var actual));
            Assert.Equal(expected.Id, actual!.Id);
            Assert.Equal(expected.Text, actual.Text);
            Assert.Equal(memoryIndex.DocumentLength(expected.Id), segmentIndex.DocumentLength(expected.Id));
            Assert.Equal(memoryIndex.GetTerms(expected.Id), segmentIndex.GetTerms(expected.Id));
        }

        var vocabulary = ((IVocabularyIndex)memoryIndex).Vocabulary.ToArray();

        foreach (string term in vocabulary)
        {
            Assert.Equal(memoryIndex.DocumentFrequency(term), segmentIndex.DocumentFrequency(term));
            Assert.Equal(memoryIndex.CorpusFrequency(term), segmentIndex.CorpusFrequency(term));
        }

        foreach (string term in vocabulary)
        {
            for (int ordinal = 0; ordinal < memoryIndex.Count; ordinal++)
            {
                string id = memoryIndex.DocumentAt(ordinal)!.Id;

                Assert.Equal(memoryIndex.TermFrequency(id, term), segmentIndex.TermFrequency(id, term));
                Assert.Equal(memoryIndex.GetTermPositions(id, term), segmentIndex.GetTermPositions(id, term));
            }
        }

        Assert.False(segmentIndex.Contains("nowhere"));
        Assert.False(segmentIndex.TryGetDocument("nowhere", out _));
        Assert.Equal(0, segmentIndex.DocumentLength("nowhere"));
        Assert.Equal(0, segmentIndex.DocumentFrequency("quokka"));
    }

    private static void AssertSamePage(
        System.Collections.Generic.IReadOnlyList<SearchResult> expected,
        System.Collections.Generic.IReadOnlyList<SearchResult> actual,
        string query)
    {
        Assert.Equal(expected.Count, actual.Count);

        for (int i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i].DocumentId, actual[i].DocumentId);

            // Bit for bit: the two indexes carry the same statistics, and the accumulator sees the same
            // records in the same order, so nothing is allowed to differ in the last bits either.
            Assert.Equal(
                BitConverter.DoubleToInt64Bits(expected[i].Score),
                BitConverter.DoubleToInt64Bits(actual[i].Score));
        }
    }

    private static (RankedTextSearchEngine Memory, RankedTextSearchEngine Segment) Engines()
    {
        var (memory, segment) = Indexes();

        return (new RankedTextSearchEngine(memory, new Bm25Scorer()), new RankedTextSearchEngine(segment, new Bm25Scorer()));
    }

    private static (InMemoryTextIndex Memory, SegmentTextIndex Segment) Indexes()
    {
        var index = new InMemoryTextIndex();
        var random = new Random(11);
        var words = new[] { "search", "engine", "index", "query", "score", "rank", "document", "token", "term", "corpus" };
        var builder = new System.Text.StringBuilder();

        for (int i = 0; i < 150; i++)
        {
            builder.Clear();

            for (int w = 0; w < 14; w++)
                builder.Append(words[random.Next(words.Length)]).Append(' ');

            index.Add(new SearchDocument("doc-" + i.ToString("D4", CultureInfo.InvariantCulture), builder.ToString()));
        }

        return (index, new SegmentTextIndex(SegmentWriter.Write(index)));
    }
}
