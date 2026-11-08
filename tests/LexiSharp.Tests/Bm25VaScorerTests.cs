using LexiSharp.Core;
using LexiSharp.Indexing;
using LexiSharp.Ranking;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// Covers <see cref="Bm25VaScorer"/>. The property that pins the transcription to the literature is
/// the degeneration check: at <c>b = 1</c> the verboseness term carries a zero weight and the
/// scorer must reproduce <see cref="Bm25Scorer"/> to the bit. The property that distinguishes it
/// from BM25 is not the size of a score but <i>which statistic moves it</i>: at <c>b = 0</c>,
/// document length must stop mattering and verboseness must keep mattering.
/// </summary>
public class Bm25VaScorerTests
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

    // ---- the degeneration that pins the formula ------------------------------------------------

    [Theory]
    [InlineData(1.5)]
    [InlineData(0.9)]
    [InlineData(2.0)]
    [InlineData(0.0)]
    public void AtBOneIsExactlyBm25(double k1)
    {
        var index = Corpus();
        var va = new Bm25VaScorer(k1, b: 1.0);
        var bm25 = new Bm25Scorer(k1, b: 1.0);

        foreach (string query in new[] { "quick", "quick brown", "lazy dog", "nothing here" })
        {
            var terms = index.Tokenizer.Tokenize(query);

            foreach (var document in index.Documents)
            {
                Assert.Equal(
                    bm25.Score(document.Id, terms, index),
                    va.Score(document.Id, terms, index));
            }
        }
    }

    // ---- what the two components actually do ---------------------------------------------------

    [Fact]
    public void AtBZeroDocumentLengthStopsMattering()
    {
        // Equal verboseness (4/3), different lengths, and the query term occurs once in each.
        // At b = 0 the normalization is verboseness alone, so the two documents must tie exactly.
        var index = Index(
            ("short", "alpha alpha beta gamma"),
            ("long", "alpha alpha alpha beta gamma delta epsilon zeta"));

        var terms = new[] { "beta" };
        var scorer = new Bm25VaScorer(1.5, b: 0.0);

        Assert.Equal(
            scorer.Score("short", terms, index),
            scorer.Score("long", terms, index));
    }

    [Fact]
    public void AtBOneDocumentLengthMattersAgain()
    {
        var index = Index(
            ("short", "alpha alpha beta gamma"),
            ("long", "alpha alpha alpha beta gamma delta epsilon zeta"));

        var terms = new[] { "beta" };
        var scorer = new Bm25VaScorer(1.5, b: 1.0);

        Assert.True(
            scorer.Score("short", terms, index) > scorer.Score("long", terms, index),
            "at b = 1 the shorter document must outrank the longer one at equal frequency");
    }

    [Fact]
    public void AtBZeroAVerboseDocumentScoresBelowASpreadOneAtEqualLengthAndFrequency()
    {
        // Same length (6 tokens), same frequency for the query term, different verboseness:
        // 6/4 = 1.5 for the repetitive document, 6/6 = 1.0 for the varied one.
        var index = Index(
            ("verbose", "alpha alpha alpha beta gamma delta"),
            ("spread", "alpha beta gamma delta epsilon zeta"));

        var terms = new[] { "beta" };
        var scorer = new Bm25VaScorer(1.5, b: 0.0);

        Assert.True(
            scorer.Score("verbose", terms, index) < scorer.Score("spread", terms, index),
            "a document that repeats its vocabulary must be damped at b = 0");
    }

    // ---- the explanation is the formula, not a second one --------------------------------------

    [Fact]
    public void ExplainReportsTheTwoComponentNormalization()
    {
        var index = Corpus();
        var scorer = new Bm25VaScorer(1.5, b: 0.4);
        var terms = index.Tokenizer.Tokenize("quick brown");

        var explanation = scorer.Explain("1", terms, index);

        double verboseness = index.DocumentLength("1") / (double)index.GetTerms("1").Distinct(StringComparer.Ordinal).Count();
        double averageVerboseness = AverageVerboseness(index);
        double expected = (1.0 - 0.4) * (verboseness / averageVerboseness)
            + 0.4 * (index.DocumentLength("1") / index.AverageDocumentLength);

        Assert.Equal(verboseness, explanation.Parameters["verboseness"], 12);
        Assert.Equal(averageVerboseness, explanation.Parameters["averageVerboseness"], 12);
        Assert.Equal(expected, explanation.LengthNormalization, 12);
    }

    [Fact]
    public void ExplainTotalEqualsScore()
    {
        var index = Corpus();
        var scorer = new Bm25VaScorer(1.5, b: 0.5);

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

    // ---- the corpus statistic's lifecycle ------------------------------------------------------

    [Fact]
    public void AverageVerbosenessIsRebuiltWhenADocumentIsAdded()
    {
        var index = Index(
            ("1", "alpha beta gamma delta"),
            ("2", "alpha alpha alpha beta"));

        var scorer = new Bm25VaScorer(1.5, b: 0.0);
        var terms = new[] { "alpha" };

        double before = scorer.Explain("1", terms, index).Parameters["averageVerboseness"];

        index.Add(new SearchDocument("3", "xxxxxxx xxxxxxx xxxxxxx xxxxxxx xxxxxxx xxxxxxx y"));

        double after = scorer.Explain("1", terms, index).Parameters["averageVerboseness"];

        Assert.True(after > before, "a maximally verbose document must raise the elite mean");
    }

    [Fact]
    public void ADocumentAddedAfterThePassIsMeasuredRatherThanServedStale()
    {
        var index = Index(("1", "alpha beta gamma delta"));
        var scorer = new Bm25VaScorer(1.5, b: 0.0);

        // Warm the table over the single document, then add a second one. The table is rebuilt on
        // the next call because the corpus size moved; the assertion is that the new document is
        // still scored from its own verboseness rather than from the adjacency of the rebuild.
        scorer.Score("1", ["alpha"], index);
        index.Add(new SearchDocument("2", "alpha alpha alpha beta"));

        double verbose = scorer.Score("2", ["beta"], index);
        double spread = scorer.Score("1", ["beta"], index);

        Assert.True(verbose < spread, "2 has verboseness 4/3 against 1's 1.0");
    }

    // ---- the interface promises ----------------------------------------------------------------

    [Fact]
    public void ADocumentSharingNoQueryTermScoresZero()
    {
        var index = Corpus();
        var scorer = new Bm25VaScorer();

        Assert.Equal(0, scorer.Score("5", ["quick", "brown"], index));
        Assert.Equal("BM25VA", scorer.Name);
    }

    [Fact]
    public void AnEmptyIndexScoresZero()
    {
        var index = new InMemoryTextIndex();
        var scorer = new Bm25VaScorer();

        Assert.Equal(0, scorer.Score("missing", ["alpha"], index));
        Assert.Equal(0, scorer.Explain("missing", ["alpha"], index).TotalScore);
    }

    [Theory]
    [InlineData(-1, 0.5)]
    [InlineData(double.NaN, 0.5)]
    [InlineData(1.5, -0.1)]
    [InlineData(1.5, 1.1)]
    public void RejectsParametersOutsideTheirDomain(double k1, double b)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Bm25VaScorer(k1, b));
    }

    [Fact]
    public void ScoresThroughTheRankedEngine()
    {
        var index = Corpus();
        var engine = new RankedTextSearchEngine(index, new Bm25VaScorer(1.5, 0.5), index.Tokenizer);

        var results = engine.Search("quick brown", new SearchOptions(Limit: 3));

        Assert.NotEmpty(results);
        Assert.All(results, result => Assert.True(double.IsFinite(result.Score)));
    }

    private static double AverageVerboseness(InMemoryTextIndex index)
    {
        double total = 0;
        int documents = 0;

        foreach (var document in index.Documents)
        {
            int length = index.DocumentLength(document.Id);

            if (length == 0)
                continue;

            total += length / (double)index.GetTerms(document.Id).Distinct(StringComparer.Ordinal).Count();
            documents++;
        }

        return documents == 0 ? 0 : total / documents;
    }
}
