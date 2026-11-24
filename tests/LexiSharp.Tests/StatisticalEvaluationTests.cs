using LexiSharp.Classification;
using LexiSharp.Core;
using Xunit;

namespace LexiSharp.Tests;

public class StatisticalEvaluationTests
{
    [Fact]
    public void Conformal_Coverage_IsCloseToNominal()
    {
        const double alpha = 0.10;
        var rng = new Random(42);
        var distributions = new Dictionary<string, ClassificationResult[]>();
        var calibration = new List<SearchDocument>();
        var test = new List<SearchDocument>();

        for (int i = 0; i < 2200; i++)
        {
            double p = 0.55 + rng.NextDouble() * 0.40;
            bool isA = rng.NextDouble() < p;
            string text = "x" + i;
            distributions[text] = new[]
            {
                new ClassificationResult("A", p),
                new ClassificationResult("B", 1 - p),
            };
            var doc = new SearchDocument(text, text, Category: isA ? "A" : "B");
            if (i < 200) calibration.Add(doc);
            else test.Add(doc);
        }

        var inner = new FixedDistributionClassifier(distributions);
        var conformal = new ConformalClassifier<FixedDistributionClassifier>(
            inner, calibration, alpha: alpha, minCalibrationSize: 100);

        Assert.True(conformal.IsCalibrated);

        int covered = 0;
        foreach (var doc in test)
        {
            var set = conformal.PredictSet(doc.Text);
            if (set.Categories.Contains(doc.Category!)) covered++;
        }

        double coverage = covered / (double)test.Count;
        Assert.InRange(coverage, 1 - alpha - 0.08, 1 - alpha + 0.08);
    }

    [Fact]
    public void AccuracyGate_SwapsOnATie_ButSignificanceGateDoesNot()
    {
        var current = new ConstantClassifier("A");
        var candidate = new ConstantClassifier("B");
        var validation = Enumerable.Range(0, 100)
            .Select(i => new SearchDocument("v" + i, "v" + i, Category: i % 2 == 0 ? "A" : "B"))
            .ToArray();

        var adaptive = new AdaptiveClassifier(current);
        Assert.True(adaptive.TrySwap(candidate, validation));
        Assert.Same(candidate, adaptive.Current);

        var adaptive2 = new AdaptiveClassifier(current);
        Assert.False(adaptive2.TrySwapSignificant(candidate, validation));
        Assert.Same(current, adaptive2.Current);
    }

    private sealed class FixedDistributionClassifier : ITextClassifier
    {
        private readonly IReadOnlyDictionary<string, ClassificationResult[]> _distributions;
        public FixedDistributionClassifier(IReadOnlyDictionary<string, ClassificationResult[]> distributions) => _distributions = distributions;
        public void Train(IEnumerable<SearchDocument> documents) { }
        public IReadOnlyList<ClassificationResult> Predict(string text, int limit = 3, IReadOnlySet<string>? excludedCategories = null) =>
            _distributions.TryGetValue(text, out var distribution) ? distribution.Take(limit).ToList() : Array.Empty<ClassificationResult>();
        public string? PredictBest(string text, IReadOnlySet<string>? excludedCategories = null) =>
            Predict(text, limit: 1).FirstOrDefault()?.Category;
    }

    private sealed class ConstantClassifier : ITextClassifier
    {
        private readonly string _category;
        public ConstantClassifier(string category) => _category = category;
        public void Train(IEnumerable<SearchDocument> documents) { }
        public IReadOnlyList<ClassificationResult> Predict(string text, int limit = 3, IReadOnlySet<string>? excludedCategories = null) =>
            new[] { new ClassificationResult(_category, 1.0) };
        public string? PredictBest(string text, IReadOnlySet<string>? excludedCategories = null) => _category;
    }
}
