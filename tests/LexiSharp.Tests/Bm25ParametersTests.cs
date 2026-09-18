using LexiSharp.Core;
using LexiSharp.Indexing;
using LexiSharp.Ranking;
using Xunit;

namespace LexiSharp.Tests;

public class Bm25ParametersTests
{
    [Fact]
    public void Balanced_MatchesTheDocumentedDefaultProfile()
    {
        Assert.Equal(1.5, Bm25Parameters.Balanced.K1);
        Assert.Equal(0.75, Bm25Parameters.Balanced.B);
    }

    [Fact]
    public void Aggressive_StrengthensSaturationAndLengthNormalization()
    {
        Assert.Equal(2.0, Bm25Parameters.Aggressive.K1);
        Assert.Equal(1.0, Bm25Parameters.Aggressive.B);
    }

    [Fact]
    public void Conservative_RelaxesSaturationAndLengthNormalization()
    {
        Assert.Equal(1.0, Bm25Parameters.Conservative.K1);
        Assert.Equal(0.5, Bm25Parameters.Conservative.B);
    }

    [Fact]
    public void Scorer_DefaultsToTheBalancedProfile()
    {
        var index = new InMemoryTextIndex();
        index.Add(new SearchDocument("1", "alpha beta"));
        var query = new[] { "alpha", "beta" };

        double fromPreset = new Bm25Scorer(Bm25Parameters.Balanced).Score("1", query, index);
        double fromDefault = new Bm25Scorer().Score("1", query, index);

        Assert.Equal(fromPreset, fromDefault, 12);
    }

    [Fact]
    public void Scorer_EquivalenceWithExplicitPair()
    {
        var index = new InMemoryTextIndex();
        index.Add(new SearchDocument("1", "alpha beta alpha gamma"));
        var query = new[] { "alpha" };

        double fromParameters = new Bm25Scorer(new Bm25Parameters(1.2, 0.5)).Score("1", query, index);
        double fromPair = new Bm25Scorer(k1: 1.2, b: 0.5).Score("1", query, index);

        Assert.Equal(fromPair, fromParameters, 12);
    }

    [Fact]
    public void Scorer_RejectsNullOrInvalidParameters()
    {
        Assert.Throws<ArgumentNullException>(() => new Bm25Scorer(parameters: null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Bm25Scorer(new Bm25Parameters(-0.5, 0.75)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Bm25Scorer(new Bm25Parameters(1.5, 1.5)));
    }
}