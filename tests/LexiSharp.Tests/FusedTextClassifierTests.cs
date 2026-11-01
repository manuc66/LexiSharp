using LexiSharp.Classification;
using LexiSharp.Core;
using LexiSharp.Embeddings;
using Xunit;

namespace LexiSharp.Tests;

public class FusedTextClassifierTests
{
    private static readonly SearchDocument[] Corpus =
    {
        new("1", "first document", Category: "A"),
        new("2", "second document", Category: "B"),
    };

    [Fact]
    public void Train_TrainsBothModelsOnTheSameMaterializedCorpus()
    {
        var first = new FixedClassifier();
        var second = new FixedClassifier();
        var classifier = new FusedTextClassifier(first, second);

        classifier.Train(Corpus);

        Assert.Equal(1, first.TrainCalls);
        Assert.Equal(1, second.TrainCalls);
        Assert.Equal(Corpus, first.Trained);
        Assert.Equal(Corpus, second.Trained);
    }

    [Fact]
    public void Predict_BlendsTheTwoDistributions()
    {
        var classifier = new FusedTextClassifier(
            new FixedClassifier(new ClassificationResult("A", 0.8), new ClassificationResult("B", 0.2)),
            new FixedClassifier(new ClassificationResult("A", 0.1), new ClassificationResult("B", 0.9)));

        var results = classifier.Predict("anything", limit: 2);

        Assert.Equal("B", results[0].Category);
        Assert.Equal(0.55, results[0].Probability, precision: 10);
        Assert.Equal(0.45, results[1].Probability, precision: 10);
    }

    [Fact]
    public void AWeightOfZero_ReturnsTheFirstModel()
    {
        var classifier = new FusedTextClassifier(
            new FixedClassifier(new ClassificationResult("A", 0.8), new ClassificationResult("B", 0.2)),
            new FixedClassifier(new ClassificationResult("A", 0.1), new ClassificationResult("B", 0.9)),
            secondWeight: 0);

        Assert.Equal("A", classifier.PredictBest("anything"));
    }

    [Fact]
    public void AWeightOfOne_ReturnsTheSecondModel()
    {
        var classifier = new FusedTextClassifier(
            new FixedClassifier(new ClassificationResult("A", 0.8), new ClassificationResult("B", 0.2)),
            new FixedClassifier(new ClassificationResult("A", 0.1), new ClassificationResult("B", 0.9)),
            secondWeight: 1);

        Assert.Equal("B", classifier.PredictBest("anything"));
    }

    [Fact]
    public void ACategoryOnlyOneModelKnowsStillCompetes()
    {
        var classifier = new FusedTextClassifier(
            new FixedClassifier(new ClassificationResult("A", 0.6), new ClassificationResult("B", 0.4)),
            new FixedClassifier(new ClassificationResult("A", 0.5), new ClassificationResult("C", 0.5)));

        var results = classifier.Predict("anything", limit: 3);

        Assert.Equal(new[] { "A", "C", "B" }, results.Select(result => result.Category));
        Assert.Equal(0.55, results[0].Probability, precision: 10);
        Assert.Equal(1.0, results.Sum(result => result.Probability), precision: 10);
    }

    [Fact]
    public void Predict_ExcludesFromBothModelsAndRenormalizes()
    {
        var classifier = new FusedTextClassifier(
            new FixedClassifier(new ClassificationResult("A", 0.6), new ClassificationResult("B", 0.4)),
            new FixedClassifier(new ClassificationResult("A", 0.5), new ClassificationResult("C", 0.5)),
            secondWeight: 0.8);

        var results = classifier.Predict(
            "anything",
            limit: 3,
            excludedCategories: new HashSet<string>(StringComparer.Ordinal) { "A" });

        Assert.Equal(new[] { "C", "B" }, results.Select(result => result.Category));
        Assert.Equal(1.0, results.Sum(result => result.Probability), precision: 10);
    }

    [Fact]
    public void Predict_ReturnsEmpty_WhenBothModelsReturnNothing()
    {
        var classifier = new FusedTextClassifier(new FixedClassifier(), new FixedClassifier());

        Assert.Empty(classifier.Predict("anything"));
        Assert.Null(classifier.PredictBest("anything"));
    }

    [Fact]
    public void Predict_UsesTheOtherModelWhenOneAbstains()
    {
        var classifier = new FusedTextClassifier(
            new FixedClassifier(),
            new FixedClassifier(new ClassificationResult("A", 0.7), new ClassificationResult("B", 0.3)),
            secondWeight: 0.2);

        var results = classifier.Predict("anything", limit: 2);

        Assert.Equal("A", results[0].Category);
        Assert.Equal(1.0, results.Sum(result => result.Probability), precision: 10);
    }

    [Fact]
    public void Predict_RespectsLimitAndOrdersTiesByCategoryName()
    {
        var classifier = new FusedTextClassifier(
            new FixedClassifier(new ClassificationResult("B", 0.5), new ClassificationResult("A", 0.5)),
            new FixedClassifier(new ClassificationResult("B", 0.5), new ClassificationResult("A", 0.5)));

        var results = classifier.Predict("anything", limit: 1);

        Assert.Single(results);
        Assert.Equal("A", results[0].Category);
    }

    [Fact]
    public void Predict_ReturnsEmpty_ForNonPositiveLimit()
    {
        var classifier = new FusedTextClassifier(
            new FixedClassifier(new ClassificationResult("A", 1.0)),
            new FixedClassifier(new ClassificationResult("A", 1.0)));

        Assert.Empty(classifier.Predict("anything", limit: 0));
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void TheSecondWeightMustBeInTheUnitInterval(double weight)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new FusedTextClassifier(new FixedClassifier(), new FixedClassifier(), weight));
    }

    [Fact]
    public void BothModelsAreRequired()
    {
        Assert.Throws<ArgumentNullException>(() => new FusedTextClassifier(null!, new FixedClassifier()));
        Assert.Throws<ArgumentNullException>(() => new FusedTextClassifier(new FixedClassifier(), null!));
        Assert.Throws<ArgumentNullException>(() =>
            new FusedTextClassifier(new FixedClassifier(), new FixedClassifier()).Train(null!));
    }

    [Fact]
    public void TheSecondWeightIsExposed()
    {
        var classifier = new FusedTextClassifier(new FixedClassifier(), new FixedClassifier(), 0.25);

        Assert.Equal(0.25, classifier.SecondWeight);
    }

    [Fact]
    public void ABlendOfNaiveBayesAndHashingCentroids_ClassifiesEndToEnd()
    {
        var documents = new[]
        {
            new SearchDocument("1", "invoice payment statement", Category: "Billing"),
            new SearchDocument("2", "refund and invoice question", Category: "Billing"),
            new SearchDocument("3", "server outage database error", Category: "Operations"),
            new SearchDocument("4", "database backup and outage", Category: "Operations"),
        };

        var classifier = new FusedTextClassifier(
            new NaiveBayesClassifier(),
            new CentroidClassifier(new HashingEmbeddingProvider(dimension: 1024)),
            secondWeight: 0.5);

        classifier.Train(documents);

        Assert.Equal("Billing", classifier.PredictBest("invoice"));
        Assert.Equal("Operations", classifier.PredictBest("database"));

        var results = classifier.Predict("invoice", limit: 2);
        Assert.Equal(1.0, results.Sum(result => result.Probability), precision: 10);
    }

    private sealed class FixedClassifier : ITextClassifier
    {
        private readonly IReadOnlyList<ClassificationResult> _results;

        public FixedClassifier(params ClassificationResult[] results) => _results = results;

        public int TrainCalls { get; private set; }

        public IReadOnlyList<SearchDocument> Trained { get; private set; } = Array.Empty<SearchDocument>();

        public void Train(IEnumerable<SearchDocument> documents)
        {
            TrainCalls++;
            Trained = documents.ToList();
        }

        public IReadOnlyList<ClassificationResult> Predict(
            string text,
            int limit = 3,
            IReadOnlySet<string>? excludedCategories = null)
        {
            var remaining = _results
                .Where(result => excludedCategories?.Contains(result.Category) != true)
                .Take(limit)
                .ToList();

            double total = remaining.Sum(result => result.Probability);

            if (remaining.Count == 0 || total <= 0)
                return Array.Empty<ClassificationResult>();

            return remaining
                .Select(result => new ClassificationResult(result.Category, result.Probability / total))
                .ToList();
        }

        public string? PredictBest(string text, IReadOnlySet<string>? excludedCategories = null)
        {
            var results = Predict(text, limit: 1, excludedCategories);
            return results.Count > 0 ? results[0].Category : null;
        }
    }
}
