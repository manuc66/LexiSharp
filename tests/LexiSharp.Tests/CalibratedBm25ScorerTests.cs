using LexiSharp.Core;
using LexiSharp.Indexing;
using LexiSharp.Ranking;
using Xunit;

namespace LexiSharp.Tests;

public class CalibratedBm25ScorerTests
{
    [Fact]
    public void Score_ReturnsProbabilityInZeroOne()
    {
        var index = new InMemoryTextIndex();
        index.Add(new SearchDocument("d1", "the quick brown fox"));
        index.Add(new SearchDocument("d2", "a lazy dog"));

        var inner = new Bm25Scorer();
        var calibrated = new CalibratedBm25Scorer(inner, a: -0.5, b: 0.0);

        double score = calibrated.Score("d1", new[] { "quick", "fox" }, index);

        Assert.InRange(score, 0, 1);
    }

    [Fact]
    public void Fit_ProducesACalibratedScorer()
    {
        var index = new InMemoryTextIndex();
        index.Add(new SearchDocument("d1", "the quick brown fox"));
        index.Add(new SearchDocument("d2", "a lazy dog"));

        var inner = new Bm25Scorer();
        var validation = new[]
        {
            ("quick fox", "d1", 1),
            ("lazy dog", "d2", 1),
            ("quick fox", "d2", 0),
        };

        var calibrated = CalibratedBm25Scorer.Fit(inner, validation, index, iterations: 50);

        double score = calibrated.Score("d1", new[] { "quick", "fox" }, index);
        Assert.InRange(score, 0, 1);
    }

    [Fact]
    public void Name_IncludesCalibrated()
    {
        var inner = new Bm25Scorer();
        var calibrated = new CalibratedBm25Scorer(inner, a: 0, b: 0);
        Assert.Contains("Calibrated", calibrated.Name);
    }
}