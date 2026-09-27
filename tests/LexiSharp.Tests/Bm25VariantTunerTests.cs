using LexiSharp.Core;
using LexiSharp.Indexing;
using LexiSharp.Ranking;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// Covers <see cref="Bm25PlusParameterTuner"/> and <see cref="Bm25LParameterTuner"/>. Both exist so the
/// variant-vs-BM25 tables can be tuned-against-tuned: the variants carry a free δ that
/// <see cref="Bm25ParameterTuner"/> cannot search, and leaving it at the paper's starting value
/// while fitting BM25's (k1, b) is not a comparison of the formulas.
/// </summary>
/// <remarks>
/// The two are covered together because they are the same search over different scorers, and
/// duplicating the whole battery twice would test the copy. Where they genuinely differ — the
/// <c>delta = 0</c> baseline — the difference is asserted explicitly rather than glossed.
/// </remarks>
public class Bm25VariantTunerTests
{
    private static readonly double[] Deltas = [0.0, 0.25, 0.5, 0.75, 1.0];

    /// <summary>
    /// A corpus where δ is the only thing that can fix the ranking, which is what a test about a δ
    /// search needs.
    /// </summary>
    /// <remarks>
    /// The query is two terms of equal idf — <c>quick</c> and <c>brown</c> each appear in exactly two
    /// of the five documents — so no term is accidentally worth more than the other.
    /// <c>covers</c> holds both once; <c>repeats</c> holds one twenty times. With no lower bound
    /// <c>repeats</c> wins on raw term frequency, and any δ &gt; 0 hands the top slot to
    /// <c>covers</c>, because the bound is paid per distinct term matched. A δ tuner that ignored δ
    /// would report a perfect score here without ever ranking the relevant document first.
    /// </remarks>
    private static InMemoryTextIndex DeltaSensitiveCorpus()
    {
        var index = new InMemoryTextIndex();
        index.Index(new[]
        {
            new SearchDocument("covers", "quick brown"),
            new SearchDocument("repeats", string.Join(" ", Enumerable.Repeat("quick", 20))),
            new SearchDocument("brownish", "zulu brown"),
            new SearchDocument("noise1", "tango sierra"),
            new SearchDocument("noise2", "india oscar"),
        });
        return index;
    }

    /// <summary>Three documents, none of them relevant, so every configuration scores the same.</summary>
    private static InMemoryTextIndex FlatCorpus()
    {
        var index = new InMemoryTextIndex();
        index.Index(new[]
        {
            new SearchDocument("1", "alpha beta"),
            new SearchDocument("2", "alpha beta"),
            new SearchDocument("3", "alpha beta"),
        });
        return index;
    }

    private static Bm25ValidationQuery[] DeltaValidationSet() => [new("quick brown", ["covers"])];

    // ---- the axis the tuner adds: δ -----------------------------------------------------------------

    [Fact]
    public void Bm25PlusFindsADeltaThatFlipsTheTopResult()
    {
        // k1 and b are pinned so δ is the only variable. Left free, the search finds k1 = 0.5,
        // b = 0, where even δ = 0 ranks the relevant document first — a perfect score the δ axis
        // never had to contribute to.
        var result = new Bm25PlusParameterTuner(DeltaSensitiveCorpus(), DeltaValidationSet())
            .Tune(k1Values: [1.2], bValues: [0.0], deltaValues: Deltas, topK: 1);

        Assert.True(result.DeltaHelped, "a delta exists that ranks the relevant document first");
        Assert.True(result.Delta > 0, $"the winner must carry a lower bound, got {result.Delta}");
        Assert.Equal(1, result.MetricScore, 12);

        // The proof that δ was load-bearing rather than incidental: the same (k1, b) with no bound
        // gets it wrong.
        Assert.Equal(0, result.Grid.Single(g => g.Delta == 0).MetricScore, 12);
        Assert.Equal(0, result.UnflooredMetricScore, 12);

        var engine = new RankedTextSearchEngine(
            DeltaSensitiveCorpus(), new Bm25PlusScorer(result.K1, result.B, result.Delta));
        Assert.Equal("covers", engine.Search("quick brown", new SearchOptions(1)).Single().DocumentId);
    }

    [Fact]
    public void Bm25LFindsADeltaThatFlipsTheTopResult()
    {
        var result = new Bm25LParameterTuner(DeltaSensitiveCorpus(), DeltaValidationSet())
            .Tune(k1Values: [1.2], bValues: [0.0], deltaValues: Deltas, topK: 1);

        Assert.True(result.DeltaHelped, "a delta exists that ranks the relevant document first");
        Assert.True(result.Delta > 0, $"the winner must carry a lower bound, got {result.Delta}");
        Assert.Equal(1, result.MetricScore, 12);
        Assert.Equal(0, result.Grid.Single(g => g.Delta == 0).MetricScore, 12);

        var engine = new RankedTextSearchEngine(
            DeltaSensitiveCorpus(), new Bm25LScorer(result.K1, result.B, result.Delta));
        Assert.Equal("covers", engine.Search("quick brown", new SearchOptions(1)).Single().DocumentId);
    }

    [Fact]
    public void AGridWhereNoDeltaHelpsReportsSoRatherThanImprovingOnIt()
    {
        // Identical documents: every ranking is the same whatever δ is, so nothing beats no bound.
        // The useful answer from a tuner is often this one, and it has to be reportable.
        var validation = new[] { new Bm25ValidationQuery("alpha", ["1", "2", "3"]) };

        var plus = new Bm25PlusParameterTuner(FlatCorpus(), validation).Tune();
        var l = new Bm25LParameterTuner(FlatCorpus(), validation).Tune();

        foreach ((double Delta, double Score, double Unfloored, bool Helped) winner in new[]
                 {
                     (plus.Delta, plus.MetricScore, plus.UnflooredMetricScore, plus.DeltaHelped),
                     (l.Delta, l.MetricScore, l.UnflooredMetricScore, l.DeltaHelped),
                 })
        {
            Assert.False(winner.Helped, "no delta can beat no delta when the corpus is uniform");
            Assert.Equal(0, winner.Delta, 12);
            Assert.Equal(winner.Unfloored, winner.Score, 12);
        }
    }

    [Fact]
    public void AFlatValidationSetTiesEveryDeltaSoTheGridShowsWhyDeltaHelpedIsFalse()
    {
        // DeltaHelped == false has two causes that look identical from the boolean alone, and the
        // reference corpus runs into the second one: a validation set too flat to separate the
        // formulas, where many configurations tie and delta = 0 is simply the first the tie-break
        // reaches. A test that only asserted the boolean would pass either way, and a reader taking
        // it as "the bound does not help" would be over-reading it.
        //
        // Here every configuration ties at a perfect score, so the grid itself has to show that
        // delta is not what separated the winner.
        var validation = new[] { new Bm25ValidationQuery("alpha", ["1", "2", "3"]) };
        var plus = new Bm25PlusParameterTuner(FlatCorpus(), validation).Tune(k1Values: [1.2], topK: 1);

        Assert.False(plus.DeltaHelped);

        // Every delta in the grid reaches the maximum, so the winner's delta = 0 is a tie-break
        // artefact rather than a finding.
        double[] deltasAtMaximum = plus.Grid
            .Where(g => g.MetricScore == plus.MetricScore)
            .Select(g => g.Delta)
            .Distinct()
            .OrderBy(d => d)
            .ToArray();

        Assert.Equal(Deltas, deltasAtMaximum);
    }

    [Fact]
    public void OmittingZeroFromTheDeltaGridLeavesTheBaselineUndefined()
    {
        // With no δ = 0 point there is nothing to compare against, and the honest answer is « I did
        // not measure that » rather than a fabricated pass.
        var plus = new Bm25PlusParameterTuner(DeltaSensitiveCorpus(), DeltaValidationSet())
            .Tune(k1Values: [1.2], bValues: [0.0], deltaValues: [0.25, 0.5, 1.0], topK: 1);
        var l = new Bm25LParameterTuner(DeltaSensitiveCorpus(), DeltaValidationSet())
            .Tune(k1Values: [1.2], bValues: [0.0], deltaValues: [0.25, 0.5, 1.0], topK: 1);

        Assert.True(double.IsNaN(plus.UnflooredMetricScore));
        Assert.True(double.IsNaN(l.UnflooredMetricScore));
        Assert.False(plus.DeltaHelped, "nothing was compared, so nothing helped");
        Assert.False(l.DeltaHelped);
    }

    // ---- the δ = 0 baseline is BM25, which is what makes these tables readable ----------------------

    [Fact]
    public void AZeroDeltaGridReproducesTheBm25ParameterTunerExactly()
    {
        // The cross-check that ties the three tuners together. Both variants degenerate to
        // Bm25Scorer at delta = 0 — BM25+'s trivially, BM25L's because the compression appears on
        // both sides of its fraction and cancels — so a delta = 0 grid must return
        // Bm25ParameterTuner's winner and its score, to the last digit, for both.
        var index = DeltaSensitiveCorpus();
        var validation = new Bm25ValidationQuery[]
        {
            new("quick brown", ["covers"]),
            new("tango", ["noise1"]),
            new("india", ["noise2"]),
        };
        double[] k1 = [0.5, 1.2, 2.0];
        double[] b = [0.0, 0.5, 0.75];

        foreach (TuningMetric metric in new[] { TuningMetric.F1, TuningMetric.Ndcg })
        {
            var reference = new Bm25ParameterTuner(index, validation).Tune(k1, b, topK: 3, metric: metric);

            var plus = new Bm25PlusParameterTuner(index, validation).Tune(k1, b, [0.0], topK: 3, metric: metric);
            var l = new Bm25LParameterTuner(index, validation).Tune(k1, b, [0.0], topK: 3, metric: metric);

            foreach ((string Name, double K1, double B, double Score, double Unfloored) variant in new[]
                     {
                         ("BM25+", plus.K1, plus.B, plus.MetricScore, plus.UnflooredMetricScore),
                         ("BM25L", l.K1, l.B, l.MetricScore, l.UnflooredMetricScore),
                     })
            {
                Assert.Equal(reference.Parameters.K1, variant.K1, 12);
                Assert.Equal(reference.Parameters.B, variant.B, 12);
                Assert.Equal(reference.MetricScore, variant.Score, 12);
                Assert.Equal(reference.MetricScore, variant.Unfloored, 12);
            }
        }
    }

    [Fact]
    public void TheFullDeltaGridStillContainsTheBm25Grid()
    {
        // The corollary of the above, and the reason a δ search can be read as a BM25 search: every
        // (k1, b) point is also evaluated at δ = 0, so the best of the δ = 0 slice is BM25's own
        // tuned score. A tuner that dropped the zero slice could report a tuned BM25+ score with no
        // BM25 to compare it against.
        var index = DeltaSensitiveCorpus();
        var validation = DeltaValidationSet();

        var reference = new Bm25ParameterTuner(index, validation).Tune(topK: 1, metric: TuningMetric.F1);
        var plus = new Bm25PlusParameterTuner(index, validation).Tune(topK: 1, metric: TuningMetric.F1);
        var l = new Bm25LParameterTuner(index, validation).Tune(topK: 1, metric: TuningMetric.F1);

        Assert.True(plus.MetricScore >= plus.UnflooredMetricScore);
        Assert.True(l.MetricScore >= l.UnflooredMetricScore);
        Assert.Equal(plus.Grid.Count, l.Grid.Count);
        Assert.True(plus.Grid.Count > reference.Grid.Count,
            "the delta axis is a strict addition to the (k1, b) grid, not a replacement for it");
    }

    // ---- grid shape, determinism, accounting ------------------------------------------------------

    [Fact]
    public void TuneUsesTheDefaultGridWhenNoGridIsSupplied()
    {
        var plus = new Bm25PlusParameterTuner(DeltaSensitiveCorpus(), DeltaValidationSet()).Tune();
        var l = new Bm25LParameterTuner(DeltaSensitiveCorpus(), DeltaValidationSet()).Tune();

        // 5 k1 x 5 b x 5 delta.
        Assert.Equal(125, plus.Grid.Count);
        Assert.Equal(125, l.Grid.Count);
        Assert.Equal(125, plus.EvaluatedConfigurations);
        Assert.Equal(125, l.EvaluatedConfigurations);
        Assert.Equal(TuningMetric.F1, plus.Metric);
        Assert.Equal(10, plus.TopK);
        Assert.Contains(plus.Grid, g => g.K1 == 1.5 && g.B == 0.75 && g.Delta == 1.0);

        // The no-bound point must be in the default grid, or DeltaHelped has nothing to compare
        // against and the tuner cannot report whether δ earned its place.
        Assert.Contains(plus.Grid, g => g.Delta == 0);
        Assert.Contains(l.Grid, g => g.Delta == 0);
    }

    [Fact]
    public void TuneReturnsTheFullGridInAscendingOrder()
    {
        var result = new Bm25PlusParameterTuner(DeltaSensitiveCorpus(), DeltaValidationSet())
            .Tune(k1Values: [0.5, 1.0], bValues: [0.0, 0.5, 1.0], deltaValues: [0.0, 0.5, 1.0]);

        Assert.Equal(18, result.Grid.Count);

        for (int i = 1; i < result.Grid.Count; i++)
        {
            var previous = result.Grid[i - 1];
            var current = result.Grid[i];

            Assert.True(
                previous.K1 < current.K1 ||
                (previous.K1 == current.K1 && previous.B < current.B) ||
                (previous.K1 == current.K1 && previous.B == current.B && previous.Delta < current.Delta),
                "the grid must be evaluated in ascending (k1, b, delta) order");
        }
    }

    [Fact]
    public void TuneBreaksTiesWithTheLowestGridPoint()
    {
        // Identical documents, so every point scores the same: the deterministic winner is the first
        // ascending (k1, b, delta) point, which is also the least aggressive one.
        var validation = new[] { new Bm25ValidationQuery("alpha", ["1", "2", "3"]) };
        double[] k1 = [1.0, 2.0];
        double[] b = [0.5, 1.0];
        double[] deltas = [0.0, 0.5, 1.0];

        var plus = new Bm25PlusParameterTuner(FlatCorpus(), validation).Tune(k1, b, deltas, topK: 10);
        var l = new Bm25LParameterTuner(FlatCorpus(), validation).Tune(k1, b, deltas, topK: 10);

        Assert.Equal(1.0, plus.K1, 12);
        Assert.Equal(0.5, plus.B, 12);
        Assert.Equal(0.0, plus.Delta, 12);
        Assert.Equal(1.0, l.K1, 12);
        Assert.Equal(0.5, l.B, 12);
        Assert.Equal(0.0, l.Delta, 12);

        Assert.Equal(plus.Grid.Max(g => g.MetricScore), plus.MetricScore, 12);
        Assert.Equal(l.Grid.Max(g => g.MetricScore), l.MetricScore, 12);
    }

    [Fact]
    public void TuneRecomputableMetricMatchesTheReportedScore()
    {
        var index = DeltaSensitiveCorpus();
        var validation = DeltaValidationSet();
        var result = new Bm25PlusParameterTuner(index, validation)
            .Tune(k1Values: [1.2], bValues: [0.0, 0.75], topK: 3, metric: TuningMetric.Ndcg);

        var engine = new RankedTextSearchEngine(index, new Bm25PlusScorer(result.K1, result.B, result.Delta));

        double recomputed = validation.Average(query =>
            RetrievalMetrics.NdcgAtK(
                engine.Search(query.Query, new SearchOptions(3)).Select(r => r.DocumentId).ToArray(),
                query.RelevantDocumentIds,
                3));

        Assert.Equal(recomputed, result.MetricScore, 12);
    }

    [Fact]
    public void ASearchAboveTheConfigurationCapIsRefusedWithTheCount()
    {
        // 3 k1 x 3 b x 3 delta = 27, asked for under a cap of 10. Refused rather than trimmed: a
        // silently shortened grid reports a number for a search nobody ran.
        var index = DeltaSensitiveCorpus();
        double[] axis = [0.0, 0.5, 1.0];

        var plusError = Assert.Throws<ArgumentException>(() =>
            new Bm25PlusParameterTuner(index, DeltaValidationSet())
                .Tune(axis, axis, axis, maxConfigurations: 10));

        var lError = Assert.Throws<ArgumentException>(() =>
            new Bm25LParameterTuner(index, DeltaValidationSet())
                .Tune(axis, axis, axis, maxConfigurations: 10));

        Assert.Contains("27", plusError.Message);
        Assert.Contains("27", lError.Message);

        // The same search is fine once the cap admits it.
        Assert.Equal(27, new Bm25PlusParameterTuner(index, DeltaValidationSet())
            .Tune(axis, axis, axis, maxConfigurations: 27).Grid.Count);
    }

    // ---- argument validation -----------------------------------------------------------------------

    [Fact]
    public void ConstructorRejectsEmptyOrMislabeledValidationSets()
    {
        var index = DeltaSensitiveCorpus();

        Assert.Throws<ArgumentException>(
            () => new Bm25PlusParameterTuner(index, Array.Empty<Bm25ValidationQuery>()));
        Assert.Throws<ArgumentException>(
            () => new Bm25LParameterTuner(index, Array.Empty<Bm25ValidationQuery>()));

        Assert.Throws<ArgumentException>(
            () => new Bm25PlusParameterTuner(index, [new Bm25ValidationQuery("quick", [])]));
        Assert.Throws<ArgumentException>(
            () => new Bm25LParameterTuner(index, [new Bm25ValidationQuery("quick", [])]));

        Assert.Throws<ArgumentNullException>(() => new Bm25PlusParameterTuner(index, null!));
        Assert.Throws<ArgumentNullException>(() => new Bm25LParameterTuner(index, null!));
        Assert.Throws<ArgumentNullException>(() => new Bm25PlusParameterTuner(null!, DeltaValidationSet()));
        Assert.Throws<ArgumentNullException>(() => new Bm25LParameterTuner(null!, DeltaValidationSet()));
    }

    [Fact]
    public void TuneValidatesGridsAndTopK()
    {
        var index = DeltaSensitiveCorpus();
        var plus = new Bm25PlusParameterTuner(index, DeltaValidationSet());
        var l = new Bm25LParameterTuner(index, DeltaValidationSet());

        foreach (Func<object> call in new Func<object>[]
                 {
                     () => plus.Tune(topK: 0),
                     () => plus.Tune(maxConfigurations: 0),
                     () => l.Tune(topK: 0),
                     () => l.Tune(maxConfigurations: -1),
                 })
        {
            Assert.Throws<ArgumentOutOfRangeException>(call);
        }

        foreach (double bad in new[] { -1.0, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => plus.Tune(k1Values: [bad]));
            Assert.Throws<ArgumentOutOfRangeException>(() => plus.Tune(bValues: [bad]));
            Assert.Throws<ArgumentOutOfRangeException>(() => plus.Tune(deltaValues: [bad]));
            Assert.Throws<ArgumentOutOfRangeException>(() => l.Tune(k1Values: [bad]));
            Assert.Throws<ArgumentOutOfRangeException>(() => l.Tune(bValues: [bad]));
            Assert.Throws<ArgumentOutOfRangeException>(() => l.Tune(deltaValues: [bad]));
        }

        foreach (double bad in new[] { -0.1, 1.5 })
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => plus.Tune(bValues: [bad]));
            Assert.Throws<ArgumentOutOfRangeException>(() => l.Tune(bValues: [bad]));
        }

        Assert.Throws<ArgumentException>(() => plus.Tune(k1Values: []));
        Assert.Throws<ArgumentException>(() => plus.Tune(bValues: []));
        Assert.Throws<ArgumentException>(() => plus.Tune(deltaValues: []));
        Assert.Throws<ArgumentException>(() => l.Tune(k1Values: []));
        Assert.Throws<ArgumentException>(() => l.Tune(bValues: []));
        Assert.Throws<ArgumentException>(() => l.Tune(deltaValues: []));
    }

    [Fact]
    public void TuneDoesNotMutateTheIndex()
    {
        var index = DeltaSensitiveCorpus();
        int documentsBefore = index.Count;
        long tokensBefore = index.CorpusTokenCount;

        new Bm25PlusParameterTuner(index, DeltaValidationSet()).Tune();
        new Bm25LParameterTuner(index, DeltaValidationSet()).Tune();

        Assert.Equal(documentsBefore, index.Count);
        Assert.Equal(tokensBefore, index.CorpusTokenCount);
    }
}
