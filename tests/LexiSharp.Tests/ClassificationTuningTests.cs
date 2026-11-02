using LexiSharp.Classification;
using LexiSharp.Core;
using LexiSharp.Eval;
using Xunit;

namespace LexiSharp.Tests;

public class ClassificationTuningTests
{
    private static ClassificationDocument Doc(string id, string category) =>
        new(id, "text " + id, category);

    private static IReadOnlyList<ClassificationDocument> Corpus()
    {
        var documents = new List<ClassificationDocument>();

        foreach (string category in new[] { "A", "B", "C" })
        {
            for (int i = 0; i < 5; i++)
                documents.Add(Doc($"{category}-{i}", category));
        }

        return documents;
    }

    [Fact]
    public void HoldOut_IsDeterministic()
    {
        var first = ClassificationEvaluation.HoldOut(Corpus(), validationFraction: 0.2, seed: 7);
        var second = ClassificationEvaluation.HoldOut(Corpus(), validationFraction: 0.2, seed: 7);

        Assert.Equal(
            first.Fit.Select(document => document.Id).OrderBy(id => id, StringComparer.Ordinal),
            second.Fit.Select(document => document.Id).OrderBy(id => id, StringComparer.Ordinal));
        Assert.Equal(
            first.Validation.Select(document => document.Id).OrderBy(id => id, StringComparer.Ordinal),
            second.Validation.Select(document => document.Id).OrderBy(id => id, StringComparer.Ordinal));
    }

    [Fact]
    public void HoldOut_IsDisjointAndCoversEveryDocument()
    {
        var corpus = Corpus();
        var split = ClassificationEvaluation.HoldOut(corpus, validationFraction: 0.2, seed: 7);

        var all = split.Fit.Concat(split.Validation).Select(document => document.Id).ToList();

        Assert.Equal(corpus.Count, all.Count);
        Assert.Equal(corpus.Count, all.Distinct(StringComparer.Ordinal).Count());
        Assert.Empty(split.Fit.Select(document => document.Id).Intersect(
            split.Validation.Select(document => document.Id), StringComparer.Ordinal));
    }

    [Fact]
    public void HoldOut_PreservesEveryCategory()
    {
        var split = ClassificationEvaluation.HoldOut(Corpus(), validationFraction: 0.2, seed: 7);

        foreach (string category in new[] { "A", "B", "C" })
        {
            Assert.Contains(split.Fit, document => document.Category == category);
            Assert.Contains(split.Validation, document => document.Category == category);
        }
    }

    [Fact]
    public void HoldOut_WithASingleDocumentCategory_KeepsItInFit()
    {
        var split = ClassificationEvaluation.HoldOut(
            new[] { Doc("solo", "S"), Doc("a", "A"), Doc("b", "A") },
            validationFraction: 0.5,
            seed: 7);

        Assert.Contains(split.Fit, document => document.Id == "solo");
        Assert.DoesNotContain(split.Validation, document => document.Id == "solo");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    public void HoldOut_WithAnInvalidFraction_Throws(double fraction)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ClassificationEvaluation.HoldOut(Corpus(), fraction, seed: 7));
    }

    [Fact]
    public void SelectBest_PicksTheHighestScore()
    {
        var candidates = new (string Candidate, double Score)[]
        {
            ("a", 0.5),
            ("b", 0.7),
            ("c", 0.6),
        };

        string best = ClassificationEvaluation.SelectBest(candidates, candidate => candidate);

        Assert.Equal("b", best);
    }

    [Fact]
    public void SelectBest_BreaksATieByTheOrdinalKey()
    {
        var candidates = new (string Candidate, double Score)[]
        {
            ("b", 0.7),
            ("a", 0.7),
            ("c", 0.5),
        };

        string best = ClassificationEvaluation.SelectBest(candidates, candidate => candidate);

        Assert.Equal("a", best);
    }

    [Fact]
    public void TuneForest_ReturnsTheCandidateWithTheBestValidationAccuracy()
    {
        var documents = new List<ClassificationDocument>();

        foreach (string category in new[] { "A", "B", "C" })
        {
            for (int i = 0; i < 12; i++)
                documents.Add(new ClassificationDocument($"{category}-{i}", $"{category} term{i % 4}", category));
        }

        var split = ClassificationEvaluation.HoldOut(documents, validationFraction: 0.25, seed: 7);
        var candidates = new[]
        {
            new ForestCandidate(MaxDepth: 2, FeatureSubset: 0.5, MinDocumentsPerLeaf: 1),
            new ForestCandidate(MaxDepth: 8, FeatureSubset: 0.5, MinDocumentsPerLeaf: 1),
        };
        var baseOptions = new RandomForestOptions
        {
            Trees = 5,
            MinDocumentFrequency = 1,
            Seed = 7,
        };

        var tuned = ClassificationEvaluation.TuneForest(split.Fit, split.Validation, candidates, baseOptions);

        var scores = candidates
            .Select(candidate => (
                Candidate: candidate,
                Score: ClassificationEvaluation.MeasureRandomForest(
                    split.Fit,
                    split.Validation,
                    baseOptions with
                    {
                        MaxDepth = candidate.MaxDepth,
                        FeatureSubsetFraction = candidate.FeatureSubset,
                        MinDocumentsPerLeaf = candidate.MinDocumentsPerLeaf,
                    }).Accuracy))
            .ToArray();

        Assert.Equal(scores.Max(score => score.Score), tuned.ValidationAccuracy, precision: 12);
        Assert.Contains(
            scores,
            score => score.Candidate == tuned.Candidate
                && Math.Abs(score.Score - tuned.ValidationAccuracy) < 1e-12);
    }

    [Fact]
    public void TuneLinear_ChoosesTheBestValidationCandidate_AndIsDeterministic()
    {
        var split = ClassificationEvaluation.HoldOut(Corpus(), validationFraction: 0.4, seed: 7);
        var candidates = new[]
        {
            new LinearCandidate(5, 0.1, 1e-4),
            new LinearCandidate(5, 0.2, 1e-3),
        };
        var baseOptions = new LinearClassifierOptions { MinDocumentFrequency = 1 };

        Func<LinearClassifierOptions, ITextClassifier> factory =
            options => new LinearClassifier(options: options);

        var first = ClassificationEvaluation.TuneLinear(
            split.Fit, split.Validation, candidates, baseOptions, factory);
        var second = ClassificationEvaluation.TuneLinear(
            split.Fit, split.Validation, candidates, baseOptions, factory);

        Assert.Equal(first.Candidate, second.Candidate);

        var scores = candidates
            .Select(candidate => (
                Candidate: candidate,
                Score: ClassificationEvaluation.MeasureClassifier(
                    "Linear",
                    split.Fit,
                    split.Validation,
                    () => factory(baseOptions with
                    {
                        Epochs = candidate.Epochs,
                        LearningRate = candidate.LearningRate,
                        L2 = candidate.L2,
                    }),
                    confidence: false).Accuracy))
            .ToArray();

        Assert.Equal(scores.Max(score => score.Score), first.ValidationAccuracy, precision: 12);
        Assert.Contains(scores, score => score.Candidate == first.Candidate);
    }

    [Fact]
    public void SelectBest_WithNoCandidates_Throws()
    {
        Assert.Throws<ArgumentException>(
            () => ClassificationEvaluation.SelectBest(Array.Empty<(string, double)>(), candidate => candidate));
    }
}
