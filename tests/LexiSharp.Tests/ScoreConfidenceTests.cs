using LexiSharp.Core;
using LexiSharp.Ranking;
using Xunit;

namespace LexiSharp.Tests;

public class ScoreConfidenceTests
{
    [Fact]
    public void WinnerMargin_ClearWinnerIsConfident()
    {
        var confidence = ScoreConfidence.Compute(new double[] { 1000, 100, 99 });

        Assert.Equal(0.9, confidence[0], 9);
        Assert.Equal(0.01, confidence[1], 9);
        Assert.Equal(0, confidence[2]);

        // The last result never beats its (missing) follower.
        Assert.Equal(0, confidence[^1]);
    }

    [Fact]
    public void WinnerMargin_IsScaleInvariant()
    {
        var original = ScoreConfidence.Compute(new double[] { 1000, 100, 99 });
        var scaled = ScoreConfidence.Compute(new double[] { 1_000_000, 100_000, 99_000 });

        Assert.Equal(original[0], scaled[0], 9);
        Assert.Equal(original[1], scaled[1], 9);
    }

    [Fact]
    public void WinnerMargin_FlatScoresStayUnderAGate()
    {
        // The near-flat BM25 outputs the report flagged as "NoGoodMatch": every candidate
        // scores, none stands out, so the top stays far from any meaningful threshold.
        var confidence = ScoreConfidence.Compute(new double[] { 12000, 11800, 11600 });

        Assert.InRange(confidence[0], 0, 0.5);
        Assert.Equal(0, confidence[2]);
        Assert.Equal(confidence[0], ScoreConfidence.TopConfidence(new double[] { 12000, 11800, 11600 }));
    }

    [Fact]
    public void WinnerMargin_SingleResultIsConfident()
    {
        var confidence = ScoreConfidence.Compute(new double[] { 42 });

        Assert.Equal(1, confidence[0]);
        Assert.Equal(1, ScoreConfidence.TopConfidence(new double[] { 42 }));
    }

    [Fact]
    public void WinnerMargin_NonPositiveScoresAreNeverConfident()
    {
        // A zero/negative lead has nothing to be confident about: score 0 means "not a match".
        var confidence = ScoreConfidence.Compute(new double[] { 0, 5, 4 });

        Assert.Equal(0, confidence[0]);
        Assert.Equal(0.2, confidence[1], 9);

        // A genuine lead with a non-match behind is a full margin.
        Assert.Equal(1, ScoreConfidence.Compute(new double[] { 5, 0 })[0]);
    }

    [Fact]
    public void EmptySet_HasNoConfidence()
    {
        Assert.Empty(ScoreConfidence.Compute(Array.Empty<double>()));
        Assert.Equal(0, ScoreConfidence.TopConfidence(Array.Empty<double>()));
        Assert.Equal(0, ScoreConfidence.TopConfidence(Array.Empty<SearchResult>()));
    }

    [Fact]
    public void ZScore_FlatRankingCollapsesToNeutral()
    {
        var confidence = ScoreConfidence.Compute(new double[] { 100, 100, 100 }, ScoreConfidenceMethod.ZScore);

        Assert.All(confidence, value => Assert.Equal(0.5, value, 9));
    }

    [Fact]
    public void ZScore_StandoutLeaderIsMostConfident()
    {
        var confidence = ScoreConfidence.Compute(
            new double[] { 1000, 100, 100, 100, 100, 100, 100 },
            ScoreConfidenceMethod.ZScore);

        // One clear outlier lifts the leader and pushes the tied tail below the neutral 0.5.
        Assert.InRange(confidence[0], 0.7, 1.0);
        Assert.True(confidence[1] < 0.5);
        Assert.True(confidence[0] > confidence[1]);
    }

    [Fact]
    public void Compute_OverSearchResults_ProjectsScores()
    {
        var results = new[]
        {
            new SearchResult("a", 1000, new SearchDocument("a", "winner")),
            new SearchResult("b", 100, new SearchDocument("b", "runner up")),
        };

        var raw = ScoreConfidence.Compute(new double[] { 1000, 100 });
        var projected = ScoreConfidence.Compute(results);

        Assert.Equal(raw[0], projected[0], 9);
        Assert.Equal(raw[1], projected[1], 9);
        Assert.Equal(raw[0], ScoreConfidence.TopConfidence(results));
    }
}