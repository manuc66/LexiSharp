using LexiSharp.Core;
using LexiSharp.Indexing;
using LexiSharp.Keywords;
using LexiSharp.Linguistics;
using Xunit;

namespace LexiSharp.Tests;

public class KeywordExtractionTests
{
    private static readonly Tokenizer StopWordTokenizer = new(new TokenizerOptions { RemoveStopWords = true });

    private static TfIdfKeywordExtractor CreateExtractor(ITextIndex? corpus = null) =>
        new(corpus, StopWordTokenizer);

    private const string Text = "search engines rank documents; the engine ranks the documents. vector search scores the search.";

    [Fact]
    public void TfIdf_MostFrequentTermWins()
    {
        var extractor = CreateExtractor();

        var keywords = extractor.Extract(Text, topN: 3);

        // "search" appears 3 times, more than any other term.
        Assert.Equal("search", keywords[0].Term);
        Assert.True(keywords[0].Score > keywords[1].Score);
    }

    [Fact]
    public void TfIdf_WithoutCorpus_ScoreIsTermFrequency()
    {
        var extractor = CreateExtractor();

        var keywords = extractor.Extract("apple apple pie", topN: 5);

        Assert.Equal(2, keywords.First(k => k.Term == "apple").Score, 6);
        Assert.Equal(1, keywords.First(k => k.Term == "pie").Score, 6);
    }

    [Fact]
    public void TfIdf_WithCorpus_DemotesCorpusFrequentTerms()
    {
        // Corpus where "apple" is everywhere and "quasar" is unique.
        var corpus = new InMemoryTextIndex();
        corpus.Index(new[]
        {
            new SearchDocument("1", "apple apple apple"),
            new SearchDocument("2", "apple banana"),
            new SearchDocument("3", "apple cherry quasar"),
        });
        var extractor = CreateExtractor(corpus);

        var keywords = extractor.Extract("apple quasar", topN: 2);

        // "apple" repeats but is corpus-common; "quasar" is corpus-rare: it wins despite tf = 1.
        Assert.Equal(new[] { "quasar", "apple" }, keywords.Select(k => k.Term).ToArray());
    }

    [Fact]
    public void TfIdf_TopNTruncates_AndEmptyTextYieldsEmpty()
    {
        var extractor = CreateExtractor();

        Assert.Equal(2, extractor.Extract(Text, topN: 2).Count);
        Assert.Empty(extractor.Extract("!!! ???", topN: 5));
        Assert.Empty(extractor.Extract("the of and", topN: 5)); // all stop words
    }

    [Fact]
    public void TfIdf_IsDeterministic()
    {
        var extractor = CreateExtractor();

        var first = extractor.Extract(Text, topN: 5);
        var second = extractor.Extract(Text, topN: 5);

        Assert.Equal(first, second);
    }

    [Fact]
    public void TextRank_HubTermWinsAndNeighboursFollow()
    {
        var extractor = new TextRankKeywordExtractor(StopWordTokenizer);

        var keywords = extractor.Extract("apple banana apple cherry apple date apple", topN: 3);

        // "apple" co-occurs with everything; it must rank first, whatever the exact scores.
        Assert.Equal("apple", keywords[0].Term);
        Assert.Equal(3, keywords.Count);
    }

    [Fact]
    public void TextRank_SingleTermText()
    {
        var extractor = new TextRankKeywordExtractor(StopWordTokenizer);

        var keywords = extractor.Extract("isolation", topN: 5);

        Assert.Single(keywords, k => k.Term == "isolation");
        Assert.True(keywords[0].Score > 0);
    }

    [Fact]
    public void TextRank_IsDeterministic()
    {
        var extractor = new TextRankKeywordExtractor(StopWordTokenizer);

        var first = extractor.Extract(Text, topN: 5);
        var second = extractor.Extract(Text, topN: 5);

        Assert.Equal(first, second);
    }

    [Fact]
    public void TextRank_ScoresArePositiveAndOrdered()
    {
        var extractor = new TextRankKeywordExtractor(StopWordTokenizer);

        var keywords = extractor.Extract(Text, topN: 5);

        Assert.All(keywords, k => Assert.True(k.Score > 0));
        Assert.Equal(keywords.Select(k => k.Score).OrderByDescending(x => x).ToList(),
            keywords.Select(k => k.Score).ToList());
    }

    [Fact]
    public void TextRank_RejectsTooSmallWindow()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TextRankKeywordExtractor(windowSize: 1));
    }

    [Fact]
    public void Extractors_RejectNullTextAndBadTopN()
    {
        Assert.Throws<ArgumentNullException>(() => CreateExtractor().Extract(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateExtractor().Extract("text", topN: 0));
        Assert.Throws<ArgumentNullException>(() => new TextRankKeywordExtractor().Extract(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TextRankKeywordExtractor().Extract("text", topN: 0));
    }
}
