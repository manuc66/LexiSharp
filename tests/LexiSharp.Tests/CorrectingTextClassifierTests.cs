using LexiSharp.Classification;
using LexiSharp.Core;
using LexiSharp.Linguistics;
using Xunit;

namespace LexiSharp.Tests;

public class CorrectingTextClassifierTests
{
    [Fact]
    public void CorrectsUnknownAlphabeticTerm()
    {
        var inner = new RecordingClassifier(new NaiveBayesClassifier());
        inner.Train(new[] { new SearchDocument("1", "hello", Category: "A") });
        var corrector = new DamerauLevenshteinTermCorrector(new[] { "hello" });
        var ranker = new CorrectingTextClassifier(inner, corrector, new[] { "hello" });

        _ = ranker.Predict("helo", limit: 3);
        Assert.Contains(inner.Inputs, input => input.Contains("hello", StringComparison.Ordinal));
    }

    [Fact]
    public void KnownTerm_Unchanged()
    {
        var inner = new NaiveBayesClassifier();
        inner.Train(new[] { new SearchDocument("1", "hello", Category: "A") });
        var corrector = new DamerauLevenshteinTermCorrector(new[] { "hello" });
        var ranker = new CorrectingTextClassifier(inner, corrector, new[] { "hello" });

        var results = ranker.Predict("hello", limit: 3);
        Assert.NotEmpty(results);
    }

    [Fact]
    public void IdentifierWithDigits_IsNotCorrected()
    {
        var inner = new RecordingClassifier(new NaiveBayesClassifier());
        inner.Train(new[] { new SearchDocument("1", "hello", Category: "A") });
        var corrector = new DamerauLevenshteinTermCorrector(new[] { "hello" });
        var ranker = new CorrectingTextClassifier(inner, corrector, new[] { "hello" });

        _ = ranker.Predict("IP1883", limit: 3);
        Assert.DoesNotContain(inner.Inputs, input => input.Contains("hello", StringComparison.Ordinal));
    }

    [Fact]
    public void PathSegment_IsNotCorrected()
    {
        var inner = new RecordingClassifier(new NaiveBayesClassifier());
        inner.Train(new[] { new SearchDocument("1", "hello", Category: "A") });
        var corrector = new DamerauLevenshteinTermCorrector(new[] { "hello" });
        var ranker = new CorrectingTextClassifier(inner, corrector, new[] { "other" });

        _ = ranker.Predict("~/src/helo", limit: 3);
        Assert.DoesNotContain(inner.Inputs, input => input.Contains("hello", StringComparison.Ordinal));
    }

    [Fact]
    public void CorrectionPreservesRestOfText()
    {
        var inner = new RecordingClassifier(new NaiveBayesClassifier());
        inner.Train(new[] { new SearchDocument("1", "hello", Category: "A") });
        var corrector = new DamerauLevenshteinTermCorrector(new[] { "hello" });
        var ranker = new CorrectingTextClassifier(inner, corrector, new[] { "hello" });

        _ = ranker.Predict("Helo!", limit: 3);
        Assert.Contains(inner.Inputs, input => input.Contains("Hello!", StringComparison.Ordinal));
    }

    private sealed class RecordingClassifier : ITextClassifier
    {
        private readonly ITextClassifier _inner;
        public RecordingClassifier(ITextClassifier inner) => _inner = inner;
        public List<string> Inputs { get; } = new();
        public void Train(IEnumerable<SearchDocument> documents) => _inner.Train(documents);
        public IReadOnlyList<ClassificationResult> Predict(string text, int limit = 3, IReadOnlySet<string>? excludedCategories = null)
        {
            Inputs.Add(text);
            return _inner.Predict(text, limit, excludedCategories);
        }
        public string? PredictBest(string text, IReadOnlySet<string>? excludedCategories = null) => _inner.PredictBest(text, excludedCategories);
    }
}
