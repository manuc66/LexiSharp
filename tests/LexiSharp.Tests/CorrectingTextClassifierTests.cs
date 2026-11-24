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
    public void IdentifierExclusion_CanBeDisabled()
    {
        var inner = new RecordingClassifier(new NaiveBayesClassifier());
        inner.Train(new[] { new SearchDocument("1", "hello", Category: "A") });
        var corrector = new DamerauLevenshteinTermCorrector(new[] { "hello" });
        var ranker = new CorrectingTextClassifier(
            inner, corrector, new[] { "other" }, excludeIdentifiers: false);

        _ = ranker.Predict("~/src/helo", limit: 3);
        Assert.Contains(inner.Inputs, input => input.Contains("hello", StringComparison.Ordinal));
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

    [Fact]
    public void OovTokenNearKnownTerm_IsCorrectedByDefault()
    {
        var inner = new RecordingClassifier(new NaiveBayesClassifier());
        inner.Train(new[] { new SearchDocument("1", "LegacyCaller", Category: "A") });
        var corrector = new DamerauLevenshteinTermCorrector(new[] { "LegacyCaller" });
        var ranker = new CorrectingTextClassifier(inner, corrector, new[] { "LegacyCaller" });

        _ = ranker.Predict("windowtracke", limit: 3);
        Assert.Contains(inner.Inputs, input => input.Contains("LegacyCaller", StringComparison.Ordinal));
    }

    [Fact]
    public void CustomGate_CanExcludeOovTokens()
    {
        var inner = new RecordingClassifier(new NaiveBayesClassifier());
        inner.Train(new[] { new SearchDocument("1", "LegacyCaller", Category: "A") });
        var corrector = new DamerauLevenshteinTermCorrector(new[] { "LegacyCaller" });
        var ranker = new CorrectingTextClassifier(
            inner, corrector, new[] { "LegacyCaller" }, correctable: _ => false);

        _ = ranker.Predict("windowtracke", limit: 3);
        Assert.DoesNotContain(inner.Inputs, input => input.Contains("LegacyCaller", StringComparison.Ordinal));
    }

    [Fact]
    public void OverlappingSpans_DoNotThrow()
    {
        var inner = new RecordingClassifier(new NaiveBayesClassifier());
        inner.Train(new[] { new SearchDocument("1", "hello", Category: "A") });
        var corrector = new DamerauLevenshteinTermCorrector(new[] { "hello" });
        var ranker = new CorrectingTextClassifier(inner, corrector, new[] { "hello" }, new OverlappingTokenizer());

        var results = ranker.Predict("helo", limit: 3);
        Assert.NotNull(results);
    }

    private sealed class OverlappingTokenizer : ISpanTokenizer
    {
        public IReadOnlyList<string> Tokenize(string text) => new[] { "helo" };
        public IReadOnlyList<TokenSpan> TokenizeWithSpans(string text) =>
            new[] { new TokenSpan("helo", 0, 4), new TokenSpan("elo", 1, 3) };
        public IReadOnlyList<TokenSpan> TokenizeWithSpans(ReadOnlySpan<char> text) => TokenizeWithSpans(text.ToString());
        public void TokenizeInto(ReadOnlySpan<char> text, List<string> destination) => destination.Add("helo");
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
