using LexiSharp.Core;
using LexiSharp.Hybrid;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// The mappings a score-blending merger puts each engine's scores through, and the arithmetic each
/// one implies.
/// </summary>
/// <remarks>
/// The values are hand-computed rather than captured from the implementation: a test that asserts
/// what the code already does pins drift, not meaning, and these are formula this file can state in
/// a comment above each assertion.
/// </remarks>
public class ScoreNormalizerTests
{
    private static SearchResult Scored(string id, double score) =>
        new(id, score, new SearchDocument(id, id));

    private static IReadOnlyList<SearchResult> List(params (string Id, double Score)[] scores) =>
        scores.Select(pair => Scored(pair.Id, pair.Score)).ToList();

    [Fact]
    public void Max_MeasuresTheLargestFiniteScore()
    {
        var normalization = new MaxScoreNormalizer().Measure(List(("a", 3), ("b", 7), ("c", 5)));

        Assert.Equal(0, normalization.Offset);
        Assert.Equal(7, normalization.Scale);
        Assert.Equal(1.0, normalization.Apply(7));
        Assert.Equal(3.0 / 7.0, normalization.Apply(3), 12);
    }

    [Fact]
    public void Max_IgnoresNonFiniteScores()
    {
        var normalization = new MaxScoreNormalizer().Measure(
            List(("a", double.NaN), ("b", double.PositiveInfinity), ("c", 2)));

        Assert.Equal(2, normalization.Scale);
    }

    [Fact]
    public void Max_HasNoScaleForAnEmptyOrUnmeasurableList()
    {
        Assert.Equal(ScoreNormalization.None, new MaxScoreNormalizer().Measure(List()));
        Assert.Equal(
            ScoreNormalization.None,
            new MaxScoreNormalizer().Measure(List(("a", double.NaN))));
    }

    [Fact]
    public void MinMax_ShiftsByTheSmallestAndDividesByTheSpread()
    {
        var normalization = new MinMaxScoreNormalizer().Measure(List(("a", 10), ("b", 6), ("c", 2)));

        Assert.Equal(2, normalization.Offset);
        Assert.Equal(8, normalization.Scale);
        Assert.Equal(1.0, normalization.Apply(10));
        Assert.Equal(0.5, normalization.Apply(6));
        Assert.Equal(0.0, normalization.Apply(2));
    }

    [Fact]
    public void MinMax_DegeneratesToTheTopScoreWhenTheListHasNoSpread()
    {
        // Dividing by a zero spread would drop every document from the blend; a list that scored the
        // same document(s) equally normalizes each of them to 1 instead, as the max normalizer does.
        var normalization = new MinMaxScoreNormalizer().Measure(List(("a", 4), ("b", 4)));

        Assert.Equal(0, normalization.Offset);
        Assert.Equal(4, normalization.Scale);
        Assert.Equal(1.0, normalization.Apply(4));
    }

    [Fact]
    public void MinMax_MeasuresOnlyPositiveScores()
    {
        // 0 is this library's "no match": letting it set the minimum would stretch a list down to a
        // value that means absence.
        var normalization = new MinMaxScoreNormalizer().Measure(List(("a", 9), ("miss", 0), ("b", 3)));

        Assert.Equal(3, normalization.Offset);
        Assert.Equal(6, normalization.Scale);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void MinMax_HasNoScaleWhenNothingPositiveWasReturned(double score)
    {
        Assert.Equal(ScoreNormalization.None, new MinMaxScoreNormalizer().Measure(List(("a", score))));
    }

    [Fact]
    public void Sum_DividesByTheTotalPositiveMass()
    {
        var normalization = new SumScoreNormalizer().Measure(List(("a", 4), ("b", 2), ("c", 0), ("d", -1)));

        Assert.Equal(0, normalization.Offset);
        Assert.Equal(6, normalization.Scale);
        Assert.Equal(4.0 / 6.0, normalization.Apply(4), 12);
    }

    [Fact]
    public void Sum_HasNoScaleWhenNothingPositiveWasReturned()
    {
        Assert.Equal(ScoreNormalization.None, new SumScoreNormalizer().Measure(List(("a", 0))));
    }

    [Fact]
    public void Apply_MapsAnUnmeasurableEngineToZeroRatherThanDividingByZero()
    {
        Assert.Equal(0, ScoreNormalization.None.Apply(12));
    }

    [Fact]
    public void WeightedMerge_DefaultNormalizerReproducesTheHistoricalScores()
    {
        // score = Σ s / max(s) over the engines that returned the document, weights 1.
        var merger = new WeightedScoreResultMerger();
        var merged = merger.Merge(
            [
                List(("x", 10), ("y", 5)),
                List(("x", 2), ("z", 1)),
            ],
            "q");

        // Engine one divides by 10, engine two by 2, so x — top of both — is 1 + 1.
        Assert.Equal(2.0, merged.Single(r => r.DocumentId == "x").Score, 12);
        Assert.Equal(0.5, merged.Single(r => r.DocumentId == "y").Score, 12);
        Assert.Equal(0.5, merged.Single(r => r.DocumentId == "z").Score, 12);
    }

    [Fact]
    public void WeightedMerge_MinMaxTreatsTheSpreadAsTheScale()
    {
        var merger = new WeightedScoreResultMerger(new MinMaxScoreNormalizer());

        var merged = merger.Merge(
            [
                List(("x", 10), ("y", 5)),
                List(("x", 2), ("z", 1)),
            ],
            "q");

        // x: 1 + 1 = 2 (top of both lists). y is the bottom of the first list, z of the second, and
        // min-max maps each list's bottom to 0 — so neither survives the merge's "score > 0" cut.
        Assert.Equal(("x", 2.0), (Assert.Single(merged).DocumentId, merged[0].Score));
    }

    [Fact]
    public void WeightedMerge_SumGivesEachEngineOneUnitOfMass()
    {
        var merger = new WeightedScoreResultMerger(new SumScoreNormalizer());

        var merged = merger.Merge(
            [
                List(("x", 10), ("y", 5)),
                List(("x", 2), ("z", 1)),
            ],
            "q");

        Assert.Equal((10.0 / 15.0) + (2.0 / 3.0), merged.Single(r => r.DocumentId == "x").Score, 12);
        Assert.Equal(5.0 / 15.0, merged.Single(r => r.DocumentId == "y").Score, 12);
    }

    [Fact]
    public void CombSum_DefaultNormalizerReproducesTheHistoricalScores()
    {
        var merged = new CombSumResultMerger().Merge(
            [
                List(("x", 10), ("y", 5)),
                List(("x", 2), ("z", 1)),
            ],
            "q");

        Assert.Equal(2.0, merged.Single(r => r.DocumentId == "x").Score, 12);
    }

    [Fact]
    public void CombSum_HonorsTheConfiguredNormalizer()
    {
        var merged = new CombSumResultMerger(new SumScoreNormalizer()).Merge(
            [
                List(("x", 10), ("y", 5)),
                List(("x", 2), ("z", 1)),
            ],
            "q");

        Assert.Equal(2.0 / 3.0 + 2.0 / 3.0, merged.Single(r => r.DocumentId == "x").Score, 12);
    }

    [Fact]
    public void CombMNZ_UsesTheSameNormalizerAsCombSum()
    {
        var normalizer = new SumScoreNormalizer();
        var sum = new CombSumResultMerger(normalizer).Merge(
            [List(("x", 10), ("y", 5)), List(("x", 2), ("z", 1))], "q");

        var mnz = new CombMNZResultMerger(normalizer).Merge(
            [List(("x", 10), ("y", 5)), List(("x", 2), ("z", 1))], "q");

        // x was found by both engines: its summed score is doubled; the others are found by one.
        Assert.Equal(sum.Single(r => r.DocumentId == "x").Score * 2, mnz.Single(r => r.DocumentId == "x").Score, 12);
        Assert.Equal(sum.Single(r => r.DocumentId == "y").Score, mnz.Single(r => r.DocumentId == "y").Score, 12);
    }

    [Fact]
    public void Explain_ReportsTheOffsetAndScaleOfTheConfiguredNormalizer()
    {
        var explanation = new WeightedScoreResultMerger(new MinMaxScoreNormalizer())
            .Explain([List(("x", 10), ("y", 5)), List(("x", 2))]);

        Assert.Equal([1.0, 1.0], explanation.Weights);
        Assert.Equal([5.0, 2.0], explanation.Normalization);
        Assert.Equal([5.0, 0.0], explanation.Offsets);
    }

    [Fact]
    public void Explain_ReportsNoOffsetsUnderTheDefaultNormalizer()
    {
        var explanation = new WeightedScoreResultMerger().Explain([List(("x", 10), ("y", 5))]);

        Assert.Equal([10.0], explanation.Normalization);
        Assert.Empty(explanation.Offsets);
    }

    [Fact]
    public void NameIsReportedForTheTrace()
    {
        Assert.Equal("max", new MaxScoreNormalizer().Name);
        Assert.Equal("minmax", new MinMaxScoreNormalizer().Name);
        Assert.Equal("sum", new SumScoreNormalizer().Name);

        Assert.Equal("max", new WeightedScoreResultMerger().NormalizationName);
        Assert.Equal("minmax", new WeightedScoreResultMerger(new MinMaxScoreNormalizer()).NormalizationName);
        Assert.Equal("sum", new WeightedScoreResultMerger(new SumScoreNormalizer()).NormalizationName);
    }

    [Fact]
    public void Normalizers_RejectANullList()
    {
        Assert.Throws<ArgumentNullException>(() => new MaxScoreNormalizer().Measure(null!));
        Assert.Throws<ArgumentNullException>(() => new MinMaxScoreNormalizer().Measure(null!));
        Assert.Throws<ArgumentNullException>(() => new SumScoreNormalizer().Measure(null!));
    }

    [Fact]
    public void Mergers_RejectANullNormalizer()
    {
        Assert.Throws<ArgumentNullException>(() => new WeightedScoreResultMerger(null!, 1.0));
        Assert.Throws<ArgumentNullException>(() => new CombSumResultMerger(null!));
        Assert.Throws<ArgumentNullException>(() => new CombMNZResultMerger(null!));
    }
}
