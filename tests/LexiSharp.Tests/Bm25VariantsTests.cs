using LexiSharp.Core;
using LexiSharp.Indexing;
using LexiSharp.Ranking;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// Covers <see cref="Bm25PlusScorer"/> and <see cref="Bm25LScorer"/>. The property that pins both to
/// the literature is the degeneration check: at <c>delta = 0</c> each must reduce to
/// <see cref="Bm25Scorer"/>, which is the only way to be sure the published formula was transcribed
/// correctly rather than merely plausible.
/// </summary>
public class Bm25VariantsTests
{
    private static InMemoryTextIndex Corpus() =>
        Index(
            ("1", "the quick brown fox jumps over the lazy dog"),
            ("2", "quick brown foxes are quick and brown and quick"),
            ("3", "a lazy dog sleeps all day long in the sun"),
            ("4", "quick thinking saved the day for the whole team"),
            ("5", "nothing in this document relates to the query at all"));

    private static InMemoryTextIndex Index(params (string Id, string Text)[] documents)
    {
        var index = new InMemoryTextIndex();
        index.Index(documents.Select(document => new SearchDocument(document.Id, document.Text)));
        return index;
    }

    // ---- the two tests that can actually catch a wrong transcription -------------------------------
    //
    // The delta = 0 degeneration test below is necessary but NOT sufficient: it pins the saturation
    // and says nothing about WHERE delta sits or WHICH denominator is used, because every wrong
    // variant I have written collapses onto BM25 at delta = 0. Verified by reverting each formula
    // and confirming these two fail while the degeneration test does not.

    [Theory]
    [InlineData(1.5, 0.75)]
    [InlineData(0.9, 0.4)]
    [InlineData(2.0, 1.0)]
    [InlineData(0.0, 0.0)]
    public void Bm25PlusAtDeltaZeroIsExactlyBm25(double k1, double b)
    {
        var index = Corpus();
        var plus = new Bm25PlusScorer(k1, b, delta: 0);
        var bm25 = new Bm25Scorer(k1, b);

        foreach (string query in new[] { "quick", "quick brown", "lazy dog", "nothing here" })
        {
            var terms = index.Tokenizer.Tokenize(query);

            foreach (var document in index.Documents)
            {
                Assert.Equal(
                    bm25.Score(document.Id, terms, index),
                    plus.Score(document.Id, terms, index),
                    12);
            }
        }
    }

    [Fact]
    public void Bm25LUsesTheCompressedDenominatorNotBm25s()
    {
        // The test that discriminates between BM25L and a mis-transcription of it. The correct
        // denominator is (k1 + ctd + delta); the wrong one is BM25's (k1 * norm + tf). The two agree
        // at delta = 0, because the compression cancels there — which is exactly why this test needs
        // a non-zero delta, and why Bm25LAtDeltaZeroIsExactlyBm25 cannot be the one that pins the
        // shape.
        //
        //   correct: idf * (k1+1) * (ctd + delta) / (k1 + ctd + delta)
        //   wrong  : idf * (k1+1) * (ctd + delta) / (k1 * norm + tf)
        var index = Index(("1", "alpha beta gamma"), ("2", "alpha"), ("3", "gamma delta"));
        var terms = index.Tokenizer.Tokenize("alpha beta");

        const double k1 = 1.5, b = 0.75, delta = 0.5;
        var scorer = new Bm25LScorer(k1, b, delta);

        double norm = 1.0 - b + b * index.DocumentLength("1") / index.AverageDocumentLength;

        // Both query terms match document "1", so the score is the sum over them.
        double Expected(bool compressedDenominator)
        {
            double total = 0;

            foreach (string term in terms)
            {
                double tf = index.TermFrequency("1", term);
                double ctd = tf / norm;
                int df = index.DocumentFrequency(term);
                double idf = Math.Log(1.0 + (index.Count - df + 0.5) / (df + 0.5));

                double bottom = compressedDenominator
                    ? k1 + ctd + delta   // the published form
                    : k1 * norm + tf;    // BM25's denominator, i.e. the mis-transcription

                total += idf * (k1 + 1) * (ctd + delta) / bottom;
            }

            return total;
        }

        double correct = Expected(compressedDenominator: true);
        double wrong = Expected(compressedDenominator: false);

        Assert.Equal(correct, scorer.Score("1", terms, index), 12);
        Assert.NotEqual(wrong, correct, 9);
    }

    [Fact]
    public void Bm25PlusAddsDeltaOutsideTheFraction()
    {
        // Same discipline for BM25+. Delta is an additive floor on the whole term weight, not a
        // shift of tf inside the fraction. The degeneration test at delta = 0 cannot tell these
        // apart — both collapse onto BM25 — which is exactly why it missed the error.
        //
        //   correct: idf * (tf*(k1+1) / (tf + k1*norm) + delta)
        //   wrong  : idf * (k1+1) * (tf + delta) / (k1*norm + tf + delta)
        var index = Index(("1", "alpha beta gamma"), ("2", "alpha"), ("3", "gamma delta"));
        var terms = index.Tokenizer.Tokenize("alpha beta");

        const double k1 = 1.5, b = 0.75, delta = 1.0;
        var scorer = new Bm25PlusScorer(k1, b, delta);

        double norm = 1.0 - b + b * index.DocumentLength("1") / index.AverageDocumentLength;

        double Expected(bool deltaOutside)
        {
            double total = 0;

            foreach (string term in terms)
            {
                double tf = index.TermFrequency("1", term);
                int df = index.DocumentFrequency(term);
                double idf = Math.Log(1.0 + (index.Count - df + 0.5) / (df + 0.5));

                total += deltaOutside
                    ? idf * (tf * (k1 + 1) / (tf + k1 * norm) + delta)
                    : idf * (k1 + 1) * (tf + delta) / (k1 * norm + tf + delta);
            }

            return total;
        }

        double correct = Expected(deltaOutside: true);
        double wrong = Expected(deltaOutside: false);

        Assert.Equal(correct, scorer.Score("1", terms, index), 12);
        Assert.NotEqual(wrong, correct, 9);
    }

    [Fact]
    public void Bm25PlusAtDeltaIsExactlyBm25PlusIdfTimesDelta()
    {
        // Follows from delta being additive: the whole difference from BM25 must be
        // delta * sum(idf over the matched terms), with no coupling to tf at all.
        var index = Index(("1", "alpha beta alpha gamma"), ("2", "alpha"), ("3", "gamma delta"));
        var terms = index.Tokenizer.Tokenize("alpha beta");

        const double k1 = 1.5, b = 0.75, delta = 0.75;
        var bm25 = new Bm25Scorer(k1, b);
        var plus = new Bm25PlusScorer(k1, b, delta);

        foreach (var document in index.Documents)
        {
            double idfSum = 0;

            foreach (string term in terms)
            {
                if (document.Id is not null && index.TermFrequency(document.Id, term) > 0)
                {
                    int df = index.DocumentFrequency(term);
                    idfSum += Math.Log(1.0 + (index.Count - df + 0.5) / (df + 0.5));
                }
            }

            Assert.Equal(
                bm25.Score(document.Id, terms, index) + idfSum * delta,
                plus.Score(document.Id, terms, index),
                12);
        }
    }

    [Theory]
    [InlineData(1.5, 0.75)]
    [InlineData(0.9, 0.4)]
    [InlineData(2.0, 1.0)]
    [InlineData(0.0, 0.0)]
    public void Bm25LAtDeltaZeroIsExactlyBm25(double k1, double b)
    {
        // BM25L degenerates to BM25 at delta = 0, and not by accident of the corpus: the compression
        // cancels, because it is applied to the numerator as well as the denominator.
        //
        //   BM25L:  ctd / (k1 + ctd)          with ctd = tf / norm
        //         = (tf / norm) / (k1 + tf / norm)
        //         = tf / (k1 * norm + tf)    ... multiplying top and bottom by norm
        //   BM25:   tf (k1 + 1) / (tf + k1 * norm)
        //
        // The two term weights are the same function, so the ranking is the same at delta = 0 for
        // any (k1, b). This corrects an earlier claim in this file — that BM25L "is not a
        // degeneration of BM25 at delta 0" because its denominator carries the compressed
        // frequency. It does, and BM25's numerator does not need compensating: the compression
        // appears on both sides and cancels.
        //
        // It matters beyond tidiness. It is what lets Bm25LParameterTuner's delta = 0 grid report
        // Bm25ParameterTuner's own score, so a BM25L result can be read against a BM25 result at
        // all, and it is why delta is the ONLY thing that distinguishes these two variants.
        var index = Corpus();
        var scorer = new Bm25LScorer(k1, b, delta: 0);
        var bm25 = new Bm25Scorer(k1, b);

        foreach (string query in new[] { "quick", "quick brown", "lazy dog", "nothing here" })
        {
            var terms = index.Tokenizer.Tokenize(query);

            foreach (var document in index.Documents)
            {
                Assert.Equal(
                    bm25.Score(document.Id, terms, index),
                    scorer.Score(document.Id, terms, index),
                    12);
            }
        }
    }

    // ---- the documented departure: score 0 for no shared term -------------------------------------

    [Fact]
    public void ADocumentSharingNoQueryTermScoresExactlyZero()
    {
        var index = Corpus();
        var terms = index.Tokenizer.Tokenize("quick");

        foreach (ITextScorer scorer in new ITextScorer[]
                 {
                     new Bm25PlusScorer(delta: 1.0),
                     new Bm25LScorer(delta: 0.5),
                 })
        {
            // The gate that keeps the « 0 means no match » convention: a non-zero delta must not
            // give an unrelated document a score just because delta exists.
            Assert.Equal(0, scorer.Score("5", terms, index));
            Assert.IsAssignableFrom<ITermOverlapScorer>(scorer);
        }
    }

    [Fact]
    public void ADeltaNeverMakesAnUnrelatedDocumentMatch()
    {
        var index = Index(("1", "alpha beta"), ("2", "gamma delta"));
        var terms = index.Tokenizer.Tokenize("alpha");

        foreach (double delta in new[] { 0.0, 0.5, 1.0, 100.0 })
        {
            Assert.Equal(0, new Bm25PlusScorer(delta: delta).Score("2", terms, index));
            Assert.Equal(0, new Bm25LScorer(delta: delta).Score("2", terms, index));
        }
    }

    // ---- the variants actually differ from BM25 ---------------------------------------------------

    [Fact]
    public void ANonZeroDeltaChangesTheScore()
    {
        var index = Corpus();
        var terms = index.Tokenizer.Tokenize("quick");

        double bm25 = new Bm25Scorer().Score("1", terms, index);

        Assert.NotEqual(bm25, new Bm25PlusScorer(delta: 1.0).Score("1", terms, index), 12);
        Assert.NotEqual(bm25, new Bm25LScorer(delta: 0.5).Score("1", terms, index), 12);
    }

    [Fact]
    public void Bm25PlusRewardsCoordinationOverRepetition()
    {
        // The effect the paper is after: '2' has 'quick' three times but no other query term, '4'
        // has one 'quick' and one other. A lower bound on tf should favour breadth over repetition
        // relative to plain BM25, which saturates repetition hard.
        var index = Index(
            ("repeats", "quick quick quick quick"),
            ("covers", "quick and brown and lazy and dog"));

        var terms = index.Tokenizer.Tokenize("quick brown");
        var bm25 = new Bm25Scorer();
        var plus = new Bm25PlusScorer(delta: 1.0);

        double bm25Ratio = bm25.Score("repeats", terms, index) / bm25.Score("covers", terms, index);
        double plusRatio = plus.Score("repeats", terms, index) / plus.Score("covers", terms, index);

        // BM25+ must shrink the gap between repetition and coverage: that is the coordination
        // effect. A ratio at or below BM25's would mean the variant is not doing its job.
        Assert.True(plusRatio < bm25Ratio,
            $"BM25+ ratio {plusRatio} should be below BM25's {bm25Ratio}");
    }

    // ---- plan parity, explainability ---------------------------------------------------------------

    [Fact]
    public void APlannedSearchIsBitIdenticalForBothVariants()
    {
        var index = Corpus();

        (ITextScorer Scorer, string Query)[] cases =
        [
            (new Bm25PlusScorer(1.4, 0.6, 0.8), "quick brown"),
            (new Bm25LScorer(1.4, 0.6, 0.8), "quick brown"),
            (new Bm25PlusScorer(), "lazy"),
            (new Bm25LScorer(), "nothing here"),
        ];

        foreach ((ITextScorer scorer, string query) in cases)
        {
            var terms = index.Tokenizer.Tokenize(query);
            var plan = ((IQueryPlannableScorer)scorer).CreatePlan(terms, index);

            foreach (var document in index.Documents)
            {
                Assert.Equal(scorer.Score(document.Id, terms, index), plan.Score(document.Id), 15);
            }
        }
    }

    [Fact]
    public void TheExplanationSumsBackToTheScoreAndReportsDelta()
    {
        var index = Corpus();
        var terms = index.Tokenizer.Tokenize("quick brown");

        foreach (ITextScorer scorer in new ITextScorer[]
                 {
                     new Bm25PlusScorer(1.3, 0.7, 0.9),
                     new Bm25LScorer(1.3, 0.7, 0.9),
                 })
        {
            var explainer = (IScoreExplainer)scorer;

            foreach (var document in index.Documents)
            {
                var explanation = explainer.Explain(document.Id, terms, index);

                Assert.Equal(scorer.Score(document.Id, terms, index), explanation.TotalScore, 12);
                Assert.Equal(explanation.TotalScore, explanation.Terms.Sum(t => t.Score), 12);
                Assert.Equal(0.9, explanation.Parameters["delta"]);
            }
        }
    }

    [Fact]
    public void AnAbsentTermIsOmittedFromTheExplanation()
    {
        var index = Corpus();
        var scorer = new Bm25PlusScorer();

        var explanation = scorer.Explain("1", index.Tokenizer.Tokenize("quick absent"), index);

        Assert.Equal("quick", Assert.Single(explanation.Terms).Term);
    }

    [Fact]
    public void BothAreNamedDistinctly()
    {
        Assert.Equal("BM25+", new Bm25PlusScorer().Name);
        Assert.Equal("BM25L", new Bm25LScorer().Name);
    }

    [Fact]
    public void ArgumentValidation()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Bm25PlusScorer(k1: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Bm25PlusScorer(b: 1.5));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Bm25PlusScorer(delta: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Bm25PlusScorer(delta: double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Bm25PlusScorer(delta: double.PositiveInfinity));

        Assert.Throws<ArgumentOutOfRangeException>(() => new Bm25LScorer(k1: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Bm25LScorer(b: 1.5));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Bm25LScorer(delta: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Bm25LScorer(delta: double.NaN));
    }

    [Fact]
    public void AnEmptyIndexAndAnUnknownDocumentScoreZero()
    {
        var index = Corpus();
        var terms = index.Tokenizer.Tokenize("quick");

        Assert.Equal(0, new Bm25PlusScorer().Score("nope", terms, index));
        Assert.Equal(0, new Bm25LScorer().Score("nope", terms, index));

        var empty = new InMemoryTextIndex();
        Assert.Equal(0, new Bm25PlusScorer().Score("whatever", terms, empty));
        Assert.Equal(0, new Bm25LScorer().Score("whatever", terms, empty));
    }

    [Fact]
    public void BothWorkThroughTheRankedEngine()
    {
        var index = Corpus();

        foreach (ITextScorer scorer in new ITextScorer[] { new Bm25PlusScorer(), new Bm25LScorer() })
        {
            var engine = new RankedTextSearchEngine(index, scorer);
            var results = engine.Search("quick brown");

            // Documents 1, 2 and 4 match. Document 4 carries only 'quick', which is enough: these
            // scorers sum over the query terms a document does contain, so matching one term of two
            // still scores. Document 3 has neither.
            Assert.Equal(3, results.Count);
            Assert.All(results, result => Assert.True(result.Score > 0));
            Assert.DoesNotContain(results, result => result.DocumentId == "3");
            Assert.DoesNotContain(results, result => result.DocumentId == "5");
        }
    }
}
