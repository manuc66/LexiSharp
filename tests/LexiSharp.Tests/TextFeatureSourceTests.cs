using LexiSharp.Classification;
using LexiSharp.Core;
using Xunit;

namespace LexiSharp.Tests;

public class TextFeatureSourceTests
{
    [Fact]
    public void Bigrams_IncludeUnigramsAndAdjacentPairs()
    {
        var source = new TextFeatureSource(maxNgram: 2);
        IFeatureSet features = source.FeaturesOf(new SearchDocument("1", "alpha beta gamma"));

        Assert.Contains("alpha", features.Features);
        Assert.Contains("beta", features.Features);
        Assert.Contains("2:alpha beta", features.Features);
        Assert.Contains("2:beta gamma", features.Features);
        Assert.DoesNotContain("2:alpha gamma", features.Features);
    }

    [Fact]
    public void TermFrequency_CountsRepeatedBigrams()
    {
        var source = new TextFeatureSource(emitTermFrequency: true, maxNgram: 2);
        IFeatureSet features = source.FeaturesOf(new SearchDocument("1", "alpha beta alpha beta"));

        Assert.Equal(2, features.NumericValue("2:alpha beta")!.Value, 12);
        Assert.Equal(1, features.NumericValue("2:beta alpha")!.Value, 12);
    }

    [Fact]
    public void Trigrams_StopAtTheDocumentEnd()
    {
        var source = new TextFeatureSource(maxNgram: 3);
        IFeatureSet features = source.FeaturesOf(new SearchDocument("1", "alpha beta gamma"));

        Assert.Contains("3:alpha beta gamma", features.Features);
        Assert.DoesNotContain("3:beta gamma", features.Features);
    }

    [Fact]
    public void MaxNgram_ZeroThrows()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TextFeatureSource(maxNgram: 0));
    }
}
