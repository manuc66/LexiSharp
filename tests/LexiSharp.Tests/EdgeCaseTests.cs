using LexiSharp.Classification;
using LexiSharp.Core;
using LexiSharp.Linguistics;
using Xunit;

namespace LexiSharp.Tests;

public class EdgeCaseTests
{
    // --- Tokenizer.Normalize ---
    [Fact]
    public void Normalize_Idempotent()
    {
        string[] inputs = { "Cœur", "Æ", "ẞ", "café", "résumé", "", "Hello World" };
        foreach (string input in inputs)
        {
            string once = Tokenizer.Normalize(input);
            string twice = Tokenizer.Normalize(once);
            Assert.Equal(once, twice);
        }
    }

    [Fact]
    public void Normalize_UppercaseLigatures()
    {
        Assert.Equal("oe", Tokenizer.Normalize("Œ"));
        Assert.Equal("ae", Tokenizer.Normalize("Æ"));
        Assert.Equal("ss", Tokenizer.Normalize("ẞ"));
    }

    [Fact]
    public void Normalize_Empty()
    {
        Assert.Equal(string.Empty, Tokenizer.Normalize(string.Empty));
    }

    // --- SignificanceTests ---
    [Fact]
    public void Wilcoxon_SizeOne_ReturnsOne()
    {
        Assert.Equal(1.0, LexiSharp.Core.SignificanceTests.WilcoxonSignedRank(new[] { 1.0 }, new[] { 1.0 }), 3);
    }

    [Fact]
    public void Wilcoxon_AllZeroDiffs_ReturnsOne()
    {
        Assert.Equal(1.0, LexiSharp.Core.SignificanceTests.WilcoxonSignedRank(new[] { 1.0, 2.0 }, new[] { 1.0, 2.0 }), 3);
    }

    [Fact]
    public void Wilcoxon_MismatchedLengths_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            LexiSharp.Core.SignificanceTests.WilcoxonSignedRank(new[] { 1.0 }, new[] { 1.0, 2.0 }));
    }

    [Fact]
    public void PairedBootstrap_ZeroResamples_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            LexiSharp.Core.SignificanceTests.PairedBootstrap(new[] { 1.0 }, new[] { 2.0 }, 0));
    }

    // --- EntityExtractor ---
    [Fact]
    public void EntityExtractor_EmptyText_ReturnsEmpty()
    {
        var extractor = new EntityExtractor(new[] { GenericEntityPatterns.Url });
        Assert.Empty(extractor.Extract(string.Empty).Entities);
    }

    [Fact]
    public void EntityExtractor_OverlappingSpans_KeepsOne()
    {
        var extractor = new EntityExtractor(new IEntityPattern[] { GenericEntityPatterns.Url, GenericEntityPatterns.Domain });
        var result = extractor.Extract("http://example.com");
        // No two entities may overlap.
        for (int i = 0; i < result.Entities.Count; i++)
            for (int j = i + 1; j < result.Entities.Count; j++)
            {
                var a = result.Entities[i];
                var b = result.Entities[j];
                bool overlap = a.Start < b.Start + b.Length && b.Start < a.Start + a.Length;
                Assert.False(overlap);
            }
    }

    // --- ConformalClassifier ---
    [Fact]
    public void Conformal_MinCalibSizeZero_EmptyCalib_NoThrow()
    {
        var inner = new NaiveBayesClassifier();
        inner.Train(new[] { new SearchDocument("1", "alpha", Category: "A") });
        var conformal = new ConformalClassifier<NaiveBayesClassifier>(inner, Array.Empty<SearchDocument>(), alpha: 0.10, minCalibrationSize: 0);
        // Should not throw.
        _ = conformal.PredictSet("alpha");
    }

    [Fact]
    public void Conformal_PredictSet_SubsetOfPredict()
    {
        var inner = new NaiveBayesClassifier();
        inner.Train(new[] { new SearchDocument("1", "alpha", Category: "A"), new SearchDocument("2", "beta", Category: "B") });
        var calib = new[]
        {
            new SearchDocument("c1", "alpha", Category: "A"),
            new SearchDocument("c2", "beta", Category: "B"),
        };
        var conformal = new ConformalClassifier<NaiveBayesClassifier>(inner, calib, alpha: 0.10, minCalibrationSize: 1);
        var predicted = conformal.Predict("alpha", limit: int.MaxValue).Select(r => r.Category).ToHashSet();
        var set = conformal.PredictSet("alpha");
        Assert.All(set.Categories, c => Assert.Contains(c, predicted));
    }

    // --- VocabularyOverlapGate ---
    [Fact]
    public void VocabularyOverlapGate_EmptyVocab_FiltersAll()
    {
        var inner = new NaiveBayesClassifier();
        inner.Train(new[] { new SearchDocument("1", "alpha", Category: "A") });
        var gate = new VocabularyOverlapGate(inner, Tokenizer.Default, new Dictionary<string, HashSet<string>>());
        Assert.Empty(gate.Predict("alpha", limit: 5));
    }

    // --- TemporalPrior ---
    [Fact]
    public void TemporalPrior_InvalidHour_Throws()
    {
        var prior = new TemporalPrior(Array.Empty<ObservedAssignment>());
        Assert.Throws<ArgumentOutOfRangeException>(() => prior.Probabilities(DayOfWeek.Monday, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => prior.Probabilities(DayOfWeek.Monday, 24));
    }

    [Fact]
    public void TemporalPrior_EmptyHistory_EmptyProbabilities()
    {
        var prior = new TemporalPrior(Array.Empty<ObservedAssignment>());
        Assert.Empty(prior.Probabilities(DayOfWeek.Monday, 9));
    }
}
