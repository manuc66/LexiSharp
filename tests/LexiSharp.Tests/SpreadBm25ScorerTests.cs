using LexiSharp.Core;
using LexiSharp.Indexing;
using LexiSharp.Ranking;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// Covers <see cref="SpreadBm25Scorer"/>. The property that pins the transcription is the α = 0
/// degeneration: with the raw count, the scorer must reproduce <see cref="Bm25Scorer"/> to the bit.
/// The property that makes it a different model is the one the hypothesis is about — at equal length
/// and equal raw frequency, a term spread across windows must outrank the same term repeated inside
/// one.
/// </summary>
public class SpreadBm25ScorerTests
{
    private static InMemoryTextIndex Index(params (string Id, string Text)[] documents)
    {
        var index = new InMemoryTextIndex();
        index.Index(documents.Select(document => new SearchDocument(document.Id, document.Text)));
        return index;
    }

    private static InMemoryTextIndex Corpus() =>
        Index(
            ("1", "the quick brown fox jumps over the lazy dog"),
            ("2", "quick brown foxes are quick and brown and quick"),
            ("3", "a lazy dog sleeps all day long in the sun"),
            ("4", "quick thinking saved the day for the whole team"),
            ("5", "nothing in this document relates to the query at all"));

    [Theory]
    [InlineData(1.5, 0.75)]
    [InlineData(0.9, 0.4)]
    [InlineData(2.0, 0.0)]
    [InlineData(0.0, 1.0)]
    public void AtAlphaZeroIsExactlyBm25(double k1, double b)
    {
        var index = Corpus();
        var spread = new SpreadBm25Scorer(k1, b, windowSize: 8, alpha: 0);
        var bm25 = new Bm25Scorer(k1, b);

        foreach (string query in new[] { "quick", "quick brown", "lazy dog", "nothing here" })
        {
            var terms = index.Tokenizer.Tokenize(query);

            foreach (var document in index.Documents)
            {
                Assert.Equal(
                    bm25.Score(document.Id, terms, index),
                    spread.Score(document.Id, terms, index));
            }
        }
    }

    [Fact]
    public void AtAlphaOneASpreadTermOutranksAContiguousOne()
    {
        // Same length? No: 8 and 9 tokens, so the length term is removed with b = 0 to isolate the
        // statistic. Both documents hold the term twice.
        var index = Index(
            ("contiguous", "alpha alpha bravo charlie delta echo foxtrot golf"),
            ("spread", "alpha bravo charlie delta echo foxtrot golf hotel alpha"));

        var spread = new SpreadBm25Scorer(1.5, b: 0, windowSize: 8, alpha: 1);
        var raw = new SpreadBm25Scorer(1.5, b: 0, windowSize: 8, alpha: 0);

        Assert.True(spread.Score("spread", ["alpha"], index) > spread.Score("contiguous", ["alpha"], index));

        // The raw-count control has nothing to distinguish them: same frequency, same idf, b = 0.
        Assert.Equal(
            raw.Score("contiguous", ["alpha"], index),
            raw.Score("spread", ["alpha"], index));
    }

    [Fact]
    public void SpreadCountsDistinctWindows()
    {
        // Positions 0 and 8 fall in windows 0 and 1 at width 8.
        var index = Index(("1", "alpha bravo charlie delta echo foxtrot golf hotel alpha"));
        var scorer = new SpreadBm25Scorer(1.5, b: 0.75, windowSize: 8, alpha: 1);

        var explanation = scorer.Explain("1", ["alpha"], index);

        Assert.Equal(2, explanation.Parameters["spread:alpha"]);
        Assert.Equal(2, explanation.Terms.Single().TermFrequency);
    }

    [Fact]
    public void AWindowSizeOfOneMakesTheSpreadEqualTheRawFrequency()
    {
        var index = Index(("1", "alpha alpha bravo charlie"));
        var scorer = new SpreadBm25Scorer(1.5, b: 0.75, windowSize: 1, alpha: 1);

        var explanation = scorer.Explain("1", ["alpha"], index);

        Assert.Equal(2, explanation.Parameters["spread:alpha"]);
        Assert.Equal(index.TermFrequency("1", "alpha"), explanation.Parameters["spread:alpha"]);
    }

    [Fact]
    public void AWindowWiderThanTheDocumentMakesTheSpreadOne()
    {
        var index = Index(("1", "alpha bravo alpha charlie alpha"));
        var scorer = new SpreadBm25Scorer(1.5, b: 0.75, windowSize: 100, alpha: 1);

        Assert.Equal(1, scorer.Explain("1", ["alpha"], index).Parameters["spread:alpha"]);
    }

    [Fact]
    public void ExplainTotalEqualsScore()
    {
        var index = Corpus();
        var scorer = new SpreadBm25Scorer(1.5, b: 0.5, windowSize: 3, alpha: 0.5);

        foreach (string query in new[] { "quick", "quick brown", "lazy dog", "nothing here" })
        {
            var terms = index.Tokenizer.Tokenize(query);

            foreach (var document in index.Documents)
            {
                Assert.Equal(
                    scorer.Score(document.Id, terms, index),
                    scorer.Explain(document.Id, terms, index).TotalScore,
                    12);
            }
        }
    }

    [Fact]
    public void ADocumentWithoutTheTermScoresZero()
    {
        var index = Corpus();
        var scorer = new SpreadBm25Scorer();

        Assert.Equal(0, scorer.Score("5", ["quick"], index));
        Assert.Equal("BM25 spread", scorer.Name);
    }

    [Fact]
    public void AnEmptyIndexScoresZero()
    {
        var index = new InMemoryTextIndex();
        var scorer = new SpreadBm25Scorer();

        Assert.Equal(0, scorer.Score("missing", ["alpha"], index));
        Assert.Equal(0, scorer.Explain("missing", ["alpha"], index).TotalScore);
    }

    [Theory]
    [InlineData(-1, 0.5, 8, 1.0)]
    [InlineData(1.5, -0.1, 8, 1.0)]
    [InlineData(1.5, 0.5, 0, 1.0)]
    [InlineData(1.5, 0.5, 8, -0.1)]
    [InlineData(1.5, 0.5, 8, 1.1)]
    public void RejectsParametersOutsideTheirDomain(double k1, double b, int windowSize, double alpha)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SpreadBm25Scorer(k1, b, windowSize, alpha));
    }
}
