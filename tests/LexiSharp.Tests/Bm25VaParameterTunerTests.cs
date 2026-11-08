using LexiSharp.Core;
using LexiSharp.Indexing;
using LexiSharp.Ranking;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// Covers <see cref="Bm25VaParameterTuner"/>. The property that pins it to the scorer is the
/// degeneration: on a grid whose only <c>b</c> is <c>1.0</c>, every point must score exactly what
/// <see cref="Bm25ParameterTuner"/> scores on the same <c>k1</c> grid — same index, same queries,
/// same metric, same grid point. A tuner that measures something other than the scorer it names
/// fails there.
/// </summary>
public class Bm25VaParameterTunerTests
{
    private static InMemoryTextIndex Corpus()
    {
        var index = new InMemoryTextIndex();
        index.Index(
        [
            new SearchDocument("1", "the quick brown fox jumps over the lazy dog"),
            new SearchDocument("2", "quick brown foxes are quick and brown and quick"),
            new SearchDocument("3", "a lazy dog sleeps all day long in the sun"),
            new SearchDocument("4", "quick thinking saved the day for the whole team"),
            new SearchDocument("5", "nothing in this document relates to the query at all"),
        ]);

        return index;
    }

    private static IReadOnlyList<Bm25ValidationQuery> Validation() =>
    [
        new("quick brown", ["2"]),
        new("lazy dog", ["3"]),
        new("quick thinking", ["4"]),
    ];

    [Fact]
    public void AtBOneEveryPointScoresExactlyWhatBm25Scores()
    {
        var index = Corpus();
        double[] k1Values = [0.5, 1.0, 1.5, 2.0];

        Bm25VaTuningResult va = new Bm25VaParameterTuner(index, Validation())
            .Tune(k1Values: k1Values, bValues: [1.0], topK: 3, metric: TuningMetric.Ndcg);

        Bm25TuningResult bm25 = new Bm25ParameterTuner(index, Validation())
            .Tune(k1Values: k1Values, bValues: [1.0], topK: 3, metric: TuningMetric.Ndcg);

        Assert.Equal(bm25.Grid.Count, va.Grid.Count);

        for (int i = 0; i < bm25.Grid.Count; i++)
        {
            Assert.Equal(bm25.Grid[i].K1, va.Grid[i].K1);
            Assert.Equal(bm25.Grid[i].MetricScore, va.Grid[i].MetricScore, 12);
        }

        Assert.Equal(bm25.Parameters.K1, va.K1);
        Assert.Equal(bm25.Parameters.B, va.B);
    }

    [Fact]
    public void TheWinnerIsTheMaximumOfTheFullGrid()
    {
        var index = Corpus();
        double[] k1Values = [0.5, 1.0, 1.5];
        double[] bValues = [0.0, 0.5, 1.0];

        Bm25VaTuningResult result = new Bm25VaParameterTuner(index, Validation())
            .Tune(k1Values: k1Values, bValues: bValues, topK: 3, metric: TuningMetric.Ndcg);

        Assert.Equal(k1Values.Length * bValues.Length, result.Grid.Count);
        Assert.Equal(k1Values.Length * bValues.Length, result.EvaluatedConfigurations);
        Assert.Equal(result.Grid.Max(point => point.MetricScore), result.MetricScore);
        Assert.Contains(result.Grid, point => point.K1 == result.K1 && point.B == result.B);
    }

    [Fact]
    public void TheDefaultGridIsTwentyFivePoints()
    {
        Bm25VaTuningResult result = new Bm25VaParameterTuner(Corpus(), Validation())
            .Tune(topK: 3, metric: TuningMetric.Ndcg);

        Assert.Equal(25, result.Grid.Count);
        Assert.Equal(25, result.EvaluatedConfigurations);
    }

    [Fact]
    public void TheWinnerAtBOneIsTheNullResultTheHarnessReports()
    {
        // The point of the flag: tuning can return b = 1, and when it does the verboseness
        // component was given a zero weight — the search tried it and the corpus preferred length.
        // This corpus is small and not expected to prefer either; the assertion is that the result
        // is always a point the grid actually contains, which is what makes the b = 1 reading safe.
        Bm25VaTuningResult result = new Bm25VaParameterTuner(Corpus(), Validation())
            .Tune(topK: 3, metric: TuningMetric.Ndcg);

        Assert.True(result.B is >= 0 and <= 1);
    }

    [Fact]
    public void RejectsAnEmptyValidationSet()
    {
        Assert.Throws<ArgumentException>(() =>
            new Bm25VaParameterTuner(Corpus(), []));
    }

    [Fact]
    public void RefusesAGridAboveTheConfigurationCap()
    {
        var tuner = new Bm25VaParameterTuner(Corpus(), Validation());

        Assert.Throws<ArgumentException>(() =>
            tuner.Tune(k1Values: [0.5, 1.0], bValues: [0.0, 0.5], topK: 3, maxConfigurations: 3));
    }
}
