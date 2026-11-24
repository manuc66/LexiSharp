using LexiSharp.Classification;
using LexiSharp.Core;
using LexiSharp.Expansion;
using Xunit;

namespace LexiSharp.Tests;

public class ReviewFixesTests
{
    [Fact]
    public void Conformal_NaNAlpha_Throws()
    {
        var inner = new NaiveBayesClassifier();
        inner.Train(new[] { new SearchDocument("1", "alpha", Category: "A") });
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ConformalClassifier<NaiveBayesClassifier>(inner, alpha: double.NaN));
    }

    [Fact]
    public void TemporalPrior_NaNAlpha_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new TemporalPrior(Array.Empty<ObservedAssignment>(), blockHours: 3, alpha: double.NaN));
    }

    [Fact]
    public void Conformal_NonConformityScores_CannotBeMutated()
    {
        var inner = new NaiveBayesClassifier();
        inner.Train(new[] { new SearchDocument("1", "alpha", Category: "A") });
        var calib = new[]
        {
            new SearchDocument("c1", "alpha", Category: "A"),
            new SearchDocument("c2", "alpha", Category: "A"),
        };
        var conformal = new ConformalClassifier<NaiveBayesClassifier>(inner, calib, alpha: 0.10, minCalibrationSize: 1);
        double original = conformal.NonConformityScores[0];

        if (conformal.NonConformityScores is double[] mutable)
        {
            mutable[0] = 123.0;
        }

        Assert.Equal(original, conformal.NonConformityScores[0]);
    }

    [Fact]
    public void CorrectingTextClassifier_NoChange_PredictsOnce()
    {
        var counting = new CountingClassifier();
        counting.Train(new[] { new SearchDocument("1", "hello", Category: "A") });
        var corrector = new PassthroughTermCorrector();
        var ranker = new CorrectingTextClassifier(counting, corrector, new[] { "hello" });

        _ = ranker.Predict("hello", limit: 1);
        Assert.Equal(1, counting.Calls);
    }

    [Fact]
    public void EntityExtractor_NullPattern_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            new EntityExtractor(new IEntityPattern[] { null! }));
    }

    private sealed class CountingClassifier : ITextClassifier
    {
        private readonly NaiveBayesClassifier _inner = new();
        public int Calls { get; private set; }
        public void Train(IEnumerable<SearchDocument> documents) => _inner.Train(documents);
        public IReadOnlyList<ClassificationResult> Predict(string text, int limit = 3, IReadOnlySet<string>? excludedCategories = null)
        {
            Calls++;
            return _inner.Predict(text, limit, excludedCategories);
        }
        public string? PredictBest(string text, IReadOnlySet<string>? excludedCategories = null) => _inner.PredictBest(text, excludedCategories);
    }
}
